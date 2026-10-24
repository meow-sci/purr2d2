using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

namespace Purr2D2.Rendering;

/// <summary>Settled-thread-only surface confirmation. Discovery, target-frame reads and
/// triangle work are all bounded. A deferred query owns immutable sweep endpoints.</summary>
internal sealed class ManualContactProbe
{
    internal const int TriangleBudget = 4096, DiscoveryBudget = 64, FrameBudget = 128, CandidateLimit = 128, PrimitiveBudget = 256;
    internal const double UpdateMilliseconds = .5, MaximumAge = .15, MaximumEndpointTravel = 1;
    internal readonly record struct Sample(ulong Gesture, double Time, double3 BaseBody, double3 TipBody, float2 Drag);
    internal readonly record struct Contact(Part Part, double3 PointLocal, double3 NormalLocal,
        double DistanceMeters, double BladeLength, double4x4 SourceTransform, double4x4 PartTransform);
    private sealed class Candidate(Part part, Vehicle vehicle, MeshReference mesh)
    {
        internal readonly Part Part = part;
        internal readonly Vehicle Vehicle = vehicle;
        internal readonly MeshReference Mesh = mesh;
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
    }
    private readonly List<Candidate> _candidates = new(CandidateLimit);
    private IEnumerator<int>? _discovery;
    private KittenEva? _owner;
    private object? _system;
    private ulong _gesture;
    private int _candidate, _replace;
    private Query? _query;
    private Sample _sample;
    internal double LastUpdateMilliseconds { get; private set; }
    internal double MaximumUpdateMilliseconds { get; private set; }
    internal int PrimitiveReads { get; private set; }
    internal int TestedTriangles { get; private set; }
    internal int DiscoveryUnits { get; private set; }
    internal int FrameReads { get; private set; }
    internal int Saturations { get; private set; }
    internal int ExpiredQueries { get; private set; }
    internal bool Pending => _query != null;
    // Tests may disable only the soft clock, retaining every hard work limit.
    internal double SoftBudgetMilliseconds { get; set; } = UpdateMilliseconds;

    internal void Reset()
    {
        _discovery?.Dispose(); _discovery = null;
        _candidates.Clear(); _query = null; _owner = null; _system = null; _gesture = 0;
        _candidate = _replace = 0;
    }

    internal bool Advance(KittenEva owner, Sample sample, out Contact contact)
    {
        long started = Stopwatch.GetTimestamp();
        try { return AdvanceCore(owner, sample, out contact); }
        finally
        {
            LastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            MaximumUpdateMilliseconds = Math.Max(MaximumUpdateMilliseconds, LastUpdateMilliseconds);
        }
    }
    private bool AdvanceCore(KittenEva owner, Sample sample, out Contact contact)
    {
        contact = default; TestedTriangles = DiscoveryUnits = FrameReads = PrimitiveReads = 0;
        if (sample.Gesture == 0 || !double.IsFinite(sample.Time) || !Point(sample.BaseBody).Finite || !Point(sample.TipBody).Finite
            || owner.IsDisposed || Program.EditorFlag || Program.GetMainCamera() == null || Universe.CurrentSystem == null)
        { Reset(); return false; }
        if (!ReferenceEquals(_owner, owner) || !ReferenceEquals(_system, Universe.CurrentSystem) || _gesture != sample.Gesture)
        { Reset(); _owner = owner; _system = Universe.CurrentSystem; _gesture = sample.Gesture; }
        _sample = sample;
        long started = Stopwatch.GetTimestamp();
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
        if (_query != null && !Current(_query, owner, sample.Time)) { _query = null; ExpiredQueries++; }
        while (WithinBudget(started) && TestedTriangles < TriangleBudget)
        {
            if (_query != null)
            {
                var query = _query;
                while (query.Primitive < query.Primitives.Length)
                {
                    if (query.Triangle == 0)
                    {
                        if (PrimitiveReads >= PrimitiveBudget) { Saturations++; return false; }
                        PrimitiveReads++;
                    }
                    var vertices = query.Primitives[query.Primitive];
                    if (vertices == null || query.Triangle + 2 >= vertices.Length)
                    { query.Primitive++; query.Triangle = 0; if (!WithinBudget(started)) { Saturations++; return false; } continue; }
                    int index = query.Triangle; query.Triangle += 3; TestedTriangles++;
                    var triangle = new CutTriangle(Point(vertices[index]), Point(vertices[index + 1]), Point(vertices[index + 2]));
                    if (query.Sweep.TryHit(triangle, out var hit, out double along))
                    {
                        contact = new(query.Source.Part, Game(hit), Game(triangle.Normal), along * query.Length,
                            query.Length, query.SourceTransform, query.PartTransform);
                        _query = null; return true;
                    }
                    if (TestedTriangles >= TriangleBudget || !WithinBudget(started))
                    { Saturations++; return false; }
                }
                _query = null;
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
            bool motion = entry.HasPrevious && previous.Gesture == sample.Gesture
                && sample.Time > previous.Time && sample.Time - previous.Time <= MaximumAge
                && (sample.Drag - previous.Drag).LengthSquared() >= .000001f
                && travel >= .000004
                && baseTravel <= MaximumEndpointTravel * MaximumEndpointTravel && travel <= endpointLimit * endpointLimit
                && Math.Abs(length - (previous.TipBody - previous.BaseBody).Length()) <= .001
                && entry.SourceTransform.Equals(sourceTransform) && sweep.Subdivisions <= BladeSweep.MaximumSubdivisions;
            entry.Base = a; entry.Tip = b; entry.Previous = sample; entry.HasPrevious = true; entry.SourceTransform = sourceTransform;
            if (motion && BoundsCouldHit(sweep, entry.Mesh.BoundingSphereRadius) && entry.Mesh.PositionsCompare is { } primitives)
                _query = new(entry, sweep, sample, length, sourceTransform, partTransform, primitives);
        }
        if (!WithinBudget(started) || _query != null || DiscoveryUnits == DiscoveryBudget || FrameReads == FrameBudget) Saturations++;
        return false;
    }
    private bool WithinBudget(long started, double fraction = 1) => Stopwatch.GetElapsedTime(started).TotalMilliseconds < SoftBudgetMilliseconds * fraction;
    private static bool Current(Query query, KittenEva owner, double now) => now >= query.Sample.Time && now - query.Sample.Time <= MaximumAge
        && !query.Source.Vehicle.IsDisposed && ReferenceEquals(query.Source.Vehicle.Parent, owner.Parent)
        && ReferenceEquals(query.Source.Part.StructuralPart.Tree?.OwningVehicle, query.Source.Vehicle)
        && query.Source.Part.StructuralPart.MatrixAsmb2VehicleAsmb.Equals(query.SourceTransform)
        && query.Source.Part.MatrixAsmb2VehicleAsmb.Equals(query.PartTransform)
        && ReferenceEquals(query.Source.Mesh.PositionsCompare, query.Primitives);
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
            if ((camera.GetPositionEgo(vehicle) - baseEgo).Length() > vehicle.BoundingSphereRadiusBody + reach + MaximumEndpointTravel) continue;
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
                            if (mesh != null && mesh.PositionsCompare != null) Add(part, vehicle, mesh);
                            yield return 0;
                        }
                    }
                }
            }
        }
    }
    private void Add(Part part, Vehicle vehicle, MeshReference mesh)
    {
        foreach (var candidate in _candidates)
            if (ReferenceEquals(candidate.Part, part) && ReferenceEquals(candidate.Mesh, mesh) && ReferenceEquals(candidate.Vehicle, vehicle)) return;
        var entry = new Candidate(part, vehicle, mesh);
        if (_candidates.Count < CandidateLimit) _candidates.Add(entry);
        else { _candidates[_replace++ % CandidateLimit] = entry; Saturations++; }
    }
    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 Game(CutPoint value) => new(value.X, value.Y, value.Z);
}
