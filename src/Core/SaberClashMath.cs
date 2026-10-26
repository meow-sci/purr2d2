using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Finite rendered blade segment tests. This samples poses; it is not continuous collision detection.</summary>
internal static class SaberClashMath
{
    internal readonly record struct Segment(double3 Start, double3 End);
    internal readonly record struct Closest(double3 OnA, double3 OnB, double DistanceSquared);

    internal static bool IsFinite(Segment segment) => Finite(segment.Start) && Finite(segment.End);

    /// <summary>Cheap lower bound used by the broad phase before the segment solver.</summary>
    internal static double AabbDistanceSquared(Segment a, Segment b)
    {
        double ax0 = Math.Min(a.Start.X, a.End.X), ax1 = Math.Max(a.Start.X, a.End.X);
        double ay0 = Math.Min(a.Start.Y, a.End.Y), ay1 = Math.Max(a.Start.Y, a.End.Y);
        double az0 = Math.Min(a.Start.Z, a.End.Z), az1 = Math.Max(a.Start.Z, a.End.Z);
        double bx0 = Math.Min(b.Start.X, b.End.X), bx1 = Math.Max(b.Start.X, b.End.X);
        double by0 = Math.Min(b.Start.Y, b.End.Y), by1 = Math.Max(b.Start.Y, b.End.Y);
        double bz0 = Math.Min(b.Start.Z, b.End.Z), bz1 = Math.Max(b.Start.Z, b.End.Z);
        double dx = Gap(ax0, ax1, bx0, bx1);
        double dy = Gap(ay0, ay1, by0, by1);
        double dz = Gap(az0, az1, bz0, bz1);
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Closest points of two finite segments, including parallel and degenerate segments.</summary>
    internal static bool TryClosest(Segment a, Segment b, out Closest closest)
    {
        closest = default;
        if (!IsFinite(a) || !IsFinite(b)) return false;

        double3 d1 = a.End - a.Start;
        double3 d2 = b.End - b.Start;
        double3 r = a.Start - b.Start;
        double aa = double3.Dot(d1, d1);
        double ee = double3.Dot(d2, d2);
        double f = double3.Dot(d2, r);
        const double DegenerateSquared = 1e-24;
        double s, t;

        if (aa <= DegenerateSquared && ee <= DegenerateSquared)
        {
            s = t = 0;
        }
        else if (aa <= DegenerateSquared)
        {
            s = 0;
            t = Clamp01(f / ee);
        }
        else
        {
            double c = double3.Dot(d1, r);
            if (ee <= DegenerateSquared)
            {
                t = 0;
                s = Clamp01(-c / aa);
            }
            else
            {
                double bb = double3.Dot(d1, d2);
                double denominator = aa * ee - bb * bb;
                // Parallel segments choose A's first endpoint; clamping B below
                // still finds a valid minimum and avoids unstable division.
                s = denominator > DegenerateSquared * Math.Max(aa, ee)
                    ? Clamp01((bb * f - c * ee) / denominator) : 0;
                t = (bb * s + f) / ee;
                if (t < 0)
                {
                    t = 0;
                    s = Clamp01(-c / aa);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Clamp01((bb - c) / aa);
                }
            }
        }

        double3 onA = a.Start + d1 * s;
        double3 onB = b.Start + d2 * t;
        double3 delta = onA - onB;
        double distanceSquared = double3.Dot(delta, delta);
        if (!Finite(onA) || !Finite(onB) || !double.IsFinite(distanceSquared) || distanceSquared < 0) return false;
        closest = new(onA, onB, distanceSquared);
        return true;
    }

    internal static bool TryCapsuleContact(Segment a, Segment b, double radius, out Closest closest)
    {
        closest = default;
        if (!double.IsFinite(radius) || radius < 0 || !TryClosest(a, b, out closest)) return false;
        return closest.DistanceSquared <= radius * radius;
    }

    private static double Gap(double minA, double maxA, double minB, double maxB) =>
        maxA < minB ? minB - maxA : maxB < minA ? minA - maxB : 0;
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
