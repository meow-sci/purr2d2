using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>A persistent camera-plane translation expressed in the owner's body frame.</summary>
public sealed class ForceOverlayDrag
{
    public const double MaximumOffsetMeters = 25;
    public const double GestureReachMeters = 8;
    public const double WheelStepMeters = .5;
    private float2 _press;
    private float _radius;
    private double3 _baseline, _right, _up, _forward;
    private double _depth;
    public bool Held { get; private set; }
    public double3 Offset { get; private set; }
    public float2 PointerOffset { get; private set; }

    public bool Begin(float2 pointer, float radius, double3 currentOffset,
        double3 rightBody, double3 upBody, double3 forwardBody)
    {
        if (!Finite(pointer) || !float.IsFinite(radius) || radius <= 0 || !Finite(currentOffset)
            || !TryUnit(rightBody, out var right) || !TryUnit(upBody, out var up)
            || !TryUnit(forwardBody, out var forward) || Math.Abs(double3.Dot(right, up)) > .001
            || Math.Abs(double3.Dot(right, forward)) > .001 || Math.Abs(double3.Dot(up, forward)) > .001)
            return false;
        _press = pointer; _radius = radius;
        _baseline = Offset = Clamp(currentOffset);
        _right = right; _up = up; _forward = forward;
        _depth = 0; PointerOffset = float2.Zero;
        Held = true;
        return true;
    }

    public void Move(float2 pointer)
    {
        if (!Held || !Finite(pointer)) return;
        PointerOffset = SaberOverlayDrag.ClampDisk((pointer - _press) / _radius);
        UpdateOffset();
    }

    public void Scroll(double ticks)
    {
        if (!Held || !double.IsFinite(ticks)) return;
        // Wheel up brings the group towards the camera, down moves it away.
        _depth = Math.Clamp(_depth - Math.Clamp(ticks, -100, 100) * WheelStepMeters,
            -GestureReachMeters, GestureReachMeters);
        UpdateOffset();
    }

    /// <summary>Wheel depth also works over the idle move circle, without starting a drag.</summary>
    public bool Scroll(double ticks, double3 currentOffset, double3 forwardBody)
    {
        if (!double.IsFinite(ticks) || ticks == 0 || !Finite(currentOffset)
            || !TryUnit(forwardBody, out var forward)) return false;
        if (Held) Scroll(ticks);
        else
        {
            Offset = Clamp(Clamp(currentOffset) - forward * (Math.Clamp(ticks, -100, 100) * WheelStepMeters));
            _baseline = Offset; _depth = 0; PointerOffset = float2.Zero;
        }
        return true;
    }

    public void Release() => Held = false;

    public void Reset()
    {
        Held = false;
        Offset = _baseline = double3.Zero;
        PointerOffset = float2.Zero;
        _depth = 0;
    }

    private void UpdateOffset() => Offset = Clamp(_baseline
        + _right * (PointerOffset.X * GestureReachMeters)
        - _up * (PointerOffset.Y * GestureReachMeters) + _forward * _depth);

    private static double3 Clamp(double3 value)
    {
        // Scaling first avoids overflow for a finite but enormous input vector.
        double largest = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        if (largest <= MaximumOffsetMeters) return value.Length() <= MaximumOffsetMeters
            ? value : value / value.Length() * MaximumOffsetMeters;
        var scaled = value / largest;
        return scaled / scaled.Length() * MaximumOffsetMeters;
    }

    private static bool TryUnit(double3 value, out double3 unit)
    {
        unit = default;
        if (!Finite(value)) return false;
        double length = value.Length();
        if (!double.IsFinite(length) || length < 1e-8) return false;
        unit = value / length;
        return true;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
