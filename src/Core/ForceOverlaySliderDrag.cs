using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>A vertical overlay slider whose track is anchored so pressing preserves the current value.</summary>
public sealed class ForceOverlaySliderDrag
{
    private double _minimum, _maximum;
    private double _baselineNormalized;
    private float _pressY;
    private bool _integer;

    public bool Held { get; private set; }
    public ForceOverlayHit Control { get; private set; }
    public float TrackTop { get; private set; }
    public float TrackBottom { get; private set; }
    public float2 PillMin { get; private set; }
    public float2 PillMax { get; private set; }

    public bool Begin(ForceOverlayHit control, float2 pointer, float2 center, float radius, float travel,
        double current, double minimum, double maximum, bool integer)
    {
        if (control is not (ForceOverlayHit.CaptureLimit or ForceOverlayHit.AngryRadius)
            || !Finite(pointer) || !Finite(center) || !float.IsFinite(radius) || radius <= 0
            || !float.IsFinite(travel) || travel <= 0 || !double.IsFinite(current)
            || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum) return false;
        _minimum = minimum; _maximum = maximum; _integer = integer;
        _baselineNormalized = Math.Clamp((current - minimum) / (maximum - minimum), 0, 1);
        _pressY = pointer.Y;
        // The pill always opens away from Move. Pointer deltas are rebased to the
        // current value so a press alone never jumps the shared setting.
        TrackTop = center.Y;
        TrackBottom = TrackTop + travel;
        // Keep the same vector arithmetic as ForceOverlayLayout.SliderEnvelope so
        // deterministic layout rebuilds compare exactly at fractional scales.
        PillMin = center - new float2(radius);
        PillMax = center + new float2(radius, travel + radius);
        Control = control;
        Held = true;
        return true;
    }

    public bool Move(float2 pointer, out double value)
    {
        value = double.NaN;
        if (!Held || !Finite(pointer)) return false;
        double normalized = Math.Clamp(_baselineNormalized
            + (pointer.Y - _pressY) / (TrackBottom - TrackTop), 0, 1);
        value = _minimum + normalized * (_maximum - _minimum);
        if (_integer) value = Math.Round(value, MidpointRounding.AwayFromZero);
        value = Math.Clamp(value, _minimum, _maximum);
        return true;
    }

    public float2 Thumb(double current)
    {
        double normalized = double.IsFinite(current)
            ? Math.Clamp((current - _minimum) / (_maximum - _minimum), 0, 1) : 0;
        return new((PillMin.X + PillMax.X) * .5f, TrackTop + (float)normalized * (TrackBottom - TrackTop));
    }

    public bool Matches(ForceOverlayLayout layout)
    {
        if (!Held || !layout.Valid) return false;
        var envelope = layout.SliderEnvelope(Control);
        return envelope.Min == PillMin && envelope.Max == PillMax;
    }

    public void Release()
    {
        Held = false;
        Control = ForceOverlayHit.None;
    }

    public void Reset()
    {
        Release();
        _minimum = _maximum = _baselineNormalized = 0;
        _pressY = 0;
        TrackTop = TrackBottom = 0;
        PillMin = PillMax = float2.Zero;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
