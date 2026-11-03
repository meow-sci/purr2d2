using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Slicing.Detection;

namespace Purr2D2.Rendering;

/// <summary>Settled-thread-only surface confirmation. Discovery, target-frame reads and
/// triangle work are all bounded. A deferred query owns immutable sweep endpoints.</summary>
internal sealed class ManualContactProbe
{
    internal const int TriangleBudget = 4096, DiscoveryBudget = 64, FrameBudget = 128, CandidateLimit = 128, PrimitiveBudget = 256;
    internal const double UpdateMilliseconds = .5, MaximumAge = .15, MaximumEndpointTravel = 1;
    internal const int RefinementUpdates = 2;
    internal const double RefinementSeconds = .05;
    internal readonly record struct Sample(ulong Gesture, double Time, double3 BaseBody, double3 TipBody, float2 Drag)
    {
        internal bool PassiveContact { get; init; }
        internal double VisualScale { get; init; } = 1d;
        internal float3 VisualAxes { get; init; } = float3.One;
        // The deployed setting before visual scaling. A nonuniform transform can
        // change physical blade length during a swing without deploying the blade.
        internal double NominalLength { get; init; }
    }
    internal readonly record struct Contact(Part Part, double3 PointLocal, double3 NormalLocal,
        double DistanceMeters, double BladeLength, double4x4 SourceTransform, double4x4 PartTransform)
    {
        internal double3 BladeDirectionLocal { get; init; }
        internal double3 MotionLocal { get; init; }
        internal double3 PreviousBaseLocal { get; init; }
        internal double3 PreviousTipLocal { get; init; }
        internal double3 CurrentBaseLocal { get; init; }
        internal double3 CurrentTipLocal { get; init; }
        internal double HitTime01 { get; init; }
        internal double AlongBlade01 { get; init; }
        internal double SweepTolerance { get; init; }
        internal double SampleTime { get; init; }
        internal long TargetOrdinal { get; init; }
        internal bool FullMeshScan { get; init; }
        internal ulong TriggerEpoch { get; init; }
        internal MeshReference? HitMesh { get; init; }
        internal double3[][]? HitPrimitives { get; init; }
        internal ulong HitGeometryRevision { get; init; }
        internal ulong HitGeometryEpoch { get; init; }
        internal object? HitScene { get; init; }
    }
    private sealed class Candidate(Part part, Vehicle vehicle, MeshReference mesh, bool immutableAsset, long ordinal)
    {
        internal readonly Part Part = part;
        internal readonly Vehicle Vehicle = vehicle;
        internal readonly MeshReference Mesh = mesh;
        internal readonly bool ImmutableAsset = immutableAsset;
        internal readonly long Ordinal = ordinal;
        internal Sample Previous;
        internal CutPoint Base, Tip;
        internal double4x4 SourceTransform;
        internal bool HasPrevious;
    }
    private sealed class Query(Candidate source, BladeSweep sweep, Sample sample, double length, double4x4 sourceTransform, double4x4 partTransform, double3[][] primitives)
    {
        internal readonly Candidate Source = source;
        internal readonly BladeSweep Sweep = sweep;
        internal readonly Sample Sample = sample;
        internal readonly double Length = length;
        internal readonly double4x4 SourceTransform = sourceTransform;
        internal readonly double4x4 PartTransform = partTransform;
        internal readonly double3[][] Primitives = primitives;
        internal int Primitive, Triangle;
        internal bool HasBest;
        internal CutPoint BestPoint, BestNormal;
        internal double BestTime, BestAlong, FirstHitTime;
        internal int RefinementUpdates, LastCountedAdvance;
        internal CachedRibbonStore.Lease? CacheLease;
        internal CachedRibbonGeometry.Query? Cached;
        internal ulong Revision;
        internal ulong RevisionEpoch;
        internal object? Scene;
        internal List<(BladeSweep Sweep, double Start, double End)>? ScanIntervals;
        internal int ScanInterval;
    }
    private sealed record CaptureWork(Candidate Source, double3[][] Primitives, CachedRibbonStore.Key Key,
        CachedRibbonCapture<double3> Capture);
    // Experimental single-source path; a caller must explicitly certify immutable
    // loaded meshes. It is not wired to the public multi-target strategy setting.
    internal Func<MeshReference, bool>? CacheableMesh { get; set; }
    private CachedRibbonStore? _ribbonStore;
    private readonly SliceGeometryRevisionRegistry _ribbonRevisions = new();
    private object? _revisionScene;
    private CaptureWork? _capture;
    internal int CacheFallbacks { get; private set; }
    internal int CacheHits { get; private set; }
    internal int CacheIncomplete { get; private set; }
    internal int CacheUnsupported { get; private set; }
    private readonly List<Candidate> _candidates = new(CandidateLimit);
    private IEnumerator<int>? _discovery;
    private KittenEva? _owner;
    private object? _system;
    private ulong _gesture;
    private int _candidate, _replace;
    private int _advanceSerial;
    private Query? _query;
    private readonly List<Query> _multiQueries = new(CandidateLimit);
    private int _multiCursor;
    private long _nextOrdinal;
    private bool _automaticCachePolicy;
    private Sample _sample;
    internal double LastUpdateMilliseconds { get; private set; }
    internal double MaximumUpdateMilliseconds { get; private set; }
    internal int PrimitiveReads { get; private set; }
    internal int TestedTriangles { get; private set; }
    internal int DiscoveryUnits { get; private set; }
    internal int FrameReads { get; private set; }
    internal int Saturations { get; private set; }
    internal int ExpiredQueries { get; private set; }
    internal bool Pending => _query != null || _multiQueries.Count != 0;
    // Tests may disable only the soft clock, retaining every hard work limit.
    internal double SoftBudgetMilliseconds { get; set; } = UpdateMilliseconds;

    internal void Reset()
    {
        _discovery?.Dispose(); _discovery = null;
        _candidates.Clear(); DropQuery(); _capture = null; _owner = null; _system = null; _gesture = 0;
        foreach (var query in _multiQueries) query.CacheLease?.Dispose();
        _multiQueries.Clear(); _multiCursor = 0;
        _candidate = _replace = _advanceSerial = 0; _nextOrdinal = 0;
    }

    internal bool Advance(KittenEva owner, Sample sample, out Contact contact)
    {
        // Returning to legacy mode must not silently enable its experimental cache.
        if (_automaticCachePolicy) { CacheableMesh = null; _automaticCachePolicy = false; }
        long started = SliceDetectionDiagnostics.Start();
        try
        {
            bool found = AdvanceCore(owner, sample, out contact);
            SliceDetectionDiagnostics.Record(SliceDetectorKind.LegacyRibbon, started, found ? 1 : 0, Pending);
            return found;
        }
        finally
        {
            if (started != 0 && DebugTimings.Enabled)
            {
                LastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                MaximumUpdateMilliseconds = Math.Max(MaximumUpdateMilliseconds, LastUpdateMilliseconds);
            }
        }
    }

    // Multi-target cached mode. Every cursor is tied to one frozen interval and
    // all cursors share the same discovery, triangle, node and clock budgets.
    // Output is caller-owned and contains at most one hit per structural part.
    internal int AdvanceMany(KittenEva owner, Sample sample, List<Contact> output)
    {
        long started = SliceDetectionDiagnostics.Start();
        int count = AdvanceManyCore(owner, sample, output);
        SliceDetectionDiagnostics.Record(SliceDetectorKind.CachedRibbon, started, count, Pending);
        return count;
    }

    private int AdvanceManyCore(KittenEva owner, Sample sample, List<Contact> output)
    {
        output.Clear();
        _advanceSerial++; TestedTriangles = DiscoveryUnits = FrameReads = PrimitiveReads = 0;
        if (sample.Gesture == 0 || !double.IsFinite(sample.Time) || !Point(sample.BaseBody).Finite || !Point(sample.TipBody).Finite
            || !double.IsFinite(sample.VisualScale) || sample.VisualScale <= 0
            || !double.IsFinite(sample.NominalLength) || sample.NominalLength < 0 || !PositiveAxes(sample.VisualAxes)
            || owner.IsDisposed || Program.EditorFlag || Program.GetMainCamera() == null || Universe.CurrentSystem == null)
        { Reset(); return 0; }
        if (!ReferenceEquals(_owner, owner) || !ReferenceEquals(_system, Universe.CurrentSystem) || _gesture != sample.Gesture)
        { Reset(); _owner = owner; _system = Universe.CurrentSystem; _gesture = sample.Gesture; }
        EnsureRevisionScene();
        _sample = sample;
        long started = Stopwatch.GetTimestamp();
        // Render-module template meshes are loaded assets. MeshView sources have
        // no verified immutability contract and always use the uncached scanner.
        if (CacheableMesh == null) { CacheableMesh = _ => true; _automaticCachePolicy = true; }
        _ribbonStore ??= new();
        _ribbonStore.Drain(); AdvanceCapture(owner, started);
        _discovery ??= Discover(owner).GetEnumerator();
        try
        {
            while (DiscoveryUnits < DiscoveryBudget && (DiscoveryUnits == 0 || WithinBudget(started, .2)))
            {
                DiscoveryUnits++;
                if (_discovery.MoveNext()) continue;
                _discovery.Dispose(); _discovery = null; break;
            }
        }
        catch (InvalidOperationException) { _discovery?.Dispose(); _discovery = null; }
        int sampled = 0;
        while (_candidates.Count > 0 && sampled < _candidates.Count && FrameReads < FrameBudget && WithinBudget(started, .4))
        {
            if (_multiQueries.Count >= CandidateLimit) { Saturations++; break; }
            if (_candidate >= _candidates.Count) _candidate = 0;
            var entry = _candidates[_candidate++]; sampled++; FrameReads++;
            if (HasMultiQuery(entry)) continue;
            if (!SampleFrame(entry, owner, sample, out var a, out var b)) { entry.HasPrevious = false; continue; }
            var previous = entry.Previous;
            var sourceTransform = entry.Part.StructuralPart.MatrixAsmb2VehicleAsmb;
            var partTransform = entry.Part.MatrixAsmb2VehicleAsmb;
            double scale = Math.Sqrt(double3.TransformNormal(double3.UnitX, partTransform).LengthSquared()
                + double3.TransformNormal(double3.UnitY, partTransform).LengthSquared()
                + double3.TransformNormal(double3.UnitZ, partTransform).LengthSquared());
            var sweep = new BladeSweep(entry.Base, entry.Tip, a, b, BladeSweep.ApproximationMeters / Math.Max(scale, 1e-12));
            double baseTravel = WorldDistanceSquared(a - entry.Base, partTransform);
            double travel = Math.Max(baseTravel, WorldDistanceSquared(b - entry.Tip, partTransform));
            double length = (sample.TipBody - sample.BaseBody).Length();
            double endpointLimit = Math.Max(MaximumEndpointTravel, length * 1.5);
            double hiltLimit = MaximumEndpointTravel * Math.Max(1d, sample.VisualScale);
            double currentLength = sample.NominalLength > 0 ? sample.NominalLength : length;
            double previousLength = previous.NominalLength > 0 ? previous.NominalLength : (previous.TipBody - previous.BaseBody).Length();
            bool motion = entry.HasPrevious && previous.Gesture == sample.Gesture
                && sample.Time > previous.Time && sample.Time - previous.Time <= MaximumAge
                && (sample.PassiveContact || (sample.Drag - previous.Drag).LengthSquared() >= .000001f)
                && travel >= .000004 && baseTravel <= hiltLimit * hiltLimit
                && travel <= Math.Max(endpointLimit, hiltLimit) * Math.Max(endpointLimit, hiltLimit)
                && Math.Abs(sample.VisualScale - previous.VisualScale) <= Math.Max(sample.VisualScale, previous.VisualScale) * .0002
                && StableAxes(sample.VisualAxes, previous.VisualAxes) && Math.Abs(currentLength - previousLength) <= .001
                && entry.SourceTransform.Equals(sourceTransform);
            entry.Base = a; entry.Tip = b; entry.Previous = sample; entry.HasPrevious = true; entry.SourceTransform = sourceTransform;
            if (!motion || !BoundsCouldHit(sweep, entry.Mesh.BoundingSphereRadius) || entry.Mesh.PositionsCompare is not { } primitives) continue;
            var query = new Query(entry, sweep, sample, length, sourceTransform, partTransform, primitives)
                { Revision = _ribbonRevisions.Revision(entry.Mesh), RevisionEpoch = _ribbonRevisions.Epoch, Scene = _system };
            PrepareCachedQuery(query);
            if (query.Cached == null && sweep.Subdivisions > BladeSweep.MaximumSubdivisions
                && !PrepareScanIntervals(query))
            { CacheUnsupported++; query.CacheLease?.Dispose(); continue; }
            _multiQueries.Add(query);
        }
        ProcessMultiQueries(owner, sample.Time, output, started);
        SortMultiContacts(output);
        return output.Count;
    }

    // Drain only already-frozen manual intervals after ordinary release. This
    // never discovers targets, samples poses, or constructs a new interval.
    // Call instead of Advance/AdvanceMany, not in addition to them in a frame.
    internal int DrainFrozen(KittenEva owner, double now, List<Contact> output)
    {
        long started = SliceDetectionDiagnostics.Start();
        int count = DrainFrozenCore(owner, now, output);
        SliceDetectionDiagnostics.Record(SliceDetectorKind.ManualDrain, started, count, Pending);
        return count;
    }

    private int DrainFrozenCore(KittenEva owner, double now, List<Contact> output)
    {
        output.Clear();
        _advanceSerial++; TestedTriangles = DiscoveryUnits = FrameReads = PrimitiveReads = 0;
        if (!double.IsFinite(now) || owner.IsDisposed || Program.EditorFlag
            || !ReferenceEquals(_owner, owner) || !ReferenceEquals(_system, Universe.CurrentSystem))
        { Reset(); return 0; }
        if (_query != null)
        {
            if (_query.Sample.PassiveContact) DropQuery();
            else { _multiQueries.Add(_query); _query = null; }
        }
        for (int i = _multiQueries.Count - 1; i >= 0; i--)
            if (_multiQueries[i].Sample.PassiveContact)
            { _multiQueries[i].CacheLease?.Dispose(); _multiQueries.RemoveAt(i); }
        long started = Stopwatch.GetTimestamp();
        ProcessMultiQueries(owner, now, output, started);
        SortMultiContacts(output);
        return output.Count;
    }

    private void ProcessMultiQueries(KittenEva owner, double now, List<Contact> output, long started)
    {
        int nodeBudget = 4096, attempts = _multiQueries.Count;
        while (attempts-- > 0 && _multiQueries.Count > 0 && WithinBudget(started))
        {
            if (_multiCursor >= _multiQueries.Count) _multiCursor = 0;
            var query = _multiQueries[_multiCursor];
            if (!Current(query, owner, now))
            { query.CacheLease?.Dispose(); _multiQueries.RemoveAt(_multiCursor); ExpiredQueries++; continue; }
            if (query.HasBest && query.LastCountedAdvance != _advanceSerial)
            { query.RefinementUpdates++; query.LastCountedAdvance = _advanceSerial; }
            bool finished = false, unsupported = false;
            if (query.Cached is { } cached)
            {
                var progress = cached.Advance(new(nodeBudget, TriangleBudget - TestedTriangles, () => WithinBudget(started)));
                nodeBudget -= progress.NodesVisited; TestedTriangles += progress.TrianglesTested;
                if (progress.Best is { } best && Better(query, best.Point, best.Normal, best.Time, best.Along))
                    SetBest(query, best.Point, best.Normal, best.Time, best.Along, now);
                unsupported = progress.Status == CachedRibbonGeometry.QueryStatus.UnsupportedMotion;
                finished = progress.Status != CachedRibbonGeometry.QueryStatus.Pending;
            }
            else
            {
                while (TestedTriangles < TriangleBudget && WithinBudget(started))
                {
                    if (query.Primitive >= query.Primitives.Length)
                    {
                        if (query.ScanIntervals == null || ++query.ScanInterval >= query.ScanIntervals.Count
                            || query.HasBest && query.ScanIntervals[query.ScanInterval].Start > query.BestTime + 1e-10)
                        { finished = true; break; }
                        query.Primitive = query.Triangle = 0;
                    }
                    if (query.Triangle == 0)
                    { if (PrimitiveReads >= PrimitiveBudget) break; PrimitiveReads++; }
                    var vertices = query.Primitives[query.Primitive];
                    if (vertices == null || query.Triangle + 2 >= vertices.Length)
                    { query.Primitive++; query.Triangle = 0; continue; }
                    int index = query.Triangle; query.Triangle += 3; TestedTriangles++;
                    var triangle = new CutTriangle(Point(vertices[index]), Point(vertices[index + 1]), Point(vertices[index + 2]));
                    var interval = query.ScanIntervals == null ? (query.Sweep, 0d, 1d) : query.ScanIntervals[query.ScanInterval];
                    if (interval.Item1.TryHit(triangle, out var point, out double along, out double localTime))
                    {
                        double time = interval.Item2 + (interval.Item3 - interval.Item2) * localTime;
                        if (Better(query, point, triangle.Normal, time, along)) SetBest(query, point, triangle.Normal, time, along, now);
                    }
                    if (query.Triangle + 2 >= vertices.Length) { query.Primitive++; query.Triangle = 0; }
                }
                if (query.ScanIntervals == null && query.Primitive >= query.Primitives.Length) finished = true;
            }
            double age = now - query.Sample.Time;
            bool refined = query.HasBest && (query.RefinementUpdates >= RefinementUpdates
                || now - query.FirstHitTime >= RefinementSeconds || age >= MaximumAge - RefinementSeconds);
            if (finished || refined)
            {
                if (unsupported) CacheUnsupported++;
                else if (query.HasBest) AddMultiContact(output, MakeContact(query, finished));
                query.CacheLease?.Dispose(); _multiQueries.RemoveAt(_multiCursor);
            }
            else { CacheIncomplete++; _multiCursor++; }
        }
    }

    private static void SortMultiContacts(List<Contact> output) => output.Sort((a, b) =>
        {
            int order = a.SampleTime.CompareTo(b.SampleTime);
            if (order == 0) order = a.HitTime01.CompareTo(b.HitTime01);
            if (order == 0) order = a.AlongBlade01.CompareTo(b.AlongBlade01);
            return order == 0 ? a.TargetOrdinal.CompareTo(b.TargetOrdinal) : order;
        });

    private void SetBest(Query query, CutPoint point, CutPoint normal, double time, double along, double now)
    {
        if (!query.HasBest) { query.FirstHitTime = now; query.LastCountedAdvance = _advanceSerial; }
        query.HasBest = true; query.BestPoint = point; query.BestNormal = normal; query.BestTime = time; query.BestAlong = along;
    }
    private bool HasMultiQuery(Candidate candidate)
    {
        foreach (var query in _multiQueries) if (ReferenceEquals(query.Source, candidate)) return true;
        return false;
    }
    private static bool PrepareScanIntervals(Query query)
    {
        var intervals = new List<(BladeSweep Sweep, double Start, double End)>(16);
        bool Split(BladeSweep sweep, double start, double end, int depth)
        {
            if (sweep.Subdivisions <= BladeSweep.MaximumSubdivisions)
            { intervals.Add((sweep, start, end)); return true; }
            if (depth == 4) return false;
            var baseMid = (sweep.Base0 + sweep.Base1) * .5;
            var tipMid = (sweep.Tip0 + sweep.Tip1) * .5;
            double middle = (start + end) * .5;
            return Split(new(sweep.Base0, sweep.Tip0, baseMid, tipMid, sweep.Tolerance), start, middle, depth + 1)
                && Split(new(baseMid, tipMid, sweep.Base1, sweep.Tip1, sweep.Tolerance), middle, end, depth + 1);
        }
        if (!query.Sweep.Finite || !double.IsFinite(query.Sweep.Tolerance) || query.Sweep.Tolerance <= 0
            || !Split(query.Sweep, 0, 1, 0)) return false;
        query.ScanIntervals = intervals; return true;
    }
    private static void AddMultiContact(List<Contact> output, Contact candidate)
    {
        for (int i = 0; i < output.Count; i++)
        {
            var old = output[i];
            if (!ReferenceEquals(old.Part.StructuralPart, candidate.Part.StructuralPart)) continue;
            if (candidate.SampleTime < old.SampleTime || candidate.SampleTime == old.SampleTime
                && (candidate.HitTime01 < old.HitTime01 || candidate.HitTime01 == old.HitTime01 && candidate.AlongBlade01 < old.AlongBlade01))
                output[i] = candidate;
            return;
        }
        if (output.Count < CandidateLimit) output.Add(candidate);
    }
    private bool AdvanceCore(KittenEva owner, Sample sample, out Contact contact)
    {
        _advanceSerial++;
        contact = default; TestedTriangles = DiscoveryUnits = FrameReads = PrimitiveReads = 0;
        if (sample.Gesture == 0 || !double.IsFinite(sample.Time) || !Point(sample.BaseBody).Finite || !Point(sample.TipBody).Finite
            || !double.IsFinite(sample.VisualScale) || sample.VisualScale <= 0
            || !double.IsFinite(sample.NominalLength) || sample.NominalLength < 0
            || !PositiveAxes(sample.VisualAxes)
            || owner.IsDisposed || Program.EditorFlag || Program.GetMainCamera() == null || Universe.CurrentSystem == null)
        { Reset(); return false; }
        if (!ReferenceEquals(_owner, owner) || !ReferenceEquals(_system, Universe.CurrentSystem) || _gesture != sample.Gesture)
        { Reset(); _owner = owner; _system = Universe.CurrentSystem; _gesture = sample.Gesture; }
        EnsureRevisionScene();
        _sample = sample;
        long started = Stopwatch.GetTimestamp();
        if (CacheableMesh != null)
        {
            _ribbonStore ??= new();
            _ribbonStore.Drain();
            AdvanceCapture(owner, started);
        }
        int nodeBudget = 4096;
        // Discovery yields after every scene item, part and module; it never copies a
        // fleet, builds a BVH or enumerates all meshes before checking the budget.
        if (_query == null)
        {
            _discovery ??= Discover(owner).GetEnumerator();
            try
            {
                while (DiscoveryUnits < DiscoveryBudget && (DiscoveryUnits == 0 || WithinBudget(started, .25)))
                {
                    DiscoveryUnits++;
                    if (_discovery.MoveNext()) continue;
                    _discovery.Dispose(); _discovery = null; break;
                }
            }
            catch (InvalidOperationException) { _discovery?.Dispose(); _discovery = null; } // Native collection changed: restart bounded discovery.
        }
        if (_query != null && _query.HasBest && _query.LastCountedAdvance != _advanceSerial)
        {
            _query.RefinementUpdates++;
            _query.LastCountedAdvance = _advanceSerial;
        }
        if (_query != null && !Current(_query, owner, sample.Time)) { DropQuery(); if (DebugTimings.Enabled) ExpiredQueries++; }
        while (WithinBudget(started))
        {
            if (_query != null)
            {
                var query = _query;
                if (query.Cached is { } cached)
                {
                    var progress = cached.Advance(new(nodeBudget, TriangleBudget - TestedTriangles, () => WithinBudget(started)));
                    nodeBudget -= progress.NodesVisited; TestedTriangles += progress.TrianglesTested;
                    if (progress.Best is { } best && Better(query, best.Point, best.Normal, best.Time, best.Along))
                    {
                        if (!query.HasBest) { query.FirstHitTime = sample.Time; query.LastCountedAdvance = _advanceSerial; }
                        query.HasBest = true; query.BestPoint = best.Point; query.BestNormal = best.Normal;
                        query.BestTime = best.Time; query.BestAlong = best.Along;
                    }
                    if (progress.Status == CachedRibbonGeometry.QueryStatus.UnsupportedMotion)
                    { CacheUnsupported++; DropQuery(); continue; }
                    if (progress.Status == CachedRibbonGeometry.QueryStatus.Pending)
                    { CacheIncomplete++; return TryFallback(query, sample.Time, out contact); }
                    if (query.HasBest) { contact = MakeContact(query, true); DropQuery(); return true; }
                    DropQuery(); continue;
                }
                while (query.Primitive < query.Primitives.Length)
                {
                    if (query.Triangle == 0)
                    {
                        if (PrimitiveReads >= PrimitiveBudget)
                        { if (DebugTimings.Enabled) Saturations++; return TryFallback(query, sample.Time, out contact); }
                        PrimitiveReads++;
                    }
                    var vertices = query.Primitives[query.Primitive];
                    if (vertices == null || query.Triangle + 2 >= vertices.Length)
                    {
                        query.Primitive++; query.Triangle = 0;
                        if (!WithinBudget(started))
                        { if (DebugTimings.Enabled) Saturations++; return TryFallback(query, sample.Time, out contact); }
                        continue;
                    }
                    if (TestedTriangles >= TriangleBudget)
                    { if (DebugTimings.Enabled) Saturations++; return TryFallback(query, sample.Time, out contact); }
                    int index = query.Triangle; query.Triangle += 3; TestedTriangles++;
                    var triangle = new CutTriangle(Point(vertices[index]), Point(vertices[index + 1]), Point(vertices[index + 2]));
                    if (query.Sweep.TryHit(triangle, out var hit, out double along, out double time) && Better(query, hit, triangle.Normal, time, along))
                    {
                        if (!query.HasBest)
                        {
                            query.FirstHitTime = sample.Time;
                            query.LastCountedAdvance = _advanceSerial;
                        }
                        query.HasBest = true; query.BestPoint = hit; query.BestNormal = triangle.Normal;
                        query.BestTime = time; query.BestAlong = along;
                    }
                    // Advance past a primitive after its final complete triangle. This
                    // lets an exactly-full triangle budget finish without another read.
                    if (query.Triangle + 2 >= vertices.Length) { query.Primitive++; query.Triangle = 0; }
                    if (query.Primitive >= query.Primitives.Length) break;
                    if (!WithinBudget(started))
                    { if (DebugTimings.Enabled) Saturations++; return TryFallback(query, sample.Time, out contact); }
                }
                if (query.HasBest)
                {
                    contact = MakeContact(query, true);
                    DropQuery(); return true;
                }
                DropQuery();
            }
            if (_candidates.Count == 0 || FrameReads >= Math.Min(FrameBudget, _candidates.Count)) break;
            if (_candidate >= _candidates.Count) _candidate = 0;
            var entry = _candidates[_candidate++]; FrameReads++;
            if (!SampleFrame(entry, owner, sample, out var a, out var b)) { entry.HasPrevious = false; continue; }
            var previous = entry.Previous;
            var sourceTransform = entry.Part.StructuralPart.MatrixAsmb2VehicleAsmb;
            var partTransform = entry.Part.MatrixAsmb2VehicleAsmb;
            // Frobenius norm bounds the full linear transform, including shear from
            // rotated children of nonuniformly scaled parents.
            double scale = Math.Sqrt(double3.TransformNormal(double3.UnitX, partTransform).LengthSquared()
                + double3.TransformNormal(double3.UnitY, partTransform).LengthSquared()
                + double3.TransformNormal(double3.UnitZ, partTransform).LengthSquared());
            var sweep = new BladeSweep(entry.Base, entry.Tip, a, b, BladeSweep.ApproximationMeters / Math.Max(scale, 1e-12));
            double baseTravel = WorldDistanceSquared(a - entry.Base, partTransform);
            double travel = Math.Max(baseTravel, WorldDistanceSquared(b - entry.Tip, partTransform));
            double length = (sample.TipBody - sample.BaseBody).Length();
            double endpointLimit = Math.Max(MaximumEndpointTravel, length * 1.5);
            double hiltLimit = MaximumEndpointTravel * Math.Max(1d, sample.VisualScale);
            double currentLength = sample.NominalLength > 0 ? sample.NominalLength : length;
            double previousLength = previous.NominalLength > 0 ? previous.NominalLength
                : (previous.TipBody - previous.BaseBody).Length();
            bool motion = entry.HasPrevious && previous.Gesture == sample.Gesture
                && sample.Time > previous.Time && sample.Time - previous.Time <= MaximumAge
                && (sample.PassiveContact || (sample.Drag - previous.Drag).LengthSquared() >= .000001f)
                && travel >= .000004
                && baseTravel <= hiltLimit * hiltLimit && travel <= Math.Max(endpointLimit, hiltLimit) * Math.Max(endpointLimit, hiltLimit)
                && Math.Abs(sample.VisualScale - previous.VisualScale) <= Math.Max(sample.VisualScale, previous.VisualScale) * .0002
                && StableAxes(sample.VisualAxes, previous.VisualAxes)
                && Math.Abs(currentLength - previousLength) <= .001
                && entry.SourceTransform.Equals(sourceTransform) && sweep.Subdivisions <= BladeSweep.MaximumSubdivisions;
            entry.Base = a; entry.Tip = b; entry.Previous = sample; entry.HasPrevious = true; entry.SourceTransform = sourceTransform;
            if (motion && BoundsCouldHit(sweep, entry.Mesh.BoundingSphereRadius) && entry.Mesh.PositionsCompare is { } primitives)
            {
                _query = new(entry, sweep, sample, length, sourceTransform, partTransform, primitives)
                    { Revision = _ribbonRevisions.Revision(entry.Mesh), RevisionEpoch = _ribbonRevisions.Epoch, Scene = _system };
                PrepareCachedQuery(_query);
            }
        }
        if (_query != null && TryFallback(_query, sample.Time, out contact))
        { if (DebugTimings.Enabled) Saturations++; return true; }
        if (DebugTimings.Enabled && (!WithinBudget(started) || _query != null
            || DiscoveryUnits == DiscoveryBudget || FrameReads == FrameBudget)) Saturations++;
        return false;
    }

    private static bool Better(Query query, CutPoint point, CutPoint normal, double time, double along)
    {
        const double epsilon = 1e-10;
        if (!query.HasBest || time < query.BestTime - epsilon) return true;
        if (Math.Abs(time - query.BestTime) > epsilon) return false;
        if (along < query.BestAlong - epsilon) return true;
        if (Math.Abs(along - query.BestAlong) > epsilon) return false;
        int pointOrder = Compare(point, query.BestPoint);
        return pointOrder < 0 || pointOrder == 0 && Compare(normal, query.BestNormal) < 0;
    }

    // Full scans publish the exact mesh minimum. Dense meshes may exceed the existing
    // per-update work limit; after two refinement updates, 50 ms, or entry into the
    // final 50 ms of the age window, publish the best confirmed triangle while current.
    private bool TryFallback(Query query, double now, out Contact contact)
    {
        contact = default;
        double age = now - query.Sample.Time;
        bool refinementLimit = query.RefinementUpdates >= RefinementUpdates || now - query.FirstHitTime >= RefinementSeconds;
        bool finalAgeWindow = age >= MaximumAge - RefinementSeconds;
        if (!query.HasBest || age < 0 || age > MaximumAge || !refinementLimit && !finalAgeWindow) return false;
        contact = MakeContact(query);
        DropQuery();
        return true;
    }

    private static Contact MakeContact(Query query, bool fullMeshScan = false)
    {
        var blade0 = query.Sweep.Tip0 - query.Sweep.Base0;
        var blade1 = query.Sweep.Tip1 - query.Sweep.Base1;
        var blade = Lerp(blade0, blade1, query.BestTime);
        var baseMotion = query.Sweep.Base1 - query.Sweep.Base0;
        var tipMotion = query.Sweep.Tip1 - query.Sweep.Tip0;
        var motion = Lerp(baseMotion, tipMotion, query.BestAlong);
        double baseTravelSquared = baseMotion.LengthSquared, tipTravelSquared = tipMotion.LengthSquared;
        if (motion.LengthSquared <= Math.Max(baseTravelSquared, tipTravelSquared) * 1e-12)
            motion = baseTravelSquared >= tipTravelSquared ? baseMotion : tipMotion;
        return new(query.Source.Part, Game(query.BestPoint), Game(query.BestNormal), query.BestAlong * query.Length,
            query.Length, query.SourceTransform, query.PartTransform)
        {
            BladeDirectionLocal = Game(blade),
            MotionLocal = Game(motion),
            PreviousBaseLocal = Game(query.Sweep.Base0), PreviousTipLocal = Game(query.Sweep.Tip0),
            CurrentBaseLocal = Game(query.Sweep.Base1), CurrentTipLocal = Game(query.Sweep.Tip1),
            HitTime01 = query.BestTime, AlongBlade01 = query.BestAlong, SweepTolerance = query.Sweep.Tolerance,
            SampleTime = query.Sample.Time,
            TargetOrdinal = query.Source.Ordinal, FullMeshScan = fullMeshScan, TriggerEpoch = query.Sample.Gesture,
            HitMesh = query.Source.Mesh, HitPrimitives = query.Primitives, HitGeometryRevision = query.Revision,
            HitGeometryEpoch = query.RevisionEpoch, HitScene = query.Scene
        };
    }

    private static int Compare(CutPoint a, CutPoint b)
    {
        int x = a.X.CompareTo(b.X); if (x != 0) return x;
        int y = a.Y.CompareTo(b.Y); return y != 0 ? y : a.Z.CompareTo(b.Z);
    }

    private static CutPoint Lerp(CutPoint a, CutPoint b, double t) => a + (b - a) * t;
    private void DropQuery() { _query?.CacheLease?.Dispose(); _query = null; }

    // Settled handoff only. An in-place geometry producer must call this hook;
    // array replacement is also detected by the per-query identity check.
    internal void InvalidateMeshGeometry(MeshReference mesh)
    {
        _ribbonRevisions.Invalidate(mesh);
        _ribbonStore?.Invalidate(key => ReferenceEquals(key.Mesh, mesh));
        if (_capture != null && ReferenceEquals(_capture.Source.Mesh, mesh)) _capture = null;
        if (_query != null && ReferenceEquals(_query.Source.Mesh, mesh)) DropQuery();
        for (int i = _multiQueries.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_multiQueries[i].Source.Mesh, mesh))
            { _multiQueries[i].CacheLease?.Dispose(); _multiQueries.RemoveAt(i); }
    }

    // Validates the exact observed mesh data after admission; callers still
    // validate that the part owns this mesh when their source signature is captured.
    internal bool IsEvidenceCurrent(Contact contact) => contact.HitMesh is { } mesh
        && ReferenceEquals(mesh.PositionsCompare, contact.HitPrimitives)
        && ReferenceEquals(contact.HitScene, Universe.CurrentSystem)
        && _ribbonRevisions.Epoch == contact.HitGeometryEpoch
        && _ribbonRevisions.Revision(mesh) == contact.HitGeometryRevision;

    private void EnsureRevisionScene()
    {
        if (ReferenceEquals(_revisionScene, _system)) return;
        _revisionScene = _system; _ribbonRevisions.NewScene();
        _ribbonStore?.Invalidate(_ => true); _capture = null;
    }

    private void PrepareCachedQuery(Query query)
    {
        if (_ribbonStore == null || (_automaticCachePolicy && !query.Source.ImmutableAsset) || CacheableMesh?.Invoke(query.Source.Mesh) != true) return;
        var key = new CachedRibbonStore.Key(query.Source.Mesh, query.Primitives, _ribbonRevisions.Epoch,
            _ribbonRevisions.Revision(query.Source.Mesh));
        var lease = _ribbonStore.Acquire(key);
        if (lease != null)
        {
            CacheHits++;
            query.CacheLease = lease; query.Revision = key.Revision;
            query.Cached = lease.Geometry.Begin(query.Sweep);
        }
        else
        {
            CacheFallbacks++;
            ScheduleCapture(query.Source, query.Primitives);
        }
    }

    private void ScheduleCapture(Candidate source, double3[][] primitives)
    {
        if (_capture != null || _ribbonStore == null || (_automaticCachePolicy && !source.ImmutableAsset) || CacheableMesh?.Invoke(source.Mesh) != true) return;
        var key = new CachedRibbonStore.Key(source.Mesh, primitives, _ribbonRevisions.Epoch, _ribbonRevisions.Revision(source.Mesh));
        if (!_ribbonStore.ContainsOrPending(key))
            _capture = new(source, primitives, key, new(primitives, Point));
    }

    internal void DisposeCache()
    {
        Reset(); _ribbonStore?.Dispose(); _ribbonStore = null; _revisionScene = null;
    }

    private void AdvanceCapture(KittenEva owner, long started)
    {
        if (_capture is not { } work || _ribbonStore == null) return;
        bool CurrentSource() => !work.Source.Vehicle.IsDisposed && ReferenceEquals(work.Source.Vehicle.Parent, owner.Parent)
            && ReferenceEquals(work.Source.Part.StructuralPart.Tree?.OwningVehicle, work.Source.Vehicle)
            && ReferenceEquals(work.Source.Mesh.PositionsCompare, work.Primitives)
            && work.Key.Epoch == _ribbonRevisions.Epoch
            && work.Key.Revision == _ribbonRevisions.Revision(work.Source.Mesh);
        var progress = work.Capture.Advance(Math.Min(16, PrimitiveBudget - PrimitiveReads),
            Math.Min(256, TriangleBudget - TestedTriangles), CurrentSource, () => WithinBudget(started));
        PrimitiveReads += progress.PrimitiveReads; TestedTriangles += progress.TrianglesCopied;
        if (progress.Status == CachedRibbonCapture<double3>.State.Pending) return;
        _capture = null;
        if (progress.Status == CachedRibbonCapture<double3>.State.Complete && CurrentSource())
            _ribbonStore.TryBuild(work.Key, work.Capture.Take());
        else CacheIncomplete++;
    }

    private bool WithinBudget(long started, double fraction = 1) => Stopwatch.GetElapsedTime(started).TotalMilliseconds < SoftBudgetMilliseconds * fraction;
    private bool Current(Query query, KittenEva owner, double now) => now >= query.Sample.Time && now - query.Sample.Time <= MaximumAge
        && !query.Source.Vehicle.IsDisposed && ReferenceEquals(query.Source.Vehicle.Parent, owner.Parent)
        && ReferenceEquals(query.Source.Part.StructuralPart.Tree?.OwningVehicle, query.Source.Vehicle)
        && query.Source.Part.StructuralPart.MatrixAsmb2VehicleAsmb.Equals(query.SourceTransform)
        && query.Source.Part.MatrixAsmb2VehicleAsmb.Equals(query.PartTransform)
        && ReferenceEquals(query.Source.Mesh.PositionsCompare, query.Primitives)
        && _ribbonRevisions.Epoch == query.RevisionEpoch
        && _ribbonRevisions.Revision(query.Source.Mesh) == query.Revision;
    private static double WorldDistanceSquared(CutPoint delta, double4x4 transform) => double3.TransformNormal(Game(delta), transform).LengthSquared();
    private static bool BoundsCouldHit(BladeSweep sweep, double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0) return false;
        // A bilinear ribbon lies inside its endpoint AABB. This is a reject only.
        return Math.Min(Math.Min(sweep.Base0.X, sweep.Tip0.X), Math.Min(sweep.Base1.X, sweep.Tip1.X)) <= radius
            && Math.Max(Math.Max(sweep.Base0.X, sweep.Tip0.X), Math.Max(sweep.Base1.X, sweep.Tip1.X)) >= -radius
            && Math.Min(Math.Min(sweep.Base0.Y, sweep.Tip0.Y), Math.Min(sweep.Base1.Y, sweep.Tip1.Y)) <= radius
            && Math.Max(Math.Max(sweep.Base0.Y, sweep.Tip0.Y), Math.Max(sweep.Base1.Y, sweep.Tip1.Y)) >= -radius
            && Math.Min(Math.Min(sweep.Base0.Z, sweep.Tip0.Z), Math.Min(sweep.Base1.Z, sweep.Tip1.Z)) <= radius
            && Math.Max(Math.Max(sweep.Base0.Z, sweep.Tip0.Z), Math.Max(sweep.Base1.Z, sweep.Tip1.Z)) >= -radius;
    }
    private static bool SampleFrame(Candidate entry, KittenEva owner, Sample sample, out CutPoint a, out CutPoint b)
    {
        a = b = default;
        if (entry.Vehicle.IsDisposed || !ReferenceEquals(entry.Vehicle.Parent, owner.Parent)
            || !ReferenceEquals(entry.Part.StructuralPart.Tree?.OwningVehicle, entry.Vehicle)
            || Program.GetMainCamera() is not { } camera) return false;
        var vehicle = entry.Vehicle.GetMatrixAsmb2Ego(camera);
        var target = entry.Part.MatrixAsmb2Ego(in vehicle);
        if (!double4x4.Invert(target, out var inverse)) return false;
        // Both transformations use this exact settled sample. Previous endpoints were
        // transformed using the previous target/owner transforms, including scale.
        var position = camera.GetPositionEgo(owner);
        var rotation = double4x4.CreateFromQuaternion(owner.Body2Ego);
        a = Point((sample.BaseBody.Transform(rotation) + position).Transform(inverse));
        b = Point((sample.TipBody.Transform(rotation) + position).Transform(inverse));
        return a.Finite && b.Finite;
    }
    private IEnumerable<int> Discover(KittenEva owner)
    {
        for (int i = 0; ReferenceEquals(_system, Universe.CurrentSystem) && i < Universe.CurrentSystem!.All.AsSpan().Length; i++)
        {
            var item = Universe.CurrentSystem!.All.AsSpan()[i];
            yield return 0;
            if (item is not Vehicle vehicle || vehicle is KittenEva || vehicle.IsDisposed || vehicle.IsEditedVehicle
                || !ReferenceEquals(vehicle.Parent, owner.Parent) || Program.GetMainCamera() is not { } camera) continue;
            double reach = (_sample.TipBody - _sample.BaseBody).Length();
            var baseEgo = _sample.BaseBody.Transform(owner.Body2Ego) + camera.GetPositionEgo(owner);
            if ((camera.GetPositionEgo(vehicle) - baseEgo).Length() > vehicle.BoundingSphereRadiusBody + reach
                + MaximumEndpointTravel * Math.Max(1d, _sample.VisualScale)) continue;
            for (int p = 0; !vehicle.IsDisposed && p < vehicle.Parts.Parts.Length; p++)
            {
                var full = vehicle.Parts.Parts[p]; yield return 0;
                if (full.IsAttachedInternal) continue;
                for (int s = -1; !vehicle.IsDisposed && ReferenceEquals(full.Tree?.OwningVehicle, vehicle) && s < full.SubParts.Length; s++)
                {
                    var part = s < 0 ? full : full.SubParts[s]; yield return 0;
                    for (int kind = 0; !vehicle.IsDisposed && ReferenceEquals(full.Tree?.OwningVehicle, vehicle) && kind < 4; kind++)
                    {
                        bool render = part.Modules.HasAny<PartModelModule>() || part.Modules.HasAny<PartModelDynamicModule>() || part.Modules.HasAny<PartModelGlassModule>();
                        if (kind == 3 && render) break;
                        int count = kind switch { 0 => part.Modules.Get<PartModelModule>().Length, 1 => part.Modules.Get<PartModelDynamicModule>().Length,
                            2 => part.Modules.Get<PartModelGlassModule>().Length, _ => part.Modules.Get<MeshViewModule>().Length };
                        for (int m = 0; !vehicle.IsDisposed && ReferenceEquals(full.Tree?.OwningVehicle, vehicle) && m < count; m++)
                        {
                            // Lists may be rebuilt between yields: recheck the indexed view.
                            MeshReference? mesh = kind switch {
                                0 when m < part.Modules.Get<PartModelModule>().Length => part.Modules.Get<PartModelModule>()[m].PartModel.Template.Mesh?.Get(),
                                1 when m < part.Modules.Get<PartModelDynamicModule>().Length => part.Modules.Get<PartModelDynamicModule>()[m].PartModelDynamic.Template.Mesh?.Get(),
                                2 when m < part.Modules.Get<PartModelGlassModule>().Length => part.Modules.Get<PartModelGlassModule>()[m].PartModelGlass.Template.Mesh?.Get(),
                                3 when m < part.Modules.Get<MeshViewModule>().Length => part.Modules.Get<MeshViewModule>()[m].MeshView, _ => null };
                            if (mesh != null && mesh.PositionsCompare != null) Add(part, vehicle, mesh, kind != 3);
                            yield return 0;
                        }
                    }
                }
            }
        }
    }
    private void Add(Part part, Vehicle vehicle, MeshReference mesh, bool immutableAsset)
    {
        foreach (var candidate in _candidates)
            if (ReferenceEquals(candidate.Part, part) && ReferenceEquals(candidate.Mesh, mesh) && ReferenceEquals(candidate.Vehicle, vehicle)) return;
        var entry = new Candidate(part, vehicle, mesh, immutableAsset, ++_nextOrdinal);
        if (_candidates.Count < CandidateLimit) _candidates.Add(entry);
        else
        {
            bool placed = false;
            // Preserve the candidate that owns each frozen cursor: evicting it
            // lets rediscovery create a duplicate concurrent query for that mesh.
            for (int i = 0; i < CandidateLimit; i++)
            {
                int slot = _replace; _replace = (_replace + 1) % CandidateLimit;
                var old = _candidates[slot];
                if (ReferenceEquals(_query?.Source, old) || HasMultiQuery(old)) continue;
                _candidates[slot] = entry; placed = true; break;
            }
            if (DebugTimings.Enabled) Saturations++;
            if (!placed) return;
        }
        if (mesh.PositionsCompare is { } primitives) ScheduleCapture(entry, primitives);
    }
    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 Game(CutPoint value) => new(value.X, value.Y, value.Z);
    private static bool PositiveAxes(float3 value) => float.IsFinite(value.X) && value.X > 0
        && float.IsFinite(value.Y) && value.Y > 0 && float.IsFinite(value.Z) && value.Z > 0;
    private static bool StableAxes(float3 a, float3 b) => Stable(a.X, b.X) && Stable(a.Y, b.Y) && Stable(a.Z, b.Z);
    private static bool Stable(float a, float b) => MathF.Abs(a - b) <= MathF.Max(a, b) * .0002f;
}
