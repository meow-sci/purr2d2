using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Autopilot;

// All coordinates are in target body space. Radius is the sampled hull radius;
// flight guidance adds its configured clearance when following the ring.
internal readonly record struct CutRing(double3 Center, double3 Axis, double3 Radial, double Radius);

internal static class CuttingRingGeometry
{
    internal const double KittenRadius = .25;
    private const int AzimuthSamples = 48;
    private const int OrbitSegments = 96;
    private const int MaxTriangles = 200_000;
    private const int MaxBounds = 64;
    private const int MaxCandidates = 1_536;
    private const int MaxTriangleTests = 4_000_000;

    internal static bool TryFind(CutTriangle[] triangles, IReadOnlyList<CutBounds> candidateBounds,
        double3 kittenBody, int axisIndex, bool reversed, double clearance,
        out CutRing ring, out string reason, double3? preferredPoint = null)
    {
        ring = default;
        if (!Finite(kittenBody) || (preferredPoint.HasValue && !Finite(preferredPoint.Value)) || axisIndex is < -1 or > 2
            || !double.IsFinite(clearance) || clearance < KittenRadius + .025)
        {
            reason = "Choose a valid cylinder axis and at least 0.28 m of hull clearance.";
            return false;
        }
        if (!TryMesh(triangles, out var mesh, out reason)) return false;
        if (candidateBounds.Count == 0)
        {
            reason = "The target has no available part bounds for a ring search.";
            return false;
        }
        if (candidateBounds.Count > MaxBounds)
        {
            reason = "Ring search part limit exceeded; use a simpler target.";
            return false;
        }
        foreach (var bounds in candidateBounds)
            if (!Valid(bounds))
            {
                reason = "Target part bounds contain invalid coordinates; capture again after loading finishes.";
                return false;
            }

        var search = new Search(mesh!);
        bool found = false, sawRoundSection = false, sawBlockedPath = false;
        double bestDistance = double.PositiveInfinity;
        int candidates = 0;
        foreach (var bounds in candidateBounds)
        {
            double3 boundsCenter = Vector((bounds.Min + bounds.Max) * .5);
            for (int axisNumber = 0; axisNumber < 3; axisNumber++)
            {
                if (axisIndex >= 0 && axisNumber != axisIndex) continue;
                double3 axis = CylinderOrbitGuidance.Axis(axisNumber, reversed);
                double minimum = Component(bounds.Min, axisNumber), maximum = Component(bounds.Max, axisNumber);
                double span = maximum - minimum;
                if (span < .08) continue;
                double margin = Math.Max(.02, span * .08);
                double lower = minimum + margin, upper = maximum - margin;
                if (lower >= upper) continue;
                double projected = Math.Clamp(Component(Point(kittenBody), axisNumber), lower, upper);
                double preferredStation = preferredPoint.HasValue ? Component(Point(preferredPoint.Value), axisNumber) : 0;
                if (preferredPoint.HasValue && (preferredStation < lower || preferredStation > upper)) continue;
                double[] stations = preferredPoint.HasValue ? [preferredStation]
                    : [projected, (minimum + maximum) * .5,
                        minimum + span * .15, minimum + span * .3,
                        minimum + span * .7, minimum + span * .85];
                var visited = new HashSet<double>();
                foreach (double station in stations)
                {
                    if (!visited.Add(station)) continue;
                    if (++candidates > MaxCandidates)
                    {
                        reason = "Ring search candidate limit exceeded; use a simpler target.";
                        return false;
                    }
                    double3 center = WithComponent(boundsCenter, axisNumber, station);
                    double3 radial = kittenBody - center;
                    radial -= axis * double3.Dot(radial, axis);
                    radial = radial.LengthSquared() > 1e-16 ? radial / radial.Length()
                        : axisNumber == 0 ? double3.UnitY : double3.UnitX;
                    double band = Math.Min(.08, Math.Min(station - minimum, maximum - station) * .4);
                    if (!search.TryRadius(center, axis, radial, band, out double surfaceRadius))
                    {
                        if (search.Exhausted) return BudgetFailure(out reason);
                        continue;
                    }
                    sawRoundSection = true;
                    var candidate = new CutRing(center, axis, radial, surfaceRadius);
                    double3 start = center + radial * (surfaceRadius + clearance);
                    double distance = preferredPoint.HasValue
                        ? (center - preferredPoint.Value).LengthSquared() + (start - kittenBody).LengthSquared() * .0001
                        : (start - kittenBody).LengthSquared();
                    if (distance >= bestDistance) continue;
                    if (!search.PathClear(kittenBody, start, KittenRadius)
                        || !search.OrbitClear(candidate, clearance))
                    {
                        if (search.Exhausted) return BudgetFailure(out reason);
                        sawBlockedPath = true;
                        continue;
                    }
                    ring = candidate;
                    bestDistance = distance;
                    found = true;
                }
            }
        }
        reason = found ? "Found a sampled circular hull section with a clear direct approach and orbit."
            : sawBlockedPath ? "A circular section was found, but its approach or orbit is obstructed. Move to a clearer side and try again."
            : sawRoundSection ? "No reachable cutting ring was found. Move to a clearer side and try again."
            : preferredPoint.HasValue
                ? "No clear circular ring exists at that selected station. Choose a point farther from the ends or another cylinder axis."
                : "No sufficiently circular hull section was found. Try Auto axis or a simpler cylindrical target.";
        return found;
    }

    internal static bool TryPick(CutTriangle[] triangles, double3 origin, double3 direction, out double3 hit)
    {
        hit = default;
        if (triangles.Length == 0 || triangles.Length > MaxTriangles || !Finite(origin) || !Finite(direction)
            || direction.LengthSquared() < 1e-20) return false;
        direction /= direction.Length();
        double nearest = double.PositiveInfinity;
        foreach (var triangle in triangles)
        {
            if (!triangle.A.Finite || !triangle.B.Finite || !triangle.C.Finite) return false;
            if (RayTriangle(Point(origin), Point(direction), triangle, out double distance)
                && distance >= 0 && distance < nearest) nearest = distance;
        }
        if (!double.IsFinite(nearest)) return false;
        hit = origin + direction * nearest;
        return Finite(hit);
    }

    internal static bool IsPathClear(CutTriangle[] triangles, double3 start, double3 end,
        double radius, out string reason)
    {
        if (!TryMesh(triangles, out var mesh, out reason)) return false;
        return IsPathClear(mesh!, start, end, radius, out reason);
    }

    internal static bool IsPathClear(CutMesh mesh, double3 start, double3 end,
        double radius, out string reason)
    {
        if (!Finite(start) || !Finite(end) || !double.IsFinite(radius) || radius <= 0)
        {
            reason = "The approach path or clearance is invalid.";
            return false;
        }
        var search = new Search(mesh);
        bool clear = search.PathClear(start, end, radius);
        if (search.Exhausted) return BudgetFailure(out reason);
        reason = clear ? "Approach path is clear." : "The direct approach is obstructed; move to a clearer side.";
        return clear;
    }

    private static bool TryMesh(CutTriangle[] triangles, out CutMesh? mesh, out string reason)
    {
        mesh = null;
        if (triangles.Length == 0)
        {
            reason = "Target surface geometry is unavailable; capture again after loading finishes.";
            return false;
        }
        if (triangles.Length > MaxTriangles)
        {
            reason = "Ring search triangle limit exceeded; use a simpler target.";
            return false;
        }
        foreach (var triangle in triangles)
            if (!triangle.A.Finite || !triangle.B.Finite || !triangle.C.Finite
                || CutPoint.Cross(triangle.B - triangle.A, triangle.C - triangle.A).LengthSquared < 1e-20)
            {
                reason = "Target surface geometry contains invalid triangles; capture another target.";
                return false;
            }
        mesh = new CutMesh(triangles);
        reason = string.Empty;
        return true;
    }

    private sealed class Search(CutMesh mesh)
    {
        private readonly List<int> _nearby = new();
        private int _tests;
        internal bool Exhausted { get; private set; }

        internal bool TryRadius(double3 center, double3 axis, double3 radial, double band, out double radius)
        {
            radius = 0;
            double minimum = double.PositiveInfinity, maximum = 0;
            double3 tangent = double3.Cross(axis, radial);
            foreach (double offset in new[] { 0.0, -band, band })
            {
                double3 planeCenter = center + axis * offset;
                for (int i = 0; i < AzimuthSamples; i++)
                {
                    double angle = 2 * Math.PI * i / AzimuthSamples;
                    double3 direction = radial * Math.Cos(angle) + tangent * Math.Sin(angle);
                    if (!OuterRadius(planeCenter, direction, out double sample) || sample < .04) return false;
                    minimum = Math.Min(minimum, sample);
                    maximum = Math.Max(maximum, sample);
                    // A circular orbit cannot keep a blade close to a square or irregular section.
                    if (maximum - minimum > Math.Min(.15, minimum * .12)) return false;
                }
            }
            radius = maximum;
            return double.IsFinite(radius);
        }

        private bool OuterRadius(double3 center, double3 radial, out double radius)
        {
            radius = 0;
            // An outward ray finds the farthest positive crossing, including an outer
            // wall when the section is hollow. This avoids assumptions about winding.
            if (!_extentReady)
            {
                foreach (var triangle in mesh.Triangles)
                {
                    _boundsMin = CutPoint.Min(_boundsMin, triangle.Min);
                    _boundsMax = CutPoint.Max(_boundsMax, triangle.Max);
                }
                _extentReady = true;
            }
            CutPoint c = Point(center);
            double extent = new CutPoint(Math.Max(Math.Abs(_boundsMin.X - c.X), Math.Abs(_boundsMax.X - c.X)),
                Math.Max(Math.Abs(_boundsMin.Y - c.Y), Math.Abs(_boundsMax.Y - c.Y)),
                Math.Max(Math.Abs(_boundsMin.Z - c.Z), Math.Abs(_boundsMax.Z - c.Z))).Length + 1;
            CutPoint origin = Point(center), direction = Point(radial), end = Point(center + radial * extent);
            mesh.Query(CutBounds.Sweep(origin, end, origin, end, 1e-8), _nearby);
            bool hit = false;
            foreach (int index in _nearby)
            {
                if (!Count()) return false;
                if (!RayTriangle(origin, direction, mesh.Triangles[index], out double distance)
                    || distance < 1e-7 || distance > extent) continue;
                radius = Math.Max(radius, distance);
                hit = true;
            }
            return hit;
        }

        private bool _extentReady;
        private CutPoint _boundsMin = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        private CutPoint _boundsMax = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

        internal bool PathClear(double3 start, double3 end, double radius)
        {
            CutPoint a = Point(start), b = Point(end);
            mesh.Query(CutBounds.Sweep(a, b, a, b, radius), _nearby);
            foreach (int index in _nearby)
            {
                if (!Count()) return false;
                if (CutGeometry.SegmentTriangleDistanceSquared(a, b, mesh.Triangles[index], out _) < radius * radius)
                    return false;
            }
            return true;
        }

        internal bool OrbitClear(CutRing ring, double clearance)
        {
            double flightRadius = ring.Radius + clearance;
            // Inflate each chord's capsule by its sagitta to cover the actual arc.
            double radius = KittenRadius + flightRadius * (1 - Math.Cos(Math.PI / OrbitSegments));
            double3 tangent = double3.Cross(ring.Axis, ring.Radial);
            double3 previous = ring.Center + ring.Radial * flightRadius;
            for (int i = 1; i <= OrbitSegments; i++)
            {
                double angle = 2 * Math.PI * i / OrbitSegments;
                double3 next = ring.Center + (ring.Radial * Math.Cos(angle) + tangent * Math.Sin(angle)) * flightRadius;
                if (!PathClear(previous, next, radius)) return false;
                previous = next;
            }
            return true;
        }

        private bool Count()
        {
            Exhausted = ++_tests > MaxTriangleTests;
            return !Exhausted;
        }
    }

    private static bool RayTriangle(CutPoint origin, CutPoint direction, CutTriangle triangle, out double distance)
    {
        distance = 0;
        CutPoint edge1 = triangle.B - triangle.A, edge2 = triangle.C - triangle.A;
        CutPoint p = CutPoint.Cross(direction, edge2);
        double determinant = CutPoint.Dot(edge1, p);
        if (Math.Abs(determinant) < 1e-12 * edge1.Length * edge2.Length) return false;
        double inverse = 1 / determinant;
        CutPoint offset = origin - triangle.A;
        double u = CutPoint.Dot(offset, p) * inverse;
        if (u < -1e-9 || u > 1 + 1e-9) return false;
        CutPoint q = CutPoint.Cross(offset, edge1);
        double v = CutPoint.Dot(direction, q) * inverse;
        if (v < -1e-9 || u + v > 1 + 1e-9) return false;
        distance = CutPoint.Dot(edge2, q) * inverse;
        return double.IsFinite(distance);
    }

    private static bool BudgetFailure(out string reason)
    {
        reason = "Ring search work limit exceeded; use a simpler target.";
        return false;
    }

    private static bool Valid(CutBounds bounds) => bounds.Min.Finite && bounds.Max.Finite
        && bounds.Min.X <= bounds.Max.X && bounds.Min.Y <= bounds.Max.Y && bounds.Min.Z <= bounds.Max.Z;
    private static bool Finite(double3 point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
    private static CutPoint Point(double3 point) => new(point.X, point.Y, point.Z);
    private static double3 Vector(CutPoint point) => new(point.X, point.Y, point.Z);
    private static double Component(CutPoint point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
    private static double3 WithComponent(double3 point, int axis, double value) => axis switch
    {
        0 => new(value, point.Y, point.Z),
        1 => new(point.X, value, point.Z),
        _ => new(point.X, point.Y, value),
    };
}
