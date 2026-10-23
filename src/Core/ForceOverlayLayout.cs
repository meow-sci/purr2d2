using Brutal.Numerics;

namespace Purr2D2.Core;

public enum ForceOverlayHit { None, Force, AngryForce, Move }

/// <summary>Left-side Force controls, using the same scale as the right-side saber controls.</summary>
public readonly record struct ForceOverlayLayout(float2 ForceCenter, float2 AngryCenter, float ButtonRadius,
    float2 MoveCenter, float MoveRadius, float2 StatusMin, float2 StatusMax, float Scale)
{
    public bool Valid => ButtonRadius > 0 && MoveRadius > 0;
    public ForceOverlayHit Hit(float2 pointer)
    {
        if (!Valid) return ForceOverlayHit.None;
        if (SaberOverlayDrag.ContainsCircle(pointer, ForceCenter, ButtonRadius)) return ForceOverlayHit.Force;
        if (SaberOverlayDrag.ContainsCircle(pointer, AngryCenter, ButtonRadius)) return ForceOverlayHit.AngryForce;
        return SaberOverlayDrag.ContainsCircle(pointer, MoveCenter, MoveRadius) ? ForceOverlayHit.Move : ForceOverlayHit.None;
    }

    public static ForceOverlayLayout Create(SaberOverlayLayout saber, float2 origin, float2 size)
    {
        if (!saber.Valid || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0) return default;
        float scale = saber.Scale, radius = saber.SwingRadius, button = saber.SliceRadius;
        var move = new float2(origin.X + 20 * scale + radius, saber.SwingCenter.Y);
        var status = new float2(move.X - radius, move.Y + 220 * scale);
        return new(move + new float2(130, 110) * scale,
            move + new float2(65, 166) * scale, button, move, radius,
            status, new(move.X + radius, origin.Y + size.Y - 20 * scale), scale);
    }
}
