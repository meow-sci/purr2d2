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
    private sealed record Piece(AuthoredDebrisShape Shape, double3 Position, doubleQuat Rotation, double Weight);
    private sealed record PartSnapshot(Part Part, double4x4 AssemblyTransform, Part[] Children);
    private sealed record BackgroundPreparation(PreparedAuthoredDebrisAsset[] Assets, SiblingPair[] CandidatePairs);
    internal readonly record struct SiblingBounds(double3 Center, double Radius);
    internal readonly record struct SiblingPair(int First, int Second);

    private const int MaximumTopLevelParts = 4096;
    private const int MaximumCapturedParts = 32768;
    private const int MaximumSmallStepsPerHandoff = 8;
    private const double MainThreadBudgetMilliseconds = 1.5;
    private const double MinimumPieceMass = .0002;
    private static int _backgroundWorkerActive;

    private readonly Vehicle _source;
    private readonly int _mainThreadId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Piece> _pieces = new();
    private readonly List<Part> _roots = new();
    private readonly List<PartSnapshot> _topology = new();
    private readonly List<Piece> _currentGroup = new();
    private PartTree? _sourceTree;
    private IParentBody? _sourceParent;
    private CelestialSystem? _sourceSystem;
    private Part[]? _topParts;
    private int _topPartsCopied;
    private int _topIndex;
    private Part? _currentFull;
    private Part[]? _currentSubParts;
    private int _subPartsCopied;
    private int _nodeIndex;
    private double _currentFullMass;
    private double _currentGroupVolume;
    private double _dryMass;
    private bool _captureComplete;
    private bool _disposed;
    private PreparationState _state = PreparationState.Capture;
    private Exception? _failure;
    private string? _assetDirectory;
    private AuthoredDebrisShape[]? _workerShapes;
    private SiblingBounds[]? _workerBounds;
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
        int visuals = 0;
        foreach (var full in source.Parts.Parts)
        {
            if (HasVisual(full)) visuals++;
            foreach (var sub in full.SubParts) if (HasVisual(sub)) visuals++;
        }
        return visuals > 1;
    }

    /// <summary>Construction only records the source and thread identity. Capture starts in AdvancePreparation.</summary>
    internal ForceVehicleBreakup(Vehicle source)
    {
        _source = source;
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
        // The full snapshot comparison runs exactly at capture completion and again
        // after worker preparation / immediately before commit.
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
                _topParts[_topPartsCopied++] = part;
            }
            return;
        }

        if (_topIndex >= _topParts!.Length)
        {
            CompleteCapture();
            return;
        }

        if (_currentFull == null)
        {
            _currentFull = _topParts[_topIndex];
            int childCount = _currentFull.SubParts.Length;
            if (childCount > MaximumCapturedParts - _topology.Count)
                throw new InvalidDataException($"Vehicle exceeds the {MaximumCapturedParts}-part breakup capture budget.");
            _currentSubParts = new Part[childCount];
            _subPartsCopied = 0;
            float mass = _currentFull.ComputeSubtreeInertMass();
            if (!float.IsFinite(mass) || mass < 0) throw new InvalidDataException("Vehicle dry mass is invalid.");
            _currentFullMass = mass;
            _dryMass += mass;
            if (!double.IsFinite(_dryMass)) throw new InvalidDataException("Vehicle dry mass is invalid.");
            return;
        }

        if (_subPartsCopied < _currentSubParts!.Length)
        {
            var liveSubParts = _currentFull.SubParts;
            if (liveSubParts.Length != _currentSubParts.Length) throw new InvalidOperationException("A source part changed during breakup capture.");
            int end = Math.Min(_subPartsCopied + MaximumSmallStepsPerHandoff, _currentSubParts.Length);
            while (_subPartsCopied < end) _currentSubParts[_subPartsCopied] = liveSubParts[_subPartsCopied++];
            return;
        }

        int nodeCount = _currentSubParts!.Length + 1;
        if (_nodeIndex < nodeCount)
        {
            Part node = _nodeIndex == 0 ? _currentFull : _currentSubParts[_nodeIndex - 1];
            CaptureNode(node);
            _nodeIndex++;
            return;
        }

        if (_currentGroupVolume > 0)
        {
            for (int i = 0; i < _currentGroup.Count; i++)
                _pieces.Add(_currentGroup[i] with { Weight = _currentFullMass * _currentGroup[i].Weight / _currentGroupVolume });
        }
        _currentGroup.Clear();
        _currentGroupVolume = 0;
        _currentFull = null;
        _currentSubParts = null;
        _subPartsCopied = 0;
        _nodeIndex = 0;
        _topIndex++;
        return;
    }

    private void CaptureNode(Part node)
    {
        if (!ReferenceEquals(node.Tree?.OwningVehicle, _source)) throw new InvalidOperationException("A source part left its vehicle during breakup capture.");
        var matrix = node.MatrixAsmb2VehicleAsmb;
        Part[] children = ReferenceEquals(node, _currentFull) ? _currentSubParts! : node.SubParts.ToArray();
        if (_topology.Count + 1 + children.Length > MaximumCapturedParts)
            throw new InvalidDataException($"Vehicle exceeds the {MaximumCapturedParts}-part breakup capture budget.");
        _topology.Add(new(node, matrix, children));

        foreach (var deformation in node.Modules.Get<FxDeformation>())
            if (FxDeformation.TryGetFrom(_sourceTree!.States, out var states) && states.GetState(deformation).Count > 0)
                throw new InvalidDataException("Dented geometry cannot be broken up until its deformed surface can be baked.");
        foreach (var chute in node.Modules.Get<Parachute>())
            if (chute.State is ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing or ChuteState.Full)
                throw new InvalidDataException("Retract or cut away deployed parachutes before breaking up their vehicle.");

        var models = Models(node).ToArray();
        if (models.Length == 0) return; // Invisible module/mass containers contribute dry mass above.
        var surfaces = new AuthoredDebrisSurface[models.Length];
        var min = new double3(double.PositiveInfinity);
        var max = new double3(double.NegativeInfinity);
        for (int i = 0; i < models.Length; i++)
        {
            var (meshReference, materialReference, glass) = models[i];
            var mesh = meshReference?.Get();
            var material = materialReference?.Get();
            if (mesh?.HostPrimitives is not { Length: > 0 } primitives || !mesh.IsReferenceable || string.IsNullOrEmpty(mesh.Id)
                || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                throw new InvalidDataException("An authored surface has no durable mesh/material or accessible geometry.");
            surfaces[i] = new(mesh.Id, material.Id, glass);
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

        if (!double4x4.Decompose(matrix, out var scale, out var rotation, out _)
            || !ForceMath.Finite(scale) || scale.X <= 0
            || Math.Abs(scale.Y - scale.X) > 1e-6 || Math.Abs(scale.Z - scale.X) > 1e-6)
            throw new InvalidDataException("Authored debris requires a finite positive uniform mesh scale.");
        var center = min + (max - min) * .5;
        // Authored sheets can be planar. Give only the collider a thin finite thickness.
        var extents = double3.Max((max - min) * scale.X, new double3(.002));
        var position = center.Transform(matrix);
        var normalizedRotation = rotation.NormalizedOrZero();
        if (!ForceMath.Finite(position) || !ForceMath.Finite(extents) || normalizedRotation == default)
            throw new InvalidDataException("Authored debris has an invalid rigid frame.");
        double volume = extents.X * extents.Y * extents.Z;
        if (!double.IsFinite(volume) || volume <= 0) throw new InvalidDataException("Authored debris has invalid dimensions.");
        if (_pieces.Count + _currentGroup.Count >= ForceCaptureLimits.Maximum)
            throw new InvalidDataException($"Vehicle exceeds the {ForceCaptureLimits.Maximum}-piece breakup budget.");
        _currentGroup.Add(new(new(surfaces, center, extents, scale.X, 1), position, normalizedRotation, volume));
        _currentGroupVolume += volume;
    }

    private void CompleteCapture()
    {
        if (_pieces.Count < 2) throw new InvalidDataException("The vehicle has fewer than two visible authored pieces.");
        double weights = _pieces.Sum(piece => piece.Weight);
        if (!double.IsFinite(_dryMass) || _dryMass <= _pieces.Count * MinimumPieceMass
            || !double.IsFinite(weights) || weights <= 0)
            throw new InvalidDataException("The vehicle has insufficient dry mass for separate debris.");
        for (int i = 0; i < _pieces.Count; i++)
            _pieces[i] = _pieces[i] with { Shape = _pieces[i].Shape with
            {
                Surfaces = _pieces[i].Shape.Surfaces.ToArray(),
                MassKg = (float)(MinimumPieceMass + (_dryMass - _pieces.Count * MinimumPieceMass) * _pieces[i].Weight / weights)
            } };
        if (!IsCapturedSourceCurrent()) throw new InvalidOperationException("The source topology or local assembly transforms changed during breakup capture.");
        _assetDirectory = (AuthoredDebrisAssets.CaptureDirectory
            ?? throw new InvalidOperationException("Persistent Force debris assets are unavailable."))();
        _workerShapes = _pieces.Select(piece => piece.Shape).ToArray();
        _workerBounds = _pieces.Select(piece => new SiblingBounds(piece.Position, piece.Shape.Extents.Length() * .5)).ToArray();
        _captureComplete = true;
        _state = PreparationState.Worker;
    }

    /// <returns>True when a task is pending or was just started.</returns>
    private bool AdvanceWorker()
    {
        if (_workerTask == null)
        {
            var prepare = AuthoredDebrisAssets.PrepareOnWorker
                ?? throw new InvalidOperationException("Persistent Force debris preparation is unavailable.");
            if (Interlocked.CompareExchange(ref _backgroundWorkerActive, 1, 0) != 0) return true;
            var shapes = _workerShapes!;
            var bounds = _workerBounds!;
            string directory = _assetDirectory!;
            var token = _cancellation.Token;
            try { _workerTask = RunWorker(prepare, shapes, bounds, directory, token); }
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
        if (_preparedAssets.Length != Count) throw new InvalidOperationException("The detached debris worker returned an incomplete asset set.");
        if (_workerBounds == null || _candidatePairs == null)
            throw new InvalidOperationException("The detached sibling-collision candidate set is invalid.");
        if (!IsCapturedSourceCurrent()) throw new InvalidOperationException("The source changed while debris assets were being prepared.");
        _templates = new PartTemplate?[_preparedAssets.Length];
        _state = PreparationState.Register;
        return false;
    }

    private static Task<BackgroundPreparation> RunWorker(
        Func<AuthoredDebrisShape[], string, CancellationToken, PreparedAuthoredDebrisAsset[]> prepare,
        AuthoredDebrisShape[] shapes, SiblingBounds[] bounds, string directory, CancellationToken cancel) => Task.Run(() =>
    {
        try
        {
            cancel.ThrowIfCancellationRequested();
            var candidatePairs = BuildSiblingCandidates(bounds, cancel);
            cancel.ThrowIfCancellationRequested();
            var assets = prepare(shapes, directory, cancel);
            return new BackgroundPreparation(assets, candidatePairs);
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
                ?? throw new InvalidOperationException("Persistent Force debris registration is unavailable."))(_preparedAssets[_assetIndex]);
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
                throw new InvalidOperationException("Force breakup teardown failed after source draining began; stored contents may already have been released.", error);
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
            throw new InvalidOperationException("Force breakup preparation must be advanced on its captured main thread.");
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
        _workerShapes = null;
        _preparedAssets = null;
        _templates = null;
        _registration = null;
    }

    private static bool HasVisual(Part node) => !node.Modules.Get<PartModelModule>().IsEmpty
        || !node.Modules.Get<PartModelDynamicModule>().IsEmpty || !node.Modules.Get<PartModelGlassModule>().IsEmpty;

    private static IEnumerable<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)> Models(Part node)
    {
        // Span-backed native module collections cannot cross iterator suspension.
        foreach (var model in node.Modules.Get<PartModelModule>().ToArray()) yield return (model.PartModel.Template.Mesh, model.PartModel.Template.Material, false);
        foreach (var model in node.Modules.Get<PartModelDynamicModule>().ToArray()) yield return (model.PartModelDynamic.Template.Mesh, model.PartModelDynamic.Template.Material, false);
        foreach (var model in node.Modules.Get<PartModelGlassModule>().ToArray()) yield return (model.PartModelGlass.Template.Mesh, model.PartModelGlass.Template.Material, true);
    }
}
