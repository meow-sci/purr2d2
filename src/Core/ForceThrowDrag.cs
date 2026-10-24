using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>A transient upward drag that chooses the speed for a Force throw.</summary>
public sealed class ForceThrowDrag
{
    public const double MinimumSpeed = 25;
    public const double MaximumSpeed = 200;

    private float _pressY;

    public bool Held { get; private set; }
    public double Speed { get; private set; } = MinimumSpeed;
    public float2 Center { get; private set; }
    public float Radius { get; private set; }
    public float Travel { get; private set; }
    public float TrackTop { get; private set; }
    public float TrackBottom { get; private set; }
    public float2 PillMin { get; private set; }
    public float2 PillMax { get; private set; }

    /// <summary>The current speed thumb position, from the button at min to the top at max.</summary>
    public float2 Thumb
    {
        get
        {
            double normalized = Math.Clamp((Speed - MinimumSpeed) / (MaximumSpeed - MinimumSpeed), 0, 1);
            return new(Center.X, (float)((double)TrackBottom - normalized * Travel));
        }
    }

    /// <summary>Begins at the minimum speed regardless of where the pointer pressed.</summary>
    public bool Begin(float2 pointer, float2 center, float radius, float travel)
    {
        if (Held || !Finite(pointer) || !Finite(center) || !float.IsFinite(radius) || radius <= 0
            || !float.IsFinite(travel) || travel <= 0) return false;

        var pillMin = center - new float2(radius, travel + radius);
        var pillMax = center + new float2(radius, radius);
        float trackTop = center.Y - travel;
        float trackBottom = center.Y;
        if (!Finite(pillMin) || !Finite(pillMax) || !float.IsFinite(trackTop) || !float.IsFinite(trackBottom)
            || trackTop >= trackBottom || pillMin.X >= pillMax.X || pillMin.Y >= pillMax.Y) return false;

        Center = center;
        Radius = radius;
        Travel = travel;
        TrackTop = trackTop;
        TrackBottom = trackBottom;
        PillMin = pillMin;
        PillMax = pillMax;
        _pressY = pointer.Y;
        Speed = MinimumSpeed;
        Held = true;
        return true;
    }

    /// <summary>Updates speed from upward movement relative to the press; horizontal movement is ignored.</summary>
    public bool Move(float2 pointer)
    {
        if (!Held || !Finite(pointer)) return false;

        // Subtract as doubles so opposite, finite float extremes cannot overflow.
        double upward = (double)_pressY - pointer.Y;
        double normalized = Math.Clamp(upward / Travel, 0, 1);
        Speed = MinimumSpeed + normalized * (MaximumSpeed - MinimumSpeed);
        return true;
    }

    /// <summary>Completes once, returning the chosen speed and restoring the transient control.</summary>
    public bool Finish(out double speed)
    {
        if (!Held)
        {
            speed = MinimumSpeed;
            return false;
        }

        speed = Speed;
        Reset();
        return true;
    }

    /// <summary>Cancels the transient control without producing a throw.</summary>
    public void Cancel() => Reset();

    public bool Matches(ForceOverlayLayout layout)
    {
        if (!Held || !layout.Valid || !Finite(layout.AngryCenter)
            || !float.IsFinite(layout.ButtonRadius) || !float.IsFinite(layout.SliderTravel)) return false;
        return Center == layout.AngryCenter && Radius == layout.ButtonRadius && Travel == layout.SliderTravel;
    }

    private void Reset()
    {
        Held = false;
        Speed = MinimumSpeed;
        Center = float2.Zero;
        Radius = Travel = TrackTop = TrackBottom = _pressY = 0;
        PillMin = PillMax = float2.Zero;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
