namespace Purr2D2.Core;

/// <summary>Absolute horizontal control for setting a saber's held blade fraction.</summary>
public sealed class SaberDeploymentDrag
{
    private double _trackWidth;

    public bool Held { get; private set; }
    public float Value { get; private set; }
    public float TrackMinX { get; private set; }
    public float TrackMaxX { get; private set; }

    public bool Begin(float pointerX, float trackMinX, float trackMaxX)
    {
        if (!float.IsFinite(pointerX) || !float.IsFinite(trackMinX) || !float.IsFinite(trackMaxX)
            || trackMaxX <= trackMinX || pointerX < trackMinX || pointerX > trackMaxX)
            return false;

        _trackWidth = (double)trackMaxX - trackMinX;
        TrackMinX = trackMinX;
        TrackMaxX = trackMaxX;
        Value = ValueAt(pointerX);
        Held = true;
        return true;
    }

    public float Move(float pointerX)
    {
        if (Held && float.IsFinite(pointerX)) Value = ValueAt(pointerX);
        return Value;
    }

    private float ValueAt(float pointerX)
    {
        double value = ((double)pointerX - TrackMinX) / _trackWidth;
        return double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : Value;
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
