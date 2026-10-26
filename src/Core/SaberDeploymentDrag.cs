namespace Purr2D2.Core;

/// <summary>Press-relative horizontal control for setting a saber's held blade fraction.</summary>
public sealed class SaberDeploymentDrag
{
    private double _pressX;
    private double _trackWidth;
    private float _baseline;

    public bool Held { get; private set; }
    public float Value { get; private set; }
    public float TrackMinX { get; private set; }
    public float TrackMaxX { get; private set; }

    public bool Begin(float pointerX, float baseline, float trackMinX, float trackMaxX)
    {
        if (!float.IsFinite(pointerX) || !float.IsFinite(baseline)
            || !float.IsFinite(trackMinX) || !float.IsFinite(trackMaxX)
            || trackMaxX <= trackMinX || pointerX < trackMinX || pointerX > trackMaxX)
            return false;

        _pressX = pointerX;
        _trackWidth = (double)trackMaxX - trackMinX;
        _baseline = Math.Clamp(baseline, 0, 1);
        TrackMinX = trackMinX;
        TrackMaxX = trackMaxX;
        Value = _baseline;
        Held = true;
        return true;
    }

    public float Move(float pointerX)
    {
        if (Held && float.IsFinite(pointerX))
        {
            double value = _baseline + ((double)pointerX - _pressX) / _trackWidth;
            if (double.IsFinite(value)) Value = (float)Math.Clamp(value, 0, 1);
        }
        return Value;
    }

    /// <summary>Commits the final pointer value and ends the gesture.</summary>
    public float Release(float pointerX)
    {
        Move(pointerX);
        Held = false;
        return Value;
    }

    public bool MatchesTrack(float trackMinX, float trackMaxX) => Held
        && float.IsFinite(trackMinX) && float.IsFinite(trackMaxX)
        && MathF.Abs(trackMinX - TrackMinX) <= .5f && MathF.Abs(trackMaxX - TrackMaxX) <= .5f;

    public void Reset() => Held = false;
}
