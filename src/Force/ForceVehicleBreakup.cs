using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;

namespace Purr2D2.Force;

/// <summary>Incrementally capture a held vehicle, prepare durable data off-thread, then
/// construct its inert native replacements in bounded main-thread handoffs.</summary>
internal sealed class ForceVehicleBreakup : IDisposable
{
    private enum PreparationState { Capture, Worker, Register, CreateRoots, CheckColliders, CreateTree, Ready, Failed, Disposed }
    private sealed record PartSnapshot(Part Part, double4x4 AssemblyTransform, Part[] Children);
    private readonly record struct CaptureCandidate(Part Part, int ParentIndex, int FullPartIndex, bool IsFullPart,
        int FirstLayerBranchIndex, Part StructuralAnchor, bool IsAttachedInternal);
    private sealed record BackgroundPreparation(ForceBreakupPlanner.PlannedPiece[] Pieces,
        PreparedAuthoredDebrisAsset[] Assets, SiblingPair[] CandidatePairs);
    internal readonly record struct SiblingBounds(double3 Center, double Radius);
    internal readonly record struct SiblingPair(int First, int Second);

    internal const int DefaultMaximumPieces = ForceBreakupLimits.Default;
    private const int MaximumTopLevelParts = 4096;
    private const int MaximumCapturedParts = 32768;
    private const int MaximumSmallStepsPerHandoff = 8;
    private const double MainThreadBudgetMilliseconds = 1.5;
    private static int _backgroundWorkerActive;

    private readonly Vehicle _source;
    private readonly int _maximumPieces;
    private readonly int _mainThreadId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<ForceBreakupPlanner.PlannedPiece> _pieces = new();
    private readonly List<Part> _roots = new();
    private readonly List<PartSnapshot> _topology = new();
    private readonly List<ForceBreakupSourceNode> _sourceNodes = new();
    private readonly List<CaptureCandidate> _captureQueue = new();
    private readonly Dictionary<Part, int> _topPartIndices = new();
    private PartTree? _sourceTree;
    private IParentBody? _sourceParent;
    private CelestialSystem? _sourceSystem;
    private Part[]? _topParts;
    private int _topPartsCopied;
    private int _captureQueueIndex;
    private int _nextBranchIndex;
    private CaptureCandidate? _currentCandidate;
    private double4x4 _currentMatrix;
    private Part[]? _currentChildren;
    private int _childrenCopied;
    private int _childrenQueued;
    private bool _currentCaptured;
    private int _currentSnapshotIndex;
    private double _dryMass;
    private bool _rawSnapshotComplete;
    private bool _captureComplete;
    private bool _disposed;
    private PreparationState _state = PreparationState.Capture;
    private Exception? _failure;
    private string? _assetDirectory;
    private Task<BackgroundPreparation>? _workerTask;
    private PreparedAuthoredDebrisAsset[]? _preparedAssets;
    private SiblingPair[]? _candidatePairs;
    private PartTemplate?[]? _templates;
    private int _assetIndex;
    private IAuthoredDebrisRegistration? _registration;
    private int _rootIndex;
    private ColliderModule[]? _currentColliders;
    private int _colliderIndex;

    internal int Count => _captureComplete ? _pieces.Count : 0;
    internal bool CaptureComplete => _captureComplete;
    internal bool Ready => !_disposed && _state == PreparationState.Ready;
    internal bool Failed => _failure != null;
    internal string? Error => _failure?.Message;

    internal static bool SphereBoundsOverlap(double3 centerA, double radiusA, double3 centerB, double radiusB)
    {
        double radius = radiusA + radiusB;
        return (centerA - centerB).LengthSquared() <= radius * radius;
    }

    internal static SiblingPair[] BuildSiblingCandidates(SiblingBounds[] bounds, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var candidates = new List<SiblingPair>();
        for (int i = 0; i < bounds.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int j = i + 1; j < bounds.Length; j++)
            {
                if ((j & 31) == 0) cancel.ThrowIfCancellationRequested();
                if (SphereBoundsOverlap(bounds[i].Center, bounds[i].Radius, bounds[j].Center, bounds[j].Radius))
                    candidates.Add(new(i, j));
            }
        }
        return candidates.ToArray();
    }

    internal static bool NeedsBreakup(Vehicle source)
    {
        if (source is KittenEva) return false;
        // A generated debris root and its picking/render child are one physical object.
        if (source.Parts.Count == 1 && source.Parts.Root.Template.Id.StartsWith(AuthoredDebrisAssets.Prefix, StringComparison.Ordinal)) return false;
        if (source.Parts.Count > 1) return true;
        return HasMultipleVisibleNodes<Part>(source.Parts.Root, HasVisual,
            static part => part.SubParts.Length, static (part, index) => part.SubParts[index]);
    }

    internal static bool HasMultipleVisibleNodes<T>(T root, Func<T, bool> hasVisual,
        Func<T, int> childCount, Func<T, int, T> childAt)
    {
        var pending = new List<T> { root };
        int visible = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            if (hasVisual(pending[i]) && ++visible > 1) return true;
            int descendants = childCount(pending[i]);
            if (pending.Count + descendants > MaximumCapturedParts) return true;
            for (int j = 0; j < descendants; j++) pending.Add(childAt(pending[i], j));
        }
        return false;
    }

    /// <summary>Construction only records the source and thread identity. Capture starts in AdvancePreparation.</summary>
    internal ForceVehicleBreakup(Vehicle source) : this(source, DefaultMaximumPieces) { }

    internal ForceVehicleBreakup(Vehicle source, int maximumPieces)
    {
        _source = source;
        _maximumPieces = ForceBreakupLimits.Clamp(maximumPieces);
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>Advance no more than eight small operations or about 1.5 ms. An operation
    /// may exceed that soft budget; no second operation starts after the budget is spent.</summary>
    internal void AdvancePreparation(double budgetMilliseconds = MainThreadBudgetMilliseconds)
    {
        if (_disposed || Ready || _failure != null) return;
        EnsureMainThread();
        double budget = double.IsFinite(budgetMilliseconds) && budgetMilliseconds > 0
            ? Math.Min(budgetMilliseconds, MainThreadBudgetMilliseconds) : 0;
        if (budget <= 0 && _state != PreparationState.Worker) return;
        try
        {
            if (!IsSourceLive()) throw new InvalidOperationException("The held source is no longer available for breakup.");
            long started = Stopwatch.GetTimestamp();
            for (int steps = 0; steps < MaximumSmallStepsPerHandoff
                && (_state == PreparationState.Worker && budget <= 0
                    || Stopwatch.GetElapsedTime(started).TotalMilliseconds < budget); steps++)
            {
                switch (_state)
                {
                    case PreparationState.Capture:
                        AdvanceCaptureUnit();
                        break;
                    case PreparationState.Worker:
                        if (AdvanceWorker()) return; // A pending worker is polled without blocking.
                        break;
                    case PreparationState.Register:
                        AdvanceRegistration();
                        break;
                    case PreparationState.CreateRoots:
                        CreateOneRoot();
                        break;
                    case PreparationState.CheckColliders:
                        CheckOneCollider();
                        break;
                    case PreparationState.CreateTree:
                        CreateOneTree();
                        break;
                    case PreparationState.Ready:
                    case PreparationState.Failed:
                    case PreparationState.Disposed:
                        return;
                }
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>Check the captured vehicle identity, topology, and source-assembly-local transforms.
    /// World pose is deliberately not captured because a held source can move while preparation runs.</summary>
    internal bool IsCurrent()
    {
        if (_disposed || !IsSourceLive()) return false;
        // Keep the controller's per-handoff liveness poll cheap while capture advances.
        // CompleteCapture and the completed worker perform full snapshot comparisons.
        // Keep polling cheap while capture or detached preparation is still in flight.
        if (_sourceTree == null || !_captureComplete) return true;
        return IsCapturedSourceCurrent();
    }

    private bool IsCapturedSourceCurrent()
    {
        if (!IsSourceLive() || _sourceTree == null) return false;
        if (!ReferenceEquals(_source.Parts, _sourceTree)) return false;
        var currentTop = _sourceTree.Parts;
        if (_topParts != null)
        {
            if (currentTop.Length != _topParts.Length) return false;
            for (int i = 0; i < _topPartsCopied; i++)
                if (!ReferenceEquals(currentTop[i], _topParts[i])) return false;
        }
        foreach (var snapshot in _topology)
        {
            if (!ReferenceEquals(snapshot.Part.Tree?.OwningVehicle, _source)
                || !snapshot.Part.MatrixAsmb2VehicleAsmb.Equals(snapshot.AssemblyTransform)) return false;
            var children = snapshot.Part.SubParts;
            if (children.Length != snapshot.Children.Length) return false;
            for (int i = 0; i < children.Length; i++)
                if (!ReferenceEquals(children[i], snapshot.Children[i])) return false;
        }
        return true;
    }

    private bool IsSourceLive() => !_source.IsDisposed && !_source.HasPhysicsBubble
        && (_sourceSystem == null || ReferenceEquals(_source.System, _sourceSystem)
            && ReferenceEquals(_source.Parent, _sourceParent) && ReferenceEquals(_source.System, Universe.CurrentSystem))
        && (_sourceTree == null || ReferenceEquals(_source.Parts, _sourceTree));

    private void AdvanceCaptureUnit()
    {
        if (_sourceTree == null)
        {
            _sourceSystem = _source.System;
            _sourceParent = _source.Parent;
            _sourceTree = _source.Parts;
            int partCount = _sourceTree.Parts.Length;
            if (partCount is <= 0 or > MaximumTopLevelParts)
                throw new InvalidDataException($"Vehicle exceeds the {MaximumTopLevelParts}-part breakup budget.");
            _topParts = new Part[partCount];
            return;
        }

        if (_topPartsCopied < _topParts!.Length)
        {
            var liveParts = _sourceTree.Parts;
            if (liveParts.Length != _topParts.Length) throw new InvalidOperationException("The vehicle topology changed during breakup capture.");
            int end = Math.Min(_topPartsCopied + MaximumSmallStepsPerHandoff, _topParts.Length);
            while (_topPartsCopied < end)
            {
                var part = liveParts[_topPartsCopied];
                if (!ReferenceEquals(part.Tree?.OwningVehicle, _source)) throw new InvalidOperationException("A source part left its vehicle during breakup capture.");
                _topParts[_topPartsCopied] = part;
                _topPartIndices.Add(part, _topPartsCopied);
                _captureQueue.Add(new(part, -1, _topPartsCopied, true, -1, part.StructuralPart, part.IsAttachedInternal));
                _topPartsCopied++;
            }
            return;
        }

        if (_currentCandidate == null)
        {
            if (_captureQueueIndex >= _captureQueue.Count)
            {
                CompleteCapture();
                return;
            }
            var candidate = _captureQueue[_captureQueueIndex++];
            if (!ReferenceEquals(candidate.Part.Tree?.OwningVehicle, _source))
                throw new InvalidOperationException("A source part left its vehicle during breakup capture.");
            if (candidate.IsFullPart)
            {
                if (!_topPartIndices.TryGetValue(candidate.StructuralAnchor, out int structuralIndex))
                    throw new InvalidDataException("An attached internal part has no captured structural parent.");
                candidate = candidate with { FullPartIndex = structuralIndex };
            }
            candidate = candidate with { IsAttachedInternal = candidate.IsAttachedInternal || candidate.Part.IsAttachedInternal };
            _currentCandidate = candidate;
            _currentMatrix = candidate.Part.MatrixAsmb2VehicleAsmb;
            if (candidate.IsFullPart)
            {
                float mass = candidate.Part.ComputeSubtreeInertMass();
                if (!float.IsFinite(mass) || mass < 0) throw new InvalidDataException("Vehicle dry mass is invalid.");
                _dryMass += mass;
                if (!double.IsFinite(_dryMass)) throw new InvalidDataException("Vehicle dry mass is invalid.");
            }
            int childCount = candidate.Part.SubParts.Length;
            int pendingCount = _captureQueue.Count - _captureQueueIndex;
            if (_topology.Count + pendingCount + 1 + childCount > MaximumCapturedParts)
                throw new InvalidDataException($"Vehicle exceeds the {MaximumCapturedParts}-part breakup capture budget.");
            _currentChildren = new Part[childCount];
            _childrenCopied = 0;
            _childrenQueued = 0;
            _currentCaptured = false;
            return;
        }

        var current = _currentCandidate.Value;
        if (_childrenCopied < _currentChildren!.Length)
        {
            var liveChildren = current.Part.SubParts;
            if (liveChildren.Length != _currentChildren.Length) throw new InvalidOperationException("A source part changed during breakup capture.");
            int end = Math.Min(_childrenCopied + MaximumSmallStepsPerHandoff, _currentChildren.Length);
            while (_childrenCopied < end) _currentChildren[_childrenCopied] = liveChildren[_childrenCopied++];
            return;
        }

        if (!_currentCaptured)
        {
            _currentSnapshotIndex = _topology.Count;
            var geometry = CaptureNode(current.Part, _currentMatrix, current.IsAttachedInternal);
            _topology.Add(new(current.Part, _currentMatrix, _currentChildren));
            _sourceNodes.Add(new(current.FullPartIndex, current.FirstLayerBranchIndex, current.IsFullPart, geometry));
            _currentCaptured = true;
            if (_currentChildren.Length == 0)
            {
                ResetCurrentNode();
                return;
            }
            return;
        }

        int enqueueEnd = Math.Min(_childrenQueued + MaximumSmallStepsPerHandoff, _currentChildren.Length);
        while (_childrenQueued < enqueueEnd)
        {
            var child = _currentChildren[_childrenQueued++];
            int firstLayerBranch = ResolveFirstLayerBranchIndex(current.IsFullPart,
                current.FirstLayerBranchIndex, ref _nextBranchIndex);
            _captureQueue.Add(new(child, _currentSnapshotIndex, current.FullPartIndex, false,
                firstLayerBranch, current.StructuralAnchor,
                current.IsAttachedInternal || child.IsAttachedInternal));
        }
        if (_childrenQueued >= _currentChildren.Length) ResetCurrentNode();
    }

    private void ResetCurrentNode()
    {
        _currentCandidate = null;
        _currentChildren = null;
        _childrenCopied = 0;
        _childrenQueued = 0;
        _currentCaptured = false;
    }

    internal static int ResolveFirstLayerBranchIndex(bool isFullPart, int inheritedBranchIndex, ref int nextBranchIndex)
    {
        if (!isFullPart) return inheritedBranchIndex;
        return nextBranchIndex++;
    }

    private AuthoredDebrisNode? CaptureNode(Part node, double4x4 matrix, bool attachedInternal)
    {
        if (!ReferenceEquals(node.Tree?.OwningVehicle, _source)) throw new InvalidOperationException("A source part left its vehicle during breakup capture.");

        foreach (var deformation in node.Modules.Get<FxDeformation>())
            if (FxDeformation.TryGetFrom(_sourceTree!.States, out var states) && states.GetState(deformation).Count > 0)
                throw new InvalidDataException("Dented geometry cannot be broken up until its deformed surface can be baked.");
        foreach (var chute in node.Modules.Get<Parachute>())
            if (chute.State is ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing or ChuteState.Full)
                throw new InvalidDataException("Retract or cut away deployed parachutes before breaking up their vehicle.");

        var models = Models(node).ToArray();
        if (models.Length == 0) return null; // Invisible module/mass containers contribute dry mass above.
        var surfaces = new AuthoredDebrisSurface[models.Length];
        var min = new double3(double.PositiveInfinity);
        var max = new double3(double.NegativeInfinity);
        for (int i = 0; i < models.Length; i++)
        {
            var (meshReference, materialReference, glass, isInternal) = models[i];
            var mesh = meshReference?.Get();
            var material = materialReference?.Get();
            if (mesh?.HostPrimitives is not { Length: > 0 } primitives || !mesh.IsReferenceable || string.IsNullOrEmpty(mesh.Id)
                || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                throw new InvalidDataException("An authored surface has no durable mesh/material or accessible geometry.");
            surfaces[i] = new(mesh.Id, material.Id, glass, isInternal);
            foreach (var primitive in primitives)
            {
                var primitiveMin = primitive.PositionMinimum;
                var primitiveMax = primitive.PositionMaximum;
                if (!ForceMath.Finite(primitiveMin) || !ForceMath.Finite(primitiveMax)
                    || primitiveMin.X > primitiveMax.X || primitiveMin.Y > primitiveMax.Y || primitiveMin.Z > primitiveMax.Z)
                    throw new InvalidDataException("Authored debris geometry has invalid precomputed mesh bounds.");
                min = double3.Min(min, primitiveMin);
                max = double3.Max(max, primitiveMax);
            }
        }

        if (!double4x4.Decompose(matrix, out var scale, out var rotation, out var translation)
            || !ForceMath.Finite(scale) || scale.X <= 0
            || Math.Abs(scale.Y - scale.X) > 1e-6 || Math.Abs(scale.Z - scale.X) > 1e-6)
            throw new InvalidDataException("Authored debris requires a finite positive uniform mesh scale.");
        var center = min + (max - min) * .5;
        // The replacement body uses a conservative box. Pad planar axes for collision
        // bounds without changing any authored mesh vertex or its local transform.
        var localSize = double3.Max(max - min, new double3(.002 / scale.X));
        var localHalf = localSize * .5;
        var position = center.Transform(matrix);
        var normalizedRotation = rotation.NormalizedOrZero();
        if (!ForceMath.Finite(position) || !ForceMath.Finite(translation) || !ForceMath.Finite(localSize) || normalizedRotation == default)
            throw new InvalidDataException("Authored debris has an invalid rigid frame.");
        var boundsMinimum = new double3(double.PositiveInfinity);
        var boundsMaximum = new double3(double.NegativeInfinity);
        for (int mask = 0; mask < 8; mask++)
        {
            var corner = center + new double3((mask & 1) == 0 ? -localHalf.X : localHalf.X,
                (mask & 2) == 0 ? -localHalf.Y : localHalf.Y, (mask & 4) == 0 ? -localHalf.Z : localHalf.Z);
            var point = corner.Transform(matrix);
            boundsMinimum = double3.Min(boundsMinimum, point);
            boundsMaximum = double3.Max(boundsMaximum, point);
        }
        var boundsExtents = double3.Max(boundsMaximum - boundsMinimum, new double3(.002));
        double volume = localSize.X * localSize.Y * localSize.Z * scale.X * scale.X * scale.X;
        if (!double.IsFinite(volume) || volume <= 0) throw new InvalidDataException("Authored debris has invalid dimensions.");
        int sourceIndex = _sourceNodes.Count;
        return new(sourceIndex, surfaces, translation, normalizedRotation, scale, position, boundsExtents, volume, attachedInternal);
    }

    private void CompleteCapture()
    {
        if (!IsCapturedSourceCurrent()) throw new InvalidOperationException("The source topology or local assembly transforms changed during breakup capture.");
        _assetDirectory = (AuthoredDebrisAssets.CaptureDirectory
            ?? throw new InvalidOperationException("Persistent Furce debris assets are unavailable."))();
        _rawSnapshotComplete = true;
        _state = PreparationState.Worker;
    }

    /// <returns>True when a task is pending or was just started.</returns>
    private bool AdvanceWorker()
    {
        if (_workerTask == null)
        {
            if (!_rawSnapshotComplete) throw new InvalidOperationException("Debris planning began before source capture completed.");
            var prepare = AuthoredDebrisAssets.PrepareOnWorker
                ?? throw new InvalidOperationException("Persistent Furce debris preparation is unavailable.");
            if (Interlocked.CompareExchange(ref _backgroundWorkerActive, 1, 0) != 0) return true;
            var sourceNodes = _sourceNodes;
            int maximumPieces = _maximumPieces;
            double dryMass = _dryMass;
            string directory = _assetDirectory!;
            var token = _cancellation.Token;
            try { _workerTask = RunWorker(prepare, sourceNodes, maximumPieces, dryMass, directory, token); }
            catch
            {
                Volatile.Write(ref _backgroundWorkerActive, 0);
                throw;
            }
            return true;
        }
        if (!_workerTask.IsCompleted) return true;
        var task = _workerTask;
        _workerTask = null;
        var result = task.GetAwaiter().GetResult(); // IsCompleted was checked; this cannot wait.
        _preparedAssets = result.Assets;
        _candidatePairs = result.CandidatePairs;
        if (result.Pieces.Length is < 2 || result.Pieces.Length > _maximumPieces
            || _preparedAssets.Length != result.Pieces.Length)
            throw new InvalidOperationException("The detached debris worker returned an invalid piece set.");
        if (_candidatePairs == null)
            throw new InvalidOperationException("The detached sibling-collision candidate set is invalid.");
        if (!IsCapturedSourceCurrent()) throw new InvalidOperationException("The source changed while debris assets were being prepared.");
        _pieces.AddRange(result.Pieces);
        _captureComplete = true; // Count now exposes the stable, fully grouped result.
        _templates = new PartTemplate?[_preparedAssets.Length];
        _state = PreparationState.Register;
        return false;
    }

    private static Task<BackgroundPreparation> RunWorker(
        Func<AuthoredDebrisShape[], string, CancellationToken, PreparedAuthoredDebrisAsset[]> prepare,
        IReadOnlyList<ForceBreakupSourceNode> sourceNodes, int maximumPieces, double dryMass,
        string directory, CancellationToken cancel) => Task.Run(() =>
    {
        try
        {
            cancel.ThrowIfCancellationRequested();
            // Freeze the DTO list on the worker. Main-thread Dispose deliberately does
            // not mutate the captured list while this task owns its reference.
            ForceBreakupSourceNode[] snapshot = sourceNodes.ToArray();
            var pieces = ForceBreakupPlanner.Build(snapshot, maximumPieces, dryMass, cancel);
            var shapes = pieces.Select(piece => piece.Shape).ToArray();
            var bounds = pieces.Select(piece => new SiblingBounds(piece.Position, piece.Shape.Extents.Length() * .5)).ToArray();
            var candidatePairs = BuildSiblingCandidates(bounds, cancel);
            cancel.ThrowIfCancellationRequested();
            var assets = prepare(shapes, directory, cancel);
            return new BackgroundPreparation(pieces, assets, candidatePairs);
        }
        finally { Volatile.Write(ref _backgroundWorkerActive, 0); }
    });

    /// <returns>True when this unit performed a main-thread native operation.</returns>
    private void AdvanceRegistration()
    {
        if (_assetIndex >= _preparedAssets!.Length)
        {
            _assetIndex = 0;
            _state = PreparationState.CreateRoots;
            return;
        }
        if (_registration == null)
        {
            _registration = (AuthoredDebrisAssets.BeginRegistration
                ?? throw new InvalidOperationException("Persistent Furce debris registration is unavailable."))(_preparedAssets[_assetIndex]);
            return;
        }
        if (!_registration.Done)
        {
            _registration.AdvanceOne();
            return;
        }
        _templates![_assetIndex] = _registration.Template
            ?? throw new InvalidOperationException("A debris template was not registered.");
        _registration = null;
        _assetIndex++;
    }

    private void CreateOneRoot()
    {
        if (_rootIndex >= Count)
        {
            if (_roots.Count != Count) throw new InvalidOperationException("A debris root was not prepared.");
            _state = PreparationState.Ready;
            return;
        }
        var template = _templates![_rootIndex] ?? throw new InvalidOperationException("A debris template is unavailable.");
        var root = new Part(template.Id, template);
        _roots.Add(root);
        _currentColliders = root.Modules.Get<ColliderModule>().ToArray();
        _colliderIndex = 0;
        _state = PreparationState.CheckColliders;
    }

    private void CheckOneCollider()
    {
        if (_currentColliders == null) throw new InvalidOperationException("Debris collider preparation was lost.");
        if (_colliderIndex < _currentColliders.Length)
        {
            if (!_currentColliders[_colliderIndex++].Handle.Exists)
                throw new InvalidOperationException("A debris collider could not be cooked.");
            return;
        }
        _state = PreparationState.CreateTree;
    }

    private void CreateOneTree()
    {
        var root = _roots[^1];
        root.Tree = PartTree.CreateFromNewPartTree(root);
        _rootIndex++;
        _currentColliders = null;
        _state = PreparationState.CreateRoots;
    }

    internal Vehicle[] Commit()
    {
        EnsureMainThread();
        if (!Ready) throw new InvalidOperationException("Debris preparation is not ready to commit.");
        if (_roots.Count != Count || !IsCurrent()) throw new InvalidOperationException("The held source changed before breakup.");
        var spawned = new List<Vehicle>(Count);
        var creatingIds = new HashSet<string>(Count, StringComparer.Ordinal);
        bool sourceTeardownStarted = false;
        try
        {
            var rotation = _source.GetBody2Cci();
            var position = _source.GetPositionCci(); var velocity = _source.GetVelocityCci();
            var time = Universe.GetLastSimStep().NextTime;
            // Native vehicle creation is necessarily an indivisible main-thread part
            // of the replacement transaction; every root was prepared ahead of time.
            for (int i = 0; i < Count; i++)
            {
                var piece = _pieces[i];
                var offset = piece.Position - _source.CenterOfMassAsmb;
                var body2Cci = doubleQuat.Concatenate(piece.Rotation, rotation).NormalizedOrZero();
                var rates = _source.BodyRates.Transform(rotation).Transform(body2Cci.Inverse());
                var orbit = Orbit.CreateFromStateCci(_source.Parent, time, position + offset.Transform(rotation),
                    velocity + double3.Cross(_source.BodyRates, offset).Transform(rotation), _source.Orbit.OrbitLineColor);
                string id = "Purr2D2_Force_" + Guid.NewGuid().ToString("N");
                creatingIds.Add(id);
                var vehicle = Vehicle.CreateVehicle(_source.System, doubleQuat.Concatenate(body2Cci, _source.Parent.GetCci2Cce()),
                    rates, _source.Parent, id, _roots[i], orbit);
                spawned.Add(vehicle);
                _source.Parent.Children.Add(vehicle); // The same registration as native separated-vehicle adoption.
                vehicle.MarkAsDebris();
                vehicle.UpdatePerFrameData();
            }
            // Candidate index pairs were built from copied assembly-local sphere bounds
            // on the worker. No geometric pair scan or radii allocation occurs here.
            foreach (var pair in _candidatePairs!)
                CollisionAvoidancePair.Link(ForcePhysicsPatches.Worker(spawned[pair.First]), ForcePhysicsPatches.Worker(spawned[pair.Second])).SuppressAllContacts = true;
            // Every replacement exists before the first irreversible source operation.
            // No solver can tick the source between draining its stores and deletion.
            sourceTeardownStarted = true;
            _source.DepleteConsumables();
            _source.StopAudio(immediate: true);
            Universe.DestroyVehicle(_source, CrewDisposition.Kill);
            return spawned.ToArray();
        }
        catch (Exception error)
        {
            if (_source.IsDisposed && spawned.Count == Count)
            {
                Console.WriteLine($"[purr2d2] Force source teardown reported after replacement: {error.Message}");
                return spawned.ToArray();
            }
            // Native construction registers before its final initialization. Recover
            // even a constructor that threw before returning its new vehicle.
            foreach (var item in _source.System.All.AsSpan())
                if (item is Vehicle vehicle && creatingIds.Contains(vehicle.Id) && !spawned.Contains(vehicle)) spawned.Add(vehicle);
            foreach (var vehicle in spawned)
                try { RollBack(vehicle); }
                catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force debris cleanup failed: {cleanup}"); }
            if (sourceTeardownStarted)
                throw new InvalidOperationException("Furce breakup teardown failed after source draining began; stored contents may already have been released.", error);
            throw;
        }
    }

    private static void RollBack(Vehicle vehicle)
    {
        if (ForcePhysicsPatches.Worker(vehicle) != null) { Universe.DestroyVehicle(vehicle); return; }
        try { vehicle.Dispose(endMission: false); }
        catch (Exception error) { Console.WriteLine($"[purr2d2] Incomplete Force debris teardown: {error.Message}"); }
        finally
        {
            vehicle.System.All.Deregister(vehicle);
            if (vehicle.Parts is { IsDisposed: false } tree) tree.Dispose();
        }
    }

    private void Fail(Exception error)
    {
        _failure = error;
        _state = PreparationState.Failed;
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void EnsureMainThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("Furce breakup preparation must be advanced on its captured main thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureMainThread();
        _disposed = true;
        _state = PreparationState.Disposed;
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        var worker = _workerTask;
        if (worker is { IsCompleted: false })
        {
            var cancellation = _cancellation;
            _ = worker.ContinueWith(static (completed, state) =>
            {
                _ = completed.Exception; // Observe worker faults even when the plan was cancelled.
                ((CancellationTokenSource)state!).Dispose();
            }, cancellation, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            if (worker != null) _ = worker.Exception;
            _cancellation.Dispose();
        }
        foreach (var root in _roots)
            if (root.Tree is { } tree) { if (!tree.IsDisposed && tree.OwningVehicle == null) tree.Dispose(); }
            else root.Dispose();
        _roots.Clear();
        _workerTask = null;
        _captureQueue.Clear();
        _topPartIndices.Clear();
        _preparedAssets = null;
        _templates = null;
        _registration = null;
    }

    private static bool HasVisual(Part node) => !node.Modules.Get<PartModelModule>().IsEmpty
        || !node.Modules.Get<PartModelDynamicModule>().IsEmpty || !node.Modules.Get<PartModelGlassModule>().IsEmpty;

    private static IEnumerable<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass, bool Internal)> Models(Part node)
    {
        // Span-backed native module collections cannot cross iterator suspension.
        foreach (var model in node.Modules.Get<PartModelModule>().ToArray())
            yield return (model.PartModel.Template.Mesh, model.PartModel.Template.Material, false, model.PartModel.Template.Internal);
        foreach (var model in node.Modules.Get<PartModelDynamicModule>().ToArray())
            yield return (model.PartModelDynamic.Template.Mesh, model.PartModelDynamic.Template.Material, false, false);
        foreach (var model in node.Modules.Get<PartModelGlassModule>().ToArray())
            yield return (model.PartModelGlass.Template.Mesh, model.PartModelGlass.Template.Material, true, false);
    }
}
