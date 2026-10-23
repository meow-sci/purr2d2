using System;
using Purr2D2.Core;

namespace Purr2D2.Rendering;

internal readonly record struct SaberVfxDetail(int ArcCount, int Segments, float ArcVisibility,
    float ShimmerStrength, float ArcLevel);

internal readonly record struct SaberVfxTuning(float ShimmerAmount, float ShimmerSpeed, int ArcCount,
    float ArcFrequency, float ArcAmplitude, float ArcBrightness, float ArcWidth)
{
    internal static SaberVfxTuning Default => new(1, 1, 3, 1, 1, 1, 1);
    internal static SaberVfxTuning LightDefault => new(.5f, .6f, 1, .35f, .5f, .65f, .8f);
    internal static SaberVfxTuning DarkDefault => new(1.5f, 1.5f, 3, 1.5f, 1.3f, 1.1f, 1);

    internal static SaberVfxTuning FromEffects(SaberEffectSettings effects, SaberType type) =>
        new SaberVfxTuning(effects.ShimmerAmount, effects.ShimmerSpeed, effects.ArcCount,
            effects.ArcFrequency, effects.ArcAmplitude, effects.ArcBrightness, effects.ArcWidth)
            .Clamped(type == SaberType.Dark ? DarkDefault : LightDefault);

    internal SaberVfxTuning Clamped() => Clamped(Default);

    private SaberVfxTuning Clamped(SaberVfxTuning fallback) => new(
        SaberVfx.Clamp(ShimmerAmount, 0, 4, fallback.ShimmerAmount),
        SaberVfx.Clamp(ShimmerSpeed, 0, 4, fallback.ShimmerSpeed),
        Math.Clamp(ArcCount, 0, SaberSettings.MaximumArcCount),
        SaberVfx.Clamp(ArcFrequency, .1f, 4, fallback.ArcFrequency),
        SaberVfx.Clamp(ArcAmplitude, 0, 3, fallback.ArcAmplitude),
        SaberVfx.Clamp(ArcBrightness, 0, 4, fallback.ArcBrightness),
        SaberVfx.Clamp(ArcWidth, .25f, 3, fallback.ArcWidth));
}

/// <summary>Continuous rate-scaled phases; changing a setting never multiplies an already wrapped clock.</summary>
internal struct SaberVfxClock
{
    private bool _initialized;
    private bool _anchored;
    private double _lastTime;
    private double _shimmerPhase;
    private double _burstPhase;

    internal readonly float ShimmerTime => SaberVfx.Clock(_shimmerPhase);
    internal readonly float BurstTime => SaberVfx.Clock(_burstPhase);

    internal void Advance(double now, float shimmerSpeed, float arcFrequency)
    {
        if (!double.IsFinite(now) || now < 0) { _anchored = false; return; }
        shimmerSpeed = SaberVfx.Clamp(shimmerSpeed, 0, 4, 1);
        arcFrequency = SaberVfx.Clamp(arcFrequency, .1f, 4, 1);
        if (!_anchored)
        {
            _anchored = true;
            _lastTime = now;
            if (!_initialized)
            {
                _initialized = true;
                _shimmerPhase = AdvancePhase(0, now, shimmerSpeed);
                _burstPhase = AdvancePhase(0, now, arcFrequency);
            }
            return;
        }

        double delta = now - _lastTime;
        _lastTime = now;
        // World-clock resets re-anchor without rewinding or advancing either visual phase.
        if (!double.IsFinite(delta) || delta <= 0) return;
        _shimmerPhase = AdvancePhase(_shimmerPhase, delta, shimmerSpeed);
        _burstPhase = AdvancePhase(_burstPhase, delta, arcFrequency);
    }

    private static double AdvancePhase(double phase, double elapsed, float rate)
    {
        if (rate <= 0) return phase;
        // Modulo in source seconds prevents overflow even for a very large clock jump.
        double increment = (elapsed % (SaberVfx.ClockPeriod / rate)) * rate;
        return (phase + increment) % SaberVfx.ClockPeriod;
    }
}

/// <summary>Pure, bounded cosmetic detail policy; no simulation or graphics resources.</summary>
internal static class SaberVfx
{
    internal const int MaximumArcCount = SaberSettings.MaximumArcCount;
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
        double distanceMeters, float bladeLengthMeters) => Detail(projectedLengthPixels,
            projectedRadiusPixels, distanceMeters, bladeLengthMeters, SaberVfxTuning.Default);

    internal static SaberVfxDetail Detail(float projectedLengthPixels, float projectedRadiusPixels,
        double distanceMeters, float bladeLengthMeters, SaberVfxTuning tuning)
    {
        if (!float.IsFinite(projectedLengthPixels) || !float.IsFinite(projectedRadiusPixels)
            || !double.IsFinite(distanceMeters) || !float.IsFinite(bladeLengthMeters)
            || projectedLengthPixels < 0 || projectedRadiusPixels < 0 || distanceMeters < 0
            || bladeLengthMeters <= 0) return default;

        tuning = tuning.Clamped();
        // Use the visible tip diameter for an end-on blade too.
        float footprint = Math.Max(projectedLengthPixels, projectedRadiusPixels * 2);
        float shimmer = .06f * Smooth(8, 36, footprint) * Smooth(.4f, 1.8f, projectedRadiusPixels)
            * (1 - Smooth(32, 64, (float)distanceMeters)) * tuning.ShimmerAmount;
        if (tuning.ArcCount == 0 || tuning.ArcBrightness <= 0) return new(0, 0, 0, shimmer, 0);
        float visibility = Smooth(24, 80, footprint) * Smooth(1, 3.5f, projectedRadiusPixels)
            * (1 - Smooth(18, 40, (float)distanceMeters)) * Smooth(.12f, .24f, bladeLengthMeters);
        if (visibility <= 0) return new(0, 0, 0, shimmer, 0);

        // Newly admitted instances fade in before they contribute at full strength.
        float level = 1 + (tuning.ArcCount - 1) * Smooth(2.5f, 6, projectedRadiusPixels) * Smooth(80, 180, footprint)
            * (1 - Smooth(8, 18, (float)distanceMeters));
        int arcs = Math.Clamp((int)Math.Ceiling(level), 1, tuning.ArcCount);
        int segments = projectedRadiusPixels >= 3.5f && distanceMeters < 10
            ? MaximumArcSegments : projectedRadiusPixels >= 2 ? 8 : MinimumArcSegments;
        return new(arcs, segments, visibility, shimmer, level);
    }

    internal static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static float Smooth(float minimum, float maximum, float value)
    {
        float t = float.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
