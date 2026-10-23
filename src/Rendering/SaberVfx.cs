using System;

namespace Purr2D2.Rendering;

internal readonly record struct SaberVfxDetail(int ArcCount, int Segments, float ArcVisibility,
    float ShimmerStrength, float ArcLevel);

/// <summary>Pure, bounded cosmetic detail policy; no simulation or graphics resources.</summary>
internal static class SaberVfx
{
    internal const int MaximumArcCount = 3;
    internal const int MinimumArcSegments = 6;
    internal const int MaximumArcSegments = 10;
    internal const double ClockPeriod = 256;

    // Do not use string.GetHashCode: its randomized seed changes between game launches.
    internal static float Seed(string id)
    {
        uint hash = 2166136261;
        foreach (var c in id) hash = unchecked((hash ^ c) * 16777619);
        return (hash & 65535) / 65536f;
    }

    // All shader time frequencies and temporal-noise periods repeat at this boundary.
    internal static float Clock(double now)
    {
        if (!double.IsFinite(now) || now <= 0) return 0;
        float value = (float)(now % ClockPeriod);
        return value < ClockPeriod ? value : 0;
    }

    internal static SaberVfxDetail Detail(float projectedLengthPixels, float projectedRadiusPixels,
        double distanceMeters, float bladeLengthMeters)
    {
        if (!float.IsFinite(projectedLengthPixels) || !float.IsFinite(projectedRadiusPixels)
            || !double.IsFinite(distanceMeters) || !float.IsFinite(bladeLengthMeters)
            || projectedLengthPixels < 0 || projectedRadiusPixels < 0 || distanceMeters < 0
            || bladeLengthMeters <= 0) return default;

        // Use the visible tip diameter for an end-on blade too.
        float footprint = Math.Max(projectedLengthPixels, projectedRadiusPixels * 2);
        float shimmer = .06f * Smooth(8, 36, footprint) * Smooth(.4f, 1.8f, projectedRadiusPixels)
            * (1 - Smooth(32, 64, (float)distanceMeters));
        float visibility = Smooth(24, 80, footprint) * Smooth(1, 3.5f, projectedRadiusPixels)
            * (1 - Smooth(18, 40, (float)distanceMeters)) * Smooth(.12f, .24f, bladeLengthMeters);
        if (visibility <= 0) return new(0, 0, 0, shimmer, 0);

        // Newly admitted instances fade in before they contribute at full strength.
        float level = 1 + 2 * Smooth(2.5f, 6, projectedRadiusPixels) * Smooth(80, 180, footprint)
            * (1 - Smooth(8, 18, (float)distanceMeters));
        int arcs = Math.Clamp((int)Math.Ceiling(level), 1, MaximumArcCount);
        int segments = projectedRadiusPixels >= 3.5f && distanceMeters < 10
            ? MaximumArcSegments : projectedRadiusPixels >= 2 ? 8 : MinimumArcSegments;
        return new(arcs, segments, visibility, shimmer, level);
    }

    private static float Smooth(float minimum, float maximum, float value)
    {
        float t = float.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
