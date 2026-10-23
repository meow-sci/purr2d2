using Brutal.Numerics;

namespace Purr2D2.Slicing;

internal static class SliceSourceFrame
{
    // Basis points check the complete affine transform without an allocation.
    // Isolation may introduce floating point roundoff when restoring a docked root.
    internal static bool SameTransform(double4x4 current, double4x4 expected)
    {
        ReadOnlySpan<double3> points = stackalloc double3[] { double3.Zero, double3.UnitX, double3.UnitY, double3.UnitZ };
        foreach (var point in points)
        {
            double distance = (double3.Transform(point, current) - double3.Transform(point, expected)).Length();
            if (!double.IsFinite(distance) || distance > 1e-7) return false;
        }
        return true;
    }
}
