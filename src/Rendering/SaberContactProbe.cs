using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

namespace Purr2D2.Rendering;

/// <summary>Bounded, read-only blade contact for sparks. Never prepares or mutates cut geometry.</summary>
internal sealed class SaberContactProbe
{
    internal const double SampleInterval = .05, RefreshInterval = .5;
    internal const int TriangleBudget = 4096, MeshBudget = 256, EntryBudget = 32768;
    private readonly record struct Entry(Part Part, Vehicle Vehicle, MeshReference Mesh);
    internal readonly record struct Contact(Part Part, double3 PointLocal, double3 NormalLocal);
    private readonly List<Entry> _entries = new();
    private KittenEva? _owner;
    private Entry _last;
    private int _lastPrimitive, _lastTriangle;
    private bool _hasLast;
    private int _entry, _primitive, _triangle;
    private double _nextSample, _nextRefresh;
    internal int TestedTriangles { get; private set; }

    internal void Reset()
    {
        _entries.Clear(); _owner = null; _hasLast = false; _last = default;
        _entry = _primitive = _triangle = TestedTriangles = 0;
        _nextSample = _nextRefresh = 0;
    }

    internal bool TrySample(KittenEva owner, double3 baseBody, double3 tipBody, double now, out Contact contact)
    {
        contact = default; TestedTriangles = 0;
        if (!ReferenceEquals(_owner, owner)) { Reset(); _owner = owner; }
        if (Program.EditorFlag || owner.IsDisposed || Universe.CurrentSystem == null || !double.IsFinite(now)
            || !Point(baseBody).Finite || !Point(tipBody).Finite || now < _nextSample
            || Program.GetMainCamera() is not { } camera) return false;
        _nextSample = now + SampleInterval;
        var bodyToEgo = double4x4.CreateFromQuaternion(owner.Body2Ego);
        var origin = baseBody.Transform(bodyToEgo) + camera.GetPositionEgo(owner);
        var end = tipBody.Transform(bodyToEgo) + camera.GetPositionEgo(owner);
        double reach = (end - origin).Length();
        if (reach < .01 || !double.IsFinite(reach)) return false;
        // A sustained touch costs one triangle test, with current transforms. No stale-contact sparks.
        if (_hasLast && TryFrame(_last, out var a, out var b)
            && _last.Mesh.PositionsCompare is { } previous && _lastPrimitive < previous.Length
            && previous[_lastPrimitive] is { } vertices && _lastTriangle + 2 < vertices.Length
            && Hit(vertices, _lastTriangle, a, b, _last.Part, out contact)) return true;
        _hasLast = false;
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + RefreshInterval;
            Refresh(owner, camera, origin, reach);
        }
        int meshes = 0;
        while (_entries.Count > 0 && meshes++ < Math.Min(MeshBudget, _entries.Count) && TestedTriangles < TriangleBudget)
        {
            if (_entry >= _entries.Count) _entry = 0;
            var entry = _entries[_entry];
            if (TryFrame(entry, out a, out b) && entry.Mesh.PositionsCompare is { } primitives)
            {
                for (; _primitive < primitives.Length; _primitive++, _triangle = 0)
                {
                    var positions = primitives[_primitive];
                    if (positions == null) continue;
                    for (; _triangle + 2 < positions.Length; _triangle += 3)
                    {
                        if (TestedTriangles >= TriangleBudget) return false; // Resume fairly next sample.
                        if (!Hit(positions, _triangle, a, b, entry.Part, out contact)) continue;
                        _last = entry; _lastPrimitive = _primitive; _lastTriangle = _triangle; _hasLast = true;
                        NextEntry(); return true;
                    }
                }
            }
            NextEntry();
        }
        return false;

        bool TryFrame(Entry entry, out CutPoint a, out CutPoint b)
        {
            a = b = default;
            if (entry.Vehicle.IsDisposed || !ReferenceEquals(entry.Vehicle.Parent, owner.Parent)
                || !ReferenceEquals(entry.Part.FullPart.Tree?.OwningVehicle, entry.Vehicle)) return false;
            var vehicleMatrix = entry.Vehicle.GetMatrixAsmb2Ego(camera);
            var matrix = entry.Part.MatrixAsmb2Ego(in vehicleMatrix);
            if (!double4x4.Invert(matrix, out var inverse)) return false;
            a = Point(origin.Transform(inverse)); b = Point(end.Transform(inverse));
            var d = b - a;
            double fraction = d.LengthSquared > 1e-20 ? Math.Clamp(-CutPoint.Dot(a, d) / d.LengthSquared, 0, 1) : 0;
            double radius = entry.Mesh.BoundingSphereRadius;
            return a.Finite && b.Finite && double.IsFinite(radius) && (a + d * fraction).LengthSquared <= radius * radius;
        }
    }

    private bool Hit(double3[] vertices, int index, CutPoint a, CutPoint b, Part part, out Contact contact)
    {
        TestedTriangles++;
        var triangle = new CutTriangle(Point(vertices[index]), Point(vertices[index + 1]), Point(vertices[index + 2]));
        if (TryIntersect(a, b, triangle, out var hit))
        { contact = new(part, Game(hit), Game(triangle.Normal)); return true; }
        contact = default; return false;
    }

    private void NextEntry() { _entry++; _primitive = _triangle = 0; }

    private void Refresh(KittenEva owner, Camera camera, double3 origin, double reach)
    {
        // Reuse storage. Module lists and native CPU vertices are already allocated by KSA.
        var resume = _entry < _entries.Count ? _entries[_entry] : default;
        _entries.Clear();
        foreach (var item in Universe.CurrentSystem!.All.AsSpan())
        {
            if (item is not Vehicle vehicle || vehicle is KittenEva || vehicle.IsDisposed || vehicle.IsEditedVehicle
                || !ReferenceEquals(vehicle.Parent, owner.Parent)
                || (camera.GetPositionEgo(vehicle) - origin).Length() > vehicle.BoundingSphereRadiusBody + reach + 2) continue;
            foreach (var part in vehicle.Parts.Parts)
            {
                if (part.IsAttachedInternal) continue;
                Add(part, vehicle);
                foreach (var sub in part.SubParts) Add(sub, vehicle);
                if (_entries.Count >= EntryBudget) break;
            }
            if (_entries.Count >= EntryBudget) break;
        }
        if (_entry >= _entries.Count || !_entries[_entry].Equals(resume)) _entry = _primitive = _triangle = 0;
    }

    private void Add(Part part, Vehicle vehicle)
    {
        bool render = false;
        foreach (var model in part.Modules.Get<PartModelModule>()) { render = true; AddMesh(model.PartModel.Template.Mesh?.Get()); }
        foreach (var model in part.Modules.Get<PartModelDynamicModule>()) { render = true; AddMesh(model.PartModelDynamic.Template.Mesh?.Get()); }
        foreach (var model in part.Modules.Get<PartModelGlassModule>()) { render = true; AddMesh(model.PartModelGlass.Template.Mesh?.Get()); }
        if (!render) foreach (var view in part.Modules.Get<MeshViewModule>()) AddMesh(view.MeshView);
        void AddMesh(MeshReference? mesh)
        { if (mesh != null && _entries.Count < EntryBudget) _entries.Add(new(part, vehicle, mesh)); }
    }

    // Two-sided, current blade segment only: no sweep history, coverage, heat, or damage.
    internal static bool TryIntersect(CutPoint a, CutPoint b, CutTriangle triangle, out CutPoint hit)
    {
        hit = default;
        var direction = b - a; var edge1 = triangle.B - triangle.A; var edge2 = triangle.C - triangle.A;
        var cross = CutPoint.Cross(direction, edge2); double determinant = CutPoint.Dot(edge1, cross);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-14) return false;
        double inverse = 1 / determinant;
        var delta = a - triangle.A; double u = CutPoint.Dot(delta, cross) * inverse;
        if (u < 0 || u > 1) return false;
        var q = CutPoint.Cross(delta, edge1); double v = CutPoint.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        double fraction = CutPoint.Dot(edge2, q) * inverse;
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1) return false;
        hit = a + direction * fraction; return hit.Finite;
    }
    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 Game(CutPoint value) => new(value.X, value.Y, value.Z);
}
