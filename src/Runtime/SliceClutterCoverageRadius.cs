using Brutal.Numerics;

namespace Purr2D2.Runtime;

/// <summary>Conservative owner-origin reach for current/recent blade endpoints and
/// pending ground-cell bounds, all expressed in the same celestial-fixed frame.</summary>
internal struct SliceClutterCoverageRadius(double3 origin)
{
    private readonly double3 _origin = origin;
    private double _radius;
    internal bool Include(double3 center, double radius = 0)
    {
        if (!Finite(_origin) || !Finite(center) || !double.IsFinite(radius) || radius < 0) return false;
        double distance = (center - _origin).Length();
        double reach = distance + radius;
        if (!double.IsFinite(reach)) return false;
        _radius = Math.Max(_radius, reach);
        return true;
    }
    internal double Reach => _radius + 2;
    private static bool Finite(double3 point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}
