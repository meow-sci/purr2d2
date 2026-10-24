using System;
using Purr2D2.Core;

namespace Purr2D2.Rendering;

internal readonly record struct DarkSaberSparkTuning(int Count, float Frequency, float Size,
    float Brightness, float Lifetime, float Speed)
{
    internal const int MaximumBursts = 16;
    internal const int MaximumSources = 32;
    internal static DarkSaberSparkTuning FromEffects(SaberEffectSettings effects) => new(
        Math.Clamp(effects.SparkCount, 0, SaberSettings.MaximumSparkCount),
        SaberVfx.Clamp(effects.SparkFrequency, 0, SaberSettings.MaximumSparkFrequency, 4),
        SaberVfx.Clamp(effects.SparkSize, .25f, 3, 1),
        SaberVfx.Clamp(effects.SparkBrightness, 0, 4, 1),
        SaberVfx.Clamp(effects.SparkLifetime, .1f, .8f, .45f),
        SaberVfx.Clamp(effects.SparkSpeed, .25f, 3, 1));

    internal bool Enabled => Count > 0 && Frequency > 0 && Brightness > 0;

    // Independent of arc settings, and cheap to reject before recording any spark commands.
    internal static float Visibility(double distance) => !double.IsFinite(distance) || distance < 0
        ? 0 : (float)Math.Clamp((24 - distance) / 12, 0, 1);
}

/// <summary>One emission per player-time window, independent of viewport count; no catch-up bursts.</summary>
internal struct DarkSaberSparkClock
{
    private bool _initialized;
    private double _lastTime, _lastEmission;
    private uint _sequence;

    internal bool TryEmit(double now, float frequency, float ownerSeed, out float seed)
    {
        seed = 0;
        if (!double.IsFinite(now) || now < 0 || !float.IsFinite(frequency) || frequency <= 0)
        { this = default; return false; }
        frequency = Math.Min(frequency, SaberSettings.MaximumSparkFrequency);
        if (_initialized && now < _lastTime) { this = default; }
        _lastTime = now;
        if (_initialized && now - _lastEmission < 1 / (double)frequency) return false;
        _initialized = true;
        _lastEmission = now;
        // Bounded deterministic keys keep GLSL hash precision in long sessions.
        seed = ownerSeed * 97 + (_sequence++ % 1024) * 13;
        return true;
    }
}
