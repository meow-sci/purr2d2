using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Press-relative click/vertical-duration gesture for one Auto Swing button.</summary>
public sealed class SaberAutoSwingDrag
{
    private float2 _press;
    private float2 _buttonCenter;
    private float2 _pillMin, _pillMax;
    private float _buttonRadius, _threshold, _travel;
    private float _startDuration;

    public bool Held { get; private set; }
    public bool Dragged { get; private set; }
    public float Value { get; private set; } = SaberAutoSwing.DefaultDurationSeconds;
    public float StartDuration => _startDuration;

    public bool Begin(float2 pointer, float duration, float2 buttonCenter, float buttonRadius,
        float2 pillMin, float2 pillMax, float threshold)
    {
        if (Held || !Finite(pointer) || !Finite(buttonCenter) || !Finite(pillMin) || !Finite(pillMax)
            || !float.IsFinite(duration) || duration < SaberAutoSwing.MinimumDurationSeconds
            || duration > SaberAutoSwing.MaximumDurationSeconds || !float.IsFinite(buttonRadius)
            || buttonRadius <= 0 || !float.IsFinite(threshold) || threshold < 0
            || pillMax.X <= pillMin.X || pillMax.Y <= pillMin.Y
            || !SaberOverlayDrag.ContainsCircle(pointer, buttonCenter, buttonRadius)) return false;
        float travel = pillMax.Y - pillMin.Y - 2 * buttonRadius;
        if (!float.IsFinite(travel) || travel <= 0) return false;
        _press = pointer;
        _buttonCenter = buttonCenter;
        _pillMin = pillMin;
        _pillMax = pillMax;
        _buttonRadius = buttonRadius;
        _threshold = threshold;
        _travel = travel;
        _startDuration = Value = duration;
        Held = true;
        Dragged = false;
        return true;
    }

    public float Move(float2 pointer)
    {
        if (!Held || !Finite(pointer)) return Value;
        double dx = (double)pointer.X - _press.X;
        double dy = (double)pointer.Y - _press.Y;
        if (!Dragged && dx * dx + dy * dy >= (double)_threshold * _threshold) Dragged = true;
        if (!Dragged) return Value = _startDuration;
        double range = SaberAutoSwing.MaximumDurationSeconds - SaberAutoSwing.MinimumDurationSeconds;
        double value = _startDuration + dy / _travel * range;
        Value = (float)Math.Clamp(value, SaberAutoSwing.MinimumDurationSeconds,
            SaberAutoSwing.MaximumDurationSeconds);
        return Value;
    }

    /// <summary>Returns true once for a valid release and applies its final pointer value.</summary>
    public bool Release(float2 pointer)
    {
        if (!Held) return false;
        Move(pointer);
        Held = false;
        return true;
    }

    /// <summary>Stops capture without deciding whether the caller enables automatic swing.</summary>
    public void Cancel() => Held = false;

    public bool MatchesLayout(float2 buttonCenter, float buttonRadius, float2 pillMin, float2 pillMax) =>
        Held && Finite(buttonCenter) && Finite(pillMin) && Finite(pillMax)
        && Close(buttonCenter.X, _buttonCenter.X) && Close(buttonCenter.Y, _buttonCenter.Y)
        && Close(buttonRadius, _buttonRadius) && Close(pillMin.X, _pillMin.X) && Close(pillMin.Y, _pillMin.Y)
        && Close(pillMax.X, _pillMax.X) && Close(pillMax.Y, _pillMax.Y);

    public void Reset()
    {
        Held = Dragged = false;
        _press = _buttonCenter = _pillMin = _pillMax = float2.Zero;
        _buttonRadius = _threshold = _travel = _startDuration = 0;
        Value = SaberAutoSwing.DefaultDurationSeconds;
    }

    private static bool Close(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && MathF.Abs(a - b) <= .5f;
    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
