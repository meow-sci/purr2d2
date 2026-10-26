namespace Purr2D2.Core;

/// <summary>Bounded, transient automatic saber-swing phase for one kitten.</summary>
public sealed class SaberAutoSwing
{
    public const float DefaultDurationSeconds = .5f;
    public const float MinimumDurationSeconds = .5f;
    public const float MaximumDurationSeconds = 3f;
    public const double EntryDurationSeconds = .15;

    private double _phase;
    private double _entryTime;

    public bool Enabled { get; private set; }
    public float DurationSeconds { get; private set; } = DefaultDurationSeconds;
    public double Phase => _phase;
    public float EntryBlend
    {
        get
        {
            float t = (float)Math.Clamp(_entryTime / EntryDurationSeconds, 0, 1);
            return t * t * (3 - 2 * t);
        }
    }

    public bool SetDuration(float seconds)
    {
        if (!float.IsFinite(seconds)) return false;
        DurationSeconds = Math.Clamp(seconds, MinimumDurationSeconds, MaximumDurationSeconds);
        return true;
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Enabled = false;
            _phase = 0;
            _entryTime = 0;
            return;
        }
        if (Enabled) return;
        Enabled = true;
        _phase = 0;
        _entryTime = 0;
    }

    public void Toggle() => SetEnabled(!Enabled);

    /// <summary>Advance only by supported caller-supplied player time; invalid/paused time freezes.</summary>
    public void Advance(double dtPlayer)
    {
        if (!Enabled) return;
        double dt = double.IsFinite(dtPlayer) ? Math.Clamp(dtPlayer, 0, .1) : 0;
        if (dt <= 0) return;
        _phase += dt / DurationSeconds;
        if (_phase >= 2) _phase -= 2;
        _entryTime = Math.Min(EntryDurationSeconds, _entryTime + dt);
    }

    /// <summary>Ease-in/out horizontal stroke and shallow squared-sine arch, eased from neutral on entry.</summary>
    public void GetOffset(out float x, out float y)
    {
        double progress = _phase <= 1 ? _phase : 2 - _phase;
        float blend = EntryBlend;
        double sin = Math.Sin(Math.PI * progress);
        double stroke = StrokePosition(progress);
        x = (float)(.8 * (2 * stroke - 1)) * blend;
        y = (float)(-.18 * sin * sin) * blend;
    }

    /// <summary>Normalized one-way stroke position with smooth, slow endpoints and a faster middle.</summary>
    internal static double StrokePosition(double progress)
    {
        if (!double.IsFinite(progress)) return 0;
        double p = Math.Clamp(progress, 0, 1);
        const double easeWidth = .2;
        double scale = easeWidth / (1 - easeWidth);
        if (p < easeWidth)
        {
            double u = p / easeWidth;
            return scale * (u * u * u - .5 * u * u * u * u);
        }
        if (p > 1 - easeWidth)
        {
            double u = (1 - p) / easeWidth;
            return 1 - scale * (u * u * u - .5 * u * u * u * u);
        }
        return (p - .5 * easeWidth) / (1 - easeWidth);
    }

    public void Reset()
    {
        Enabled = false;
        DurationSeconds = DefaultDurationSeconds;
        _phase = 0;
        _entryTime = 0;
    }
}
