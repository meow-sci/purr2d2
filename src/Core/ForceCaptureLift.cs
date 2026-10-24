using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Eases a captured Force group one metre along the owner's BODY up axis.</summary>
public sealed class ForceCaptureLift
{
    public const double DurationSeconds = .45;
    public const double HeightMeters = 1;
    public const double MaximumDeltaTime = .1;

    // KSA's native model +Y axis maps to BODY -Z through SaberPoseMath.ModelToBody.
    public static readonly double3 BodyUp = -double3.UnitZ;

    private double _elapsed;

    public bool IsActive { get; private set; }
    public double Progress { get; private set; }
    public double DisplayedHeightMeters { get; private set; }
    public double3 OffsetBody => BodyUp * DisplayedHeightMeters;

    /// <summary>Starts a fresh lift at exactly zero height.</summary>
    public double3 Start()
    {
        _elapsed = 0;
        Progress = 0;
        DisplayedHeightMeters = 0;
        IsActive = true;
        return OffsetBody;
    }

    /// <summary>Advances by a finite, positive, bounded amount of player time.</summary>
    public double3 Advance(double deltaTime)
    {
        if (!IsActive || !double.IsFinite(deltaTime) || deltaTime <= 0)
            return OffsetBody;

        _elapsed = Math.Min(DurationSeconds, _elapsed + Math.Min(deltaTime, MaximumDeltaTime));
        if (DurationSeconds - _elapsed <= 1e-12)
        {
            _elapsed = DurationSeconds;
            Progress = 1;
            DisplayedHeightMeters = HeightMeters;
            IsActive = false;
            return OffsetBody;
        }

        double t = _elapsed / DurationSeconds;
        Progress = Math.Clamp(t * t * t * (t * (6 * t - 15) + 10), 0, 1);
        DisplayedHeightMeters = HeightMeters * Progress;
        return OffsetBody;
    }

    public void Reset()
    {
        _elapsed = 0;
        Progress = 0;
        DisplayedHeightMeters = 0;
        IsActive = false;
    }
}
