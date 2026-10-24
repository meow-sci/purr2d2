using Brutal.Numerics;

namespace Purr2D2.Core;

public enum ForceOverlayHit { None, CaptureLimit, AngryRadius, AngryForce, Force, Move }

/// <summary>Left-side Force controls, using the same scale as the right-side saber controls.</summary>
public readonly record struct ForceOverlayLayout(float2 CaptureLimitCenter, float2 AngryRadiusCenter,
    float2 AngryCenter, float2 ForceCenter, float ButtonRadius, float2 MoveCenter, float MoveRadius,
    float2 StatusMin, float2 StatusMax, float Scale)
{
    public bool Valid => ButtonRadius > 0 && MoveRadius > 0;
    public float SliderTravel => 96 * Scale;
    public float2 ControlsMin => new(Math.Min(MoveCenter.X - MoveRadius, CaptureLimitCenter.X - ButtonRadius),
        MoveCenter.Y - MoveRadius);
    public float2 ControlsMax => new(Math.Max(ForceCenter.X + ButtonRadius, StatusMax.X), StatusMax.Y);

    /// <summary>Bounds of a settings circle expanded vertically downward into its slider pill.</summary>
    public (float2 Min, float2 Max) SliderEnvelope(ForceOverlayHit hit)
    {
        var center = hit switch
        {
            ForceOverlayHit.CaptureLimit => CaptureLimitCenter,
            ForceOverlayHit.AngryRadius => AngryRadiusCenter,
            _ => new float2(float.NaN)
        };
        if (!Valid || !float.IsFinite(center.X)) return default;
        return (center - new float2(ButtonRadius),
            center + new float2(ButtonRadius, SliderTravel + ButtonRadius));
    }

    /// <summary>Moves all Force controls and their status as one bounded group.</summary>
    public ForceOverlayLayout WithControlsOffset(float2 origin, float2 size, float2 normalizedOffset, float topInset = 0)
    {
        if (!Valid || !SaberOverlayLayout.TryControlsTranslation(ControlsMin, ControlsMax, origin, size,
            normalizedOffset, topInset, out var delta)) return default;
        return this with { CaptureLimitCenter = CaptureLimitCenter + delta,
            AngryRadiusCenter = AngryRadiusCenter + delta, ForceCenter = ForceCenter + delta, AngryCenter = AngryCenter + delta,
            MoveCenter = MoveCenter + delta, StatusMin = StatusMin + delta, StatusMax = StatusMax + delta };
    }

    public ForceOverlayHit Hit(float2 pointer)
    {
        if (!Valid) return ForceOverlayHit.None;
        if (SaberOverlayDrag.ContainsCircle(pointer, ForceCenter, ButtonRadius)) return ForceOverlayHit.Force;
        if (SaberOverlayDrag.ContainsCircle(pointer, AngryCenter, ButtonRadius)) return ForceOverlayHit.AngryForce;
        if (SaberOverlayDrag.ContainsCircle(pointer, AngryRadiusCenter, ButtonRadius)) return ForceOverlayHit.AngryRadius;
        if (SaberOverlayDrag.ContainsCircle(pointer, CaptureLimitCenter, ButtonRadius)) return ForceOverlayHit.CaptureLimit;
        return SaberOverlayDrag.ContainsCircle(pointer, MoveCenter, MoveRadius) ? ForceOverlayHit.Move : ForceOverlayHit.None;
    }

    public static ForceOverlayLayout Create(SaberOverlayLayout saber, float2 origin, float2 size)
    {
        if (!saber.Valid || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0) return default;
        float scale = saber.Scale, radius = saber.SwingRadius, button = saber.SliceRadius;
        // Ten scaled pixels beyond the old anchor keep the new leftmost circle inside the viewport.
        var move = new float2(origin.X + 30 * scale + radius, saber.SwingCenter.Y);
        var status = new float2(move.X - radius, move.Y + 330 * scale);
        return new(move + new float2(-130, 110) * scale,
            move + new float2(-65, 166) * scale,
            move + new float2(65, 166) * scale,
            move + new float2(130, 110) * scale, button, move, radius,
            status, status + new float2(radius * 2, 48 * scale), scale);
    }
}
