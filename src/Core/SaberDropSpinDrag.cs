using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>One transient downward drag choosing release spin; every press starts at zero.</summary>
public sealed class SaberDropSpinDrag
{
    public const float MaximumMultiplier = 2f;
    private float _pressY;

    public bool Held { get; private set; }
    public float SpinMultiplier { get; private set; }
    public float2 Center { get; private set; }
    public float Radius { get; private set; }
    public float Travel { get; private set; }
    public float2 PillMin => Center - new float2(Radius);
    public float2 PillMax => Center + new float2(Radius, Radius + Travel);
    public float2 Thumb => Center + new float2(0, Travel * (SpinMultiplier / MaximumMultiplier));

    public bool Begin(float2 pointer, float2 center, float radius, float travel)
    {
        if (Held || !Finite(pointer) || !Finite(center) || !float.IsFinite(radius) || radius <= 0
            || !float.IsFinite(travel) || travel <= 0
            || !SaberOverlayDrag.ContainsCircle(pointer, center, radius)) return false;
        var min = center - new float2(radius);
        var max = center + new float2(radius, radius + travel);
        if (!Finite(min) || !Finite(max) || min.X >= max.X || min.Y >= max.Y) return false;
        Center = center;
        Radius = radius;
        Travel = travel;
        _pressY = pointer.Y;
        SpinMultiplier = 0;
        Held = true;
        return true;
    }

    public bool Move(float2 pointer)
    {
        if (!Held || !Finite(pointer)) return false;
        // Subtraction in double also handles opposite finite float extremes.
        double fraction = Math.Clamp(((double)pointer.Y - _pressY) / Travel, 0, 1);
        SpinMultiplier = (float)(fraction * MaximumMultiplier);
        return true;
    }

    /// <summary>Samples the final release coordinate and completes at most once.</summary>
    public bool Finish(float2 pointer, out float spinMultiplier)
    {
        spinMultiplier = 0;
        if (!Move(pointer)) { Cancel(); return false; }
        spinMultiplier = SpinMultiplier;
        Cancel();
        return true;
    }

    public bool Matches(SaberOverlayLayout layout) => Held && layout.Valid
        && Center == layout.DropCenter && Radius == layout.IgniteRadius && Travel == layout.DropPillTravel;

    public void Cancel()
    {
        Held = false;
        SpinMultiplier = Radius = Travel = _pressY = 0;
        Center = float2.Zero;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
