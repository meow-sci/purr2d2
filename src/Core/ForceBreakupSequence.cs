namespace Purr2D2.Core;

/// <summary>Direction-aware Shift+scroll tension and timing for one Force breakup gesture.</summary>
internal sealed class ForceBreakupSequence
{
    internal const double NativeScrollUnitPixels = 12;
    internal const double MinimumVisualDelaySeconds = .25;

    private double _groanThresholdPixels = 6;
    private double _splitThresholdPixels = 18;
    private double _stageOneStartedAt;

    internal double TensionPixels { get; private set; }
    internal bool StageOneActive { get; private set; }
    internal bool SplitRequested { get; private set; }
    internal double StageOneStartedAt => _stageOneStartedAt;
    internal double GroanThresholdPixels => _groanThresholdPixels;
    internal double SplitThresholdPixels => _splitThresholdPixels;

    /// <summary>
    /// A threshold edit invalidates the active gesture. The caller can then cancel any
    /// preparation owned by the Force controller before accepting more scroll input.
    /// </summary>
    internal bool Configure(double groanThresholdPixels, double splitThresholdPixels)
    {
        if (!double.IsFinite(groanThresholdPixels) || groanThresholdPixels <= 0)
            groanThresholdPixels = 6;
        if (!double.IsFinite(splitThresholdPixels) || splitThresholdPixels <= groanThresholdPixels)
            splitThresholdPixels = Math.Max(18, groanThresholdPixels + 1);

        if (_groanThresholdPixels == groanThresholdPixels && _splitThresholdPixels == splitThresholdPixels)
            return false;
        _groanThresholdPixels = groanThresholdPixels;
        _splitThresholdPixels = splitThresholdPixels;
        Reset();
        return true;
    }

    /// <summary>Convert native GLFW scroll units to signed screen pixels without quantizing.</summary>
    internal static double PixelsFromNativeScroll(double ticks)
    {
        if (!double.IsFinite(ticks)) return 0;
        // Negative wheel ticks expand and build tension. Saturation only matters for
        // malformed/extreme inputs; ordinary fractional trackpad deltas remain exact.
        double maximumTicks = double.MaxValue / NativeScrollUnitPixels;
        return -Math.Clamp(ticks, -maximumTicks, maximumTicks) * NativeScrollUnitPixels;
    }

    /// <summary>
    /// Accumulate outward pixels and unwind inward pixels. Stage one starts only when
    /// the caller confirms an intact, breakable source is currently held.
    /// </summary>
    internal ForceBreakupInputResult ApplyScroll(double deltaPixels, double now, bool canStart)
    {
        if (!canStart)
        {
            bool cancelled = StageOneActive;
            Reset();
            return new(false, cancelled, false);
        }
        if (!double.IsFinite(deltaPixels) || deltaPixels == 0) return default;

        double nextTension = SaturatingAdd(TensionPixels, deltaPixels);
        TensionPixels = Math.Max(0, nextTension);

        bool started = false;
        if (!StageOneActive && MeetsDistanceThreshold(TensionPixels, _groanThresholdPixels) && double.IsFinite(now))
        {
            StageOneActive = true;
            _stageOneStartedAt = now;
            started = true;
        }

        if (StageOneActive && !MeetsDistanceThreshold(TensionPixels, _groanThresholdPixels))
        {
            StageOneActive = false;
            SplitRequested = false;
            _stageOneStartedAt = 0;
            return new(false, true, false);
        }

        SplitRequested = StageOneActive && MeetsDistanceThreshold(TensionPixels, _splitThresholdPixels);
        return new(started, false, SplitRequested);
    }

    internal bool MinimumDelayElapsed(double now) => StageOneActive && double.IsFinite(now)
        && now >= _stageOneStartedAt && now - _stageOneStartedAt >= MinimumVisualDelaySeconds;

    internal void Reset()
    {
        TensionPixels = 0;
        StageOneActive = false;
        SplitRequested = false;
        _stageOneStartedAt = 0;
    }

    private static double SaturatingAdd(double left, double right)
    {
        if (right > 0 && left > double.MaxValue - right) return double.MaxValue;
        if (right < 0 && left < -double.MaxValue - right) return -double.MaxValue;
        return left + right;
    }

    private static bool MeetsDistanceThreshold(double distance, double threshold) =>
        distance >= threshold || threshold - distance <= Math.Max(1, threshold) * 1e-12;
}

internal readonly record struct ForceBreakupInputResult(bool StageOneStarted, bool StageOneCancelled,
    bool SplitRequested);

internal readonly record struct ForceBreakupStepResult(bool Active, bool Failed, bool SourceCommitted, bool Completed);
