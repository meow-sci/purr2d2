using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>An upper semicircle: each quarter-circle from the top produces one full item turn.</summary>
public sealed class ForceRotationDrag
{
    public const double MaximumRotation = Math.PI * 2;
    private float2 _center;
    private float _trackRadius, _handleRadius;
    private double _grabAngle;
    public bool Held { get; private set; }
    public double Rotation { get; private set; }

    public bool Begin(float2 pointer, ForceOverlayLayout layout, double currentRotation)
    {
        if (!layout.Valid || !Finite(pointer) || !double.IsFinite(currentRotation)) return false;
        currentRotation = Math.Clamp(currentRotation, -MaximumRotation, MaximumRotation);
        if (!layout.HitRotation(pointer, currentRotation)) return false;
        Rotation = currentRotation;
        _center = layout.MoveCenter;
        _trackRadius = layout.RotationTrackRadius;
        _handleRadius = layout.RotationHandleRadius;
        // Keep an off-center grab from nudging the displayed angle on press.
        _grabAngle = PointerAngle(pointer) - Rotation / 4;
        Held = true;
        return true;
    }

    public bool Matches(ForceOverlayLayout layout) => layout.Valid && layout.MoveCenter == _center
        && layout.RotationTrackRadius == _trackRadius && layout.RotationHandleRadius == _handleRadius;

    public bool Move(float2 pointer)
    {
        if (!Held || !Finite(pointer) || (pointer - _center).LengthSquared() < 1e-8f) return false;
        double angle = Rotation / 4;
        // Unwrap around the current position so dragging past the bottom cannot
        // jump across the atan2 seam from +360 to -360 degrees.
        double delta = Math.IEEERemainder(PointerAngle(pointer) - _grabAngle - angle, Math.PI * 2);
        Rotation = Math.Clamp(angle + delta, -Math.PI / 2, Math.PI / 2) * 4;
        return true;
    }

    public void Release() => Held = false;
    public void Reset() { Held = false; Rotation = 0; }
    private double PointerAngle(float2 pointer) => Math.Atan2(pointer.X - _center.X, _center.Y - pointer.Y);
    private static bool Finite(float2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
