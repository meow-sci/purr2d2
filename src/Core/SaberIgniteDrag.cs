using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Click to toggle, or drag vertically from the current blade fraction.</summary>
public sealed class SaberIgniteDrag
{
    private float2 _press, _center;
    private float _radius, _travel, _threshold, _start;
    private bool _startedEnabled;

    public bool Held { get; private set; }
    public bool Dragged { get; private set; }
    public float Value { get; private set; }

    public bool Begin(float2 pointer, float deployment, bool enabled, float2 center,
        float radius, float travel, float threshold)
    {
        if (Held || !Finite(pointer) || !Finite(center) || !float.IsFinite(deployment)
            || deployment < 0 || deployment > 1 || !float.IsFinite(radius) || radius <= 0
            || !float.IsFinite(travel) || travel <= 0 || !float.IsFinite(threshold) || threshold < 0
            || !SaberOverlayDrag.ContainsCircle(pointer, center, radius)) return false;
        _press = pointer;
        _center = center;
        _radius = radius;
        _travel = travel;
        _threshold = threshold;
        _start = Value = deployment;
        _startedEnabled = enabled;
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
        if (Dragged) Value = (float)Math.Clamp(_start + dy / _travel, 0, 1);
        return Value;
    }

    /// <summary>A drag holds its final fraction; a click toggles the mode captured on press.</summary>
    public bool Release(float2 pointer, out float deployment)
    {
        deployment = Value;
        if (!Held || !Finite(pointer)) return false;
        Move(pointer);
        deployment = Dragged ? Value : _startedEnabled ? 0 : 1;
        Held = false;
        return true;
    }

    public bool MatchesLayout(float2 center, float radius, float travel) => Held
        && Close(center.X, _center.X) && Close(center.Y, _center.Y)
        && Close(radius, _radius) && Close(travel, _travel);

    public void Reset()
    {
        Held = Dragged = false;
        Value = _start = _radius = _travel = _threshold = 0;
        _startedEnabled = false;
        _press = _center = float2.Zero;
    }

    private static bool Close(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && MathF.Abs(a - b) <= .5f;
    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
