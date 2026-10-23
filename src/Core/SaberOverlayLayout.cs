using Brutal.Numerics;

namespace Purr2D2.Core;

public enum SaberOverlayHit { None, Kittens, Swing, Slice, SomersaultForward, SomersaultBackward, SomersaultLeft, SomersaultRight, Ignite }

/// <summary>Viewport-relative geometry shared by drawing and exact pointer hit testing.</summary>
public readonly record struct SaberOverlayLayout(float2 ListMin, float2 ListMax, float ListBodyTop,
    float ListBodyHeight, float ListFooterHeight, float RowHeight, float Padding,
    float2 SwingCenter, float SwingRadius, float2 SliceCenter, float SliceRadius, float Scale,
    float2 ForwardCenter, float2 BackwardCenter, float2 LeftCenter, float2 RightCenter,
    float2 StatusMin, float2 StatusMax, bool ShowSomersaultButtons)
{
    public bool Valid => RowHeight > 0 && ListBodyHeight > 0;
    public float IgniteRadius => SliceRadius;
    // Align the left edges so the extra control fits even the narrowest layout.
    public float2 IgniteCenter => SwingCenter - new float2(SwingRadius - IgniteRadius, SwingRadius + 18 * Scale);
    public float SomersaultRadius => SliceRadius;
    public float ColorSwatchSize => RowHeight * .62f;
    public float2 ColorSwatchMin(int row, float scroll) => new(ListMax.X - Padding - ColorSwatchSize,
        ListBodyTop + row * RowHeight - scroll + RowHeight * .18f);
    public bool IsColorSwatch(float2 point, int row, float scroll)
    {
        if (!Valid || row < 0 || !float.IsFinite(scroll) || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.Y < ListBodyTop || point.Y >= ListBodyTop + ListBodyHeight) return false;
        var min = ColorSwatchMin(row, scroll);
        return point.X >= min.X && point.X <= min.X + ColorSwatchSize
            && point.Y >= min.Y && point.Y <= min.Y + ColorSwatchSize;
    }
    public SaberOverlayHit Hit(float2 point)
    {
        if (!Valid) return SaberOverlayHit.None;
        if (point.X >= ListMin.X && point.X <= ListMax.X && point.Y >= ListMin.Y && point.Y <= ListMax.Y)
            return SaberOverlayHit.Kittens;
        if (SaberOverlayDrag.ContainsCircle(point, SwingCenter, SwingRadius)) return SaberOverlayHit.Swing;
        if (SaberOverlayDrag.ContainsCircle(point, SliceCenter, SliceRadius)) return SaberOverlayHit.Slice;
        if (SaberOverlayDrag.ContainsCircle(point, IgniteCenter, IgniteRadius)) return SaberOverlayHit.Ignite;
        if (!ShowSomersaultButtons) return SaberOverlayHit.None;
        if (SaberOverlayDrag.ContainsCircle(point, ForwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultForward;
        if (SaberOverlayDrag.ContainsCircle(point, BackwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultBackward;
        if (SaberOverlayDrag.ContainsCircle(point, LeftCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultLeft;
        return SaberOverlayDrag.ContainsCircle(point, RightCenter, SomersaultRadius) ? SaberOverlayHit.SomersaultRight : SaberOverlayHit.None;
    }

    public static SaberOverlayLayout Create(float2 origin, float2 size, float fontSize, int kittenCount, float topInset = 0,
        bool showSomersaultButtons = true)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(fontSize)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(topInset)
            || fontSize <= 0 || size.X < 240 || size.Y < 220) return default;
        topInset = Math.Max(0, topInset);
        float availableHeight = size.Y - topInset;
        if (availableHeight < 120) return default;
        // Budget the list, both existing actions, all three diamond rows and status together.
        // Keep the familiar half-height swing anchor wherever the complete group fits.
        // Reserve a second large circle on the left for Force manipulation.
        float scale = Math.Min(Math.Clamp(fontSize / 16f, .75f, 2), Math.Min(size.X / 840, availableHeight / 820));
        float pad = 12 * scale, margin = 20 * scale, row = 28 * scale;
        float radius = 132 * scale, smallRadius = 27 * scale;
        float listTop = origin.Y + Math.Max(margin, topInset);
        float minimumSwingY = listTop + (100 + 16) * scale + radius;
        float maximumSwingY = origin.Y + size.Y - (132 + 16 + 186 + 12 + 48 + 20) * scale;
        if (minimumSwingY > maximumSwingY) return default;
        float swingY = Math.Clamp(origin.Y + size.Y * .5f, minimumSwingY, maximumSwingY);
        var slice = new float2(origin.X + size.X - margin - smallRadius - 60 * scale, swingY);
        var swing = slice - new float2(radius + smallRadius + 18 * scale, 0);
        var min = new float2(origin.X + size.X - margin - 296 * scale, listTop);
        float header = 38 * scale;
        float maximumHeight = Math.Min(size.Y * .34f, swing.Y - radius - min.Y - 16 * scale);
        float desiredHeight = header + Math.Max(1, kittenCount) * row + pad;
        bool scroll = desiredHeight > maximumHeight;
        float footer = scroll ? 22 * scale : 0;
        float height = Math.Max(header + row + footer + pad, Math.Min(desiredHeight, maximumHeight));
        float step = smallRadius * 2 + 12 * scale;
        var forward = new float2(swing.X, swing.Y + radius + 16 * scale + smallRadius);
        var backward = forward + new float2(0, step * 2);
        var statusMin = new float2(min.X, backward.Y + smallRadius + pad);
        var statusMax = new float2(min.X + 296 * scale, origin.Y + size.Y - margin);
        return new(min, min + new float2(296 * scale, height), min.Y + header,
            height - header - footer, footer, row, pad, swing, radius, slice, smallRadius, scale,
            forward, backward, forward + new float2(-step, step), forward + new float2(step, step), statusMin, statusMax,
            showSomersaultButtons);
    }
}
