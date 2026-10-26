using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Bounds one-shot swooshes to distinct, recent manual mouse movement.</summary>
public struct ManualSwingSwooshGate
{
    public const long CooldownMilliseconds = 1250;
    public const long MaximumPendingAgeMilliseconds = 150;

    private float2 _press, _lastPointer;
    private double _thresholdSquared;
    private long _pendingAt, _lastObservedAt, _nextAllowedAt;
    private bool _held, _thresholdArmed, _pending, _hasObservedTime, _hasPlayed;

    public readonly bool Held => _held;
    public readonly bool Pending => _pending;

    public void Begin(float2 pointer, float thresholdPixels)
    {
        if (!Finite(pointer) || !float.IsFinite(thresholdPixels) || thresholdPixels <= 0)
        {
            Release();
            return;
        }

        _press = _lastPointer = pointer;
        _thresholdSquared = (double)thresholdPixels * thresholdPixels;
        _held = true;
        _thresholdArmed = _pending = false;
    }

    /// <summary>Tracks native cursor samples without ever playing audio in an input callback.</summary>
    public void Move(float2 pointer, long now, bool allowed)
    {
        if (!_held || !Finite(pointer)) return;

        bool timeValid = ObserveTime(now);
        if (!allowed || !timeValid)
        {
            Rebase(pointer);
            return;
        }

        bool changed = pointer.X != _lastPointer.X || pointer.Y != _lastPointer.Y;
        _lastPointer = pointer;
        if (!changed) return;

        if (!_thresholdArmed)
        {
            double dx = (double)pointer.X - _press.X;
            double dy = (double)pointer.Y - _press.Y;
            if (dx * dx + dy * dy < _thresholdSquared) return;
            _thresholdArmed = true;
        }

        _pending = true;
        _pendingAt = now;
    }

    /// <summary>Consumes every sample attempt, including blocked, stale, or cooling-down input.</summary>
    public bool TryConsume(long now, bool allowed)
    {
        bool timeValid = ObserveTime(now);
        if (!_held)
        {
            _pending = false;
            return false;
        }
        if (!allowed || !timeValid)
        {
            _pending = false;
            Rebase(_lastPointer);
            return false;
        }
        if (!_pending) return false;

        bool recent = _pendingAt >= 0 && now >= _pendingAt
            && now - _pendingAt <= MaximumPendingAgeMilliseconds;
        bool cooldownElapsed = !_hasPlayed || now >= _nextAllowedAt;
        _pending = false;
        if (!recent)
        {
            Rebase(_lastPointer);
            return false;
        }
        if (!cooldownElapsed) return false;

        _hasPlayed = true;
        _nextAllowedAt = now > long.MaxValue - CooldownMilliseconds
            ? long.MaxValue : now + CooldownMilliseconds;
        // Consumption advances the rebase point without reapplying the press threshold.
        _press = _lastPointer;
        return true;
    }

    /// <summary>Ends the held gesture but retains the shared cadence across re-grabs.</summary>
    public void Release()
    {
        _held = _thresholdArmed = _pending = false;
        _press = _lastPointer = default;
        _thresholdSquared = 0;
        _pendingAt = 0;
    }

    public void Cancel() => Release();

    /// <summary>World reset: clears held input and the global cadence history.</summary>
    public void Reset()
    {
        Release();
        _lastObservedAt = _nextAllowedAt = 0;
        _hasObservedTime = _hasPlayed = false;
    }

    private bool ObserveTime(long now)
    {
        if (now < 0 || (_hasObservedTime && now < _lastObservedAt)) return false;
        _lastObservedAt = now;
        _hasObservedTime = true;
        return true;
    }

    private void Rebase(float2 pointer)
    {
        _press = _lastPointer = pointer;
        _thresholdArmed = _pending = false;
        _pendingAt = 0;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
