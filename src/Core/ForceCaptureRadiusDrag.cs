using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>A transient upward gesture that selects the pre-capture Angry radius.</summary>
internal sealed class ForceCaptureRadiusDrag
{
    internal const double MinimumRadius = 2;
    internal const double MaximumRadius = 50;
    internal const float DragThreshold = 4;

    private float _pressX, _pressY;

    internal bool Held { get; private set; }
    internal bool Dragged { get; private set; }
    internal double Radius { get; private set; } = MinimumRadius;
    internal float2 Center { get; private set; }
    internal float ButtonRadius { get; private set; }
    internal float Travel { get; private set; }
    internal float TrackTop { get; private set; }
    internal float TrackBottom { get; private set; }
    internal float2 PillMin { get; private set; }
    internal float2 PillMax { get; private set; }

    internal float2 Thumb
    {
        get
        {
            double normalized = (Radius - MinimumRadius) / (MaximumRadius - MinimumRadius);
            return new(Center.X, (float)((double)TrackBottom - Math.Clamp(normalized, 0, 1) * Travel));
        }
    }

    /// <summary>Begins a fresh radius selection at two metres.</summary>
    internal bool BeginSelection(float2 pointer, float2 center, float buttonRadius, float travel)
    {
        if (Held || !Finite(pointer) || !Finite(center) || !float.IsFinite(buttonRadius) || buttonRadius <= 0
            || !float.IsFinite(travel) || travel <= 0) return false;

        var pillMin = center - new float2(buttonRadius, travel + buttonRadius);
        var pillMax = center + new float2(buttonRadius, buttonRadius);
        float trackTop = center.Y - travel;
        float trackBottom = center.Y;
        if (!Finite(pillMin) || !Finite(pillMax) || !float.IsFinite(trackTop) || !float.IsFinite(trackBottom)
            || trackTop >= trackBottom || pillMin.X >= pillMax.X || pillMin.Y >= pillMax.Y) return false;

        Center = center;
        ButtonRadius = buttonRadius;
        Travel = travel;
        TrackTop = trackTop;
        TrackBottom = trackBottom;
        PillMin = pillMin;
        PillMax = pillMax;
        _pressX = pointer.X;
        _pressY = pointer.Y;
        Radius = MinimumRadius;
        Dragged = false;
        Held = true;
        return true;
    }

    /// <summary>Uses press-relative upward travel, allowing the chosen value to move either way.</summary>
    internal bool Move(float2 pointer)
    {
        if (!Held || !Finite(pointer)) return false;
        double dx = (double)pointer.X - _pressX;
        double dy = (double)pointer.Y - _pressY;
        if (!Dragged && dx * dx + dy * dy >= DragThreshold * DragThreshold) Dragged = true;
        if (!Dragged) return true;

        double upward = (double)_pressY - pointer.Y;
        double candidate = MinimumRadius
            + Math.Clamp(upward / Travel, 0, 1) * (MaximumRadius - MinimumRadius);
        if (double.IsFinite(candidate)) Radius = Math.Clamp(candidate, MinimumRadius, MaximumRadius);
        return true;
    }

    /// <summary>Finishes once, returning whether this was a drag and the selected radius.</summary>
    internal bool Finish(out bool dragged, out double radius)
    {
        if (!Held)
        {
            dragged = false;
            radius = MinimumRadius;
            return false;
        }
        dragged = Dragged;
        radius = Radius;
        Reset();
        return true;
    }

    internal void Cancel() => Reset();

    internal bool Matches(ForceOverlayLayout layout) => Held && layout.Valid && Finite(layout.AngryCenter)
        && float.IsFinite(layout.ButtonRadius) && float.IsFinite(layout.SliderTravel)
        && Center == layout.AngryCenter && ButtonRadius == layout.ButtonRadius && Travel == layout.SliderTravel;

    private void Reset()
    {
        Held = Dragged = false;
        Radius = MinimumRadius;
        Center = float2.Zero;
        ButtonRadius = Travel = TrackTop = TrackBottom = _pressX = _pressY = 0;
        PillMin = PillMax = float2.Zero;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
