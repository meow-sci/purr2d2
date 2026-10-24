using Brutal.Numerics;

namespace Purr2D2.Core;

public enum SaberOverlayHit { None, Kittens, Swing, Slice, SomersaultForward, SomersaultBackward, SomersaultLeft, SomersaultRight, Ignite, Settings }

/// <summary>Viewport-relative geometry shared by drawing and exact pointer hit testing.</summary>
public readonly record struct SaberOverlayLayout(float2 ListMin, float2 ListMax, float ListBodyTop,
    float ListBodyHeight, float ListFooterHeight, float RowHeight, float Padding,
    float2 SwingCenter, float SwingRadius, float2 SliceCenter, float SliceRadius, float Scale,
    float2 ForwardCenter, float2 BackwardCenter, float2 LeftCenter, float2 RightCenter,
    float2 StatusMin, float2 StatusMax, bool ShowSomersaultButtons)
{
    public bool Valid => RowHeight > 0 && ListBodyHeight > 0;
    public float IgniteRadius => SliceRadius;
    public float SwingSliceEdgeGap => (SliceCenter - SwingCenter).Length() - SwingRadius - SliceRadius;
    public float2 IgniteCenter => SwingCenter + new float2(-94, 160)
        * ((SliceCenter - SwingCenter).Length() / MathF.Sqrt(94 * 94 + 160 * 160));
    public float SwingMarkerRadius => 5 * Scale;
    public float SwingTravelRadius => SwingRadius - SwingMarkerRadius;
    public float2 SwingIndicator(float2 offset) => SwingCenter + SaberOverlayDrag.ClampDisk(offset) * SwingTravelRadius;
    public float SomersaultRadius => SliceRadius;
    public float SettingsFooterHeight => 50 * Scale;
    public float2 SettingsMax => ListMax - new float2(Padding);
    public float2 SettingsMin => new(ListMin.X + Padding, SettingsMax.Y - 30 * Scale);
    public float ColorSwatchSize => RowHeight * .62f;
    public float2 ColorSwatchMin(int row, float scroll) => new(ListMax.X - Padding - ColorSwatchSize,
        ListBodyTop + row * RowHeight - scroll + RowHeight * .18f);
    public float2 ControlsMin => new(Math.Min(Math.Min(SwingCenter.X - SwingRadius, SliceCenter.X - SliceRadius), StatusMin.X),
        Math.Min(SwingCenter.Y - SwingRadius, StatusMin.Y));
    public float2 ControlsMax => new(StatusMax.X, StatusMax.Y);

    /// <summary>Moves the action controls and their status together; offsets are fractions of the viewport.</summary>
    public SaberOverlayLayout WithControlsOffset(float2 origin, float2 size, float2 normalizedOffset, float topInset = 0)
    {
        if (!Valid || !TryControlsTranslation(ControlsMin, ControlsMax, origin, size, normalizedOffset, topInset, out var delta))
            return default;
        return this with { SwingCenter = SwingCenter + delta, SliceCenter = SliceCenter + delta,
            ForwardCenter = ForwardCenter + delta, BackwardCenter = BackwardCenter + delta,
            LeftCenter = LeftCenter + delta, RightCenter = RightCenter + delta,
            StatusMin = StatusMin + delta, StatusMax = StatusMax + delta };
    }

    internal static bool TryControlsTranslation(float2 min, float2 max, float2 origin, float2 size,
        float2 normalizedOffset, float topInset, out float2 translation)
    {
        translation = float2.Zero;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(size.X)
            || !float.IsFinite(size.Y) || !float.IsFinite(topInset) || size.X <= 0 || size.Y <= 0) return false;
        var workMin = origin + new float2(0, Math.Max(0, topInset));
        var workMax = origin + size;
        var lower = workMin - min;
        var upper = workMax - max;
        if (!float.IsFinite(workMax.X) || !float.IsFinite(workMax.Y)
            || lower.X > upper.X || lower.Y > upper.Y) return false;
        float x = float.IsFinite(normalizedOffset.X) ? Math.Clamp(normalizedOffset.X, -1, 1) : 0;
        float y = float.IsFinite(normalizedOffset.Y) ? Math.Clamp(normalizedOffset.Y, -1, 1) : 0;
        translation = new(Math.Clamp(x * size.X, lower.X, upper.X), Math.Clamp(y * size.Y, lower.Y, upper.Y));
        return true;
    }
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
        if (SaberOverlayDrag.ContainsCircle(point, SwingCenter, SwingRadius)) return SaberOverlayHit.Swing;
        if (SaberOverlayDrag.ContainsCircle(point, SliceCenter, SliceRadius)) return SaberOverlayHit.Slice;
        if (SaberOverlayDrag.ContainsCircle(point, IgniteCenter, IgniteRadius)) return SaberOverlayHit.Ignite;
        if (ShowSomersaultButtons)
        {
            if (SaberOverlayDrag.ContainsCircle(point, ForwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultForward;
            if (SaberOverlayDrag.ContainsCircle(point, BackwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultBackward;
            if (SaberOverlayDrag.ContainsCircle(point, LeftCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultLeft;
            if (SaberOverlayDrag.ContainsCircle(point, RightCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultRight;
        }
        // Actions draw after the fixed list, including when custom offsets place them over it.
        if (point.X >= SettingsMin.X && point.X <= SettingsMax.X && point.Y >= SettingsMin.Y && point.Y <= SettingsMax.Y)
            return SaberOverlayHit.Settings;
        return point.X >= ListMin.X && point.X <= ListMax.X && point.Y >= ListMin.Y && point.Y <= ListMax.Y
            ? SaberOverlayHit.Kittens : SaberOverlayHit.None;
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
        float minimumSwingY = listTop + (154 + 16) * scale + radius;
        float originalStep = smallRadius * 2 + 12 * scale;
        float step = originalStep - smallRadius / MathF.Sqrt(2);
        float somersaultRaise = smallRadius * 1.5f; // 0.75 button diameters across the whole diamond.
        float centerOffset = radius + 16 * scale + originalStep + smallRadius * 2 - somersaultRaise;
        float forwardOffset = centerOffset - step;
        const float statusGap = 25;
        const float statusHeight = 96;
        float maximumSwingY = origin.Y + size.Y
            - (forwardOffset + step * 2 + smallRadius + (statusGap + statusHeight) * scale + margin);
        if (minimumSwingY > maximumSwingY) return default;
        float swingY = Math.Clamp(origin.Y + size.Y * .5f, minimumSwingY, maximumSwingY);
        var swing = new float2(origin.X + size.X - 284 * scale, swingY);
        var slice = swing + new float2(-130, 110) * scale;
        var min = new float2(origin.X + size.X - margin - 296 * scale, listTop);
        float header = 38 * scale;
        float maximumHeight = Math.Min(size.Y * .34f, swing.Y - radius - min.Y - 16 * scale);
        float desiredHeight = header + Math.Max(1, kittenCount) * row + 50 * scale;
        bool scroll = desiredHeight > maximumHeight;
        float footer = (50 + (scroll ? 22 : 0)) * scale;
        float height = Math.Max(header + row + footer, Math.Min(desiredHeight, maximumHeight));
        var forward = new float2(swing.X, swing.Y + forwardOffset);
        var backward = forward + new float2(0, step * 2);
        float statusWidth = 444 * scale;
        var statusMin = new float2(swing.X - statusWidth * .5f,
            backward.Y + smallRadius + statusGap * scale);
        var statusMax = statusMin + new float2(statusWidth, statusHeight * scale);
        return new(min, min + new float2(296 * scale, height), min.Y + header,
            height - header - footer, footer, row, pad, swing, radius, slice, smallRadius, scale,
            forward, backward, forward + new float2(-step, step), forward + new float2(step, step), statusMin, statusMax,
            showSomersaultButtons);
    }
}
