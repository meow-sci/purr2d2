using Brutal.Numerics;

namespace Purr2D2.Core;

public enum SaberOverlayHit { None, Kittens, Swing, Slice }

/// <summary>Viewport-relative geometry shared by drawing and exact pointer hit testing.</summary>
public readonly record struct SaberOverlayLayout(float2 ListMin, float2 ListMax, float ListBodyTop,
    float ListBodyHeight, float ListFooterHeight, float RowHeight, float Padding,
    float2 SwingCenter, float SwingRadius, float2 SliceCenter, float SliceRadius, float Scale)
{
    public bool Valid => RowHeight > 0 && ListBodyHeight > 0;
    public SaberOverlayHit Hit(float2 point)
    {
        if (!Valid) return SaberOverlayHit.None;
        if (point.X >= ListMin.X && point.X <= ListMax.X && point.Y >= ListMin.Y && point.Y <= ListMax.Y)
            return SaberOverlayHit.Kittens;
        if (SaberOverlayDrag.ContainsCircle(point, SwingCenter, SwingRadius)) return SaberOverlayHit.Swing;
        return SaberOverlayDrag.ContainsCircle(point, SliceCenter, SliceRadius) ? SaberOverlayHit.Slice : SaberOverlayHit.None;
    }

    public static SaberOverlayLayout Create(float2 origin, float2 size, float fontSize, int kittenCount, float topInset = 0)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(fontSize)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 240 || size.Y < 220) return default;
        float scale = Math.Min(Math.Clamp(fontSize / 16f, .75f, 2), Math.Max(.5f, Math.Min(size.X / 420, size.Y / 560)));
        float pad = 12 * scale, margin = 20 * scale, row = 28 * scale;
        float radius = 88 * scale, smallRadius = 36 * scale;
        var slice = new float2(origin.X + size.X - margin - smallRadius, origin.Y + size.Y * .5f);
        var swing = slice - new float2(radius + smallRadius + 18 * scale, 0);
        var min = new float2(origin.X + size.X - margin - 296 * scale, origin.Y + Math.Max(margin, topInset));
        float header = 38 * scale;
        float maximumHeight = Math.Min(size.Y * .34f, swing.Y - radius - min.Y - 16 * scale);
        float desiredHeight = header + Math.Max(1, kittenCount) * row + pad;
        bool scroll = desiredHeight > maximumHeight;
        float footer = scroll ? 22 * scale : 0;
        float height = Math.Max(header + row + footer, Math.Min(desiredHeight, maximumHeight));
        return new(min, min + new float2(296 * scale, height), min.Y + header,
            height - header - footer, footer, row, pad, swing, radius, slice, smallRadius, scale);
    }
}
