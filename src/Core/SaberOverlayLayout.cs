using Brutal.Numerics;

namespace Purr2D2.Core;

public enum SaberOverlayHit { None, Kittens, Swing, Slice, SomersaultForward, SomersaultBackward, SomersaultLeft, SomersaultRight, Ignite, AutoSwing, Drop }

/// <summary>Viewport-relative geometry shared by drawing and exact pointer hit testing.</summary>
public readonly record struct SaberOverlayLayout(float2 ListMin, float2 ListMax, float ListBodyTop,
    float ListBodyHeight, float ListFooterHeight, float RowHeight, float Padding,
    float2 SwingCenter, float SwingRadius, float2 SliceCenter, float SliceRadius, float Scale,
    float2 ForwardCenter, float2 BackwardCenter, float2 LeftCenter, float2 RightCenter,
    float2 StatusMin, float2 StatusMax, bool ShowSomersaultButtons)
{
    public bool ShowKittenList { get; init; } = true;
    public bool ShowBFL9KControls { get; init; } = true;
    /// <summary>Regular-font dimensions for the no-wrap Auto Swing duration label.</summary>
    public float AutoSwingValueWidth { get; init; } = 48;
    public float AutoSwingValueHeight { get; init; } = 16;
    public float DropSpinValueWidth { get; init; } = 48;
    public float DropSpinValueHeight { get; init; } = 16;
    public bool Valid => RowHeight > 0 && ListBodyHeight > 0;
    public float IgniteRadius => SliceRadius;
    public float SwingSliceEdgeGap => (SliceCenter - SwingCenter).Length() - SwingRadius - SliceRadius;
    public float2 IgniteCenter => SwingCenter + ActionButtonOffset(1, Scale);
    public float2 DropCenter => SwingCenter + ActionButtonOffset(2, Scale);
    public float2 AutoSwingCenter => SwingCenter + ActionButtonOffset(3, Scale);

    /// <summary>Equal radial and neighbor edge gaps, mirrored for the left-side Furce controls.</summary>
    internal static float2 ActionButtonOffset(int index, float scale, bool mirrored = false)
    {
        // Keep Slice's familiar lower-left anchor and continue around the same circle.
        float distance = MathF.Sqrt(130 * 130 + 110 * 110);
        float gap = distance - 132 - 27;
        float step = 2 * MathF.Asin((54 + gap) / (2 * distance));
        float angle = MathF.Atan2(110, -130) - index * step;
        var offset = new float2(MathF.Cos(angle), MathF.Sin(angle)) * (distance * scale);
        return mirrored ? new(-offset.X, offset.Y) : offset;
    }
    public float AutoSwingPillTravel => 120 * Scale;
    public float DropPillTravel => AutoSwingPillTravel;
    public float2 DropPillMin => DropCenter - new float2(IgniteRadius);
    public float2 DropPillMax => DropCenter + new float2(IgniteRadius, IgniteRadius + DropPillTravel);
    public float2 DropSpinValueLabelPosition(float markerY) => new(
        DropPillMax.X + AutoSwingValueLabelGap, markerY - DropSpinValueHeight * .5f);
    public float2 DropSpinValueEnvelopeMin => DropSpinValueLabelPosition(DropCenter.Y);
    public float2 DropSpinValueEnvelopeMax => DropSpinValueLabelPosition(DropCenter.Y + DropPillTravel)
        + new float2(DropSpinValueWidth + AutoSwingValueShadowOffset, DropSpinValueHeight + AutoSwingValueShadowOffset);
    public float IgnitePillTravel => AutoSwingPillTravel;
    public float2 IgnitePillMin => IgniteCenter - new float2(IgniteRadius);
    public float2 IgnitePillMax => IgniteCenter + new float2(IgniteRadius, IgniteRadius + IgnitePillTravel);
    public float AutoSwingPillWidth => 2 * IgniteRadius;
    public float2 AutoSwingPillMin => AutoSwingCenter - new float2(IgniteRadius);
    public float2 AutoSwingPillMax => AutoSwingCenter + new float2(IgniteRadius,
        IgniteRadius + AutoSwingPillTravel);
    public float AutoSwingValueLabelGap => Math.Max(4, 6 * Scale);
    public float AutoSwingValueShadowOffset => Math.Max(1, Scale);
    public float2 AutoSwingValueLabelPosition(float markerY) => new(
        AutoSwingPillMax.X + AutoSwingValueLabelGap, markerY - AutoSwingValueHeight * .5f);
    public float2 AutoSwingValueEnvelopeMin => AutoSwingValueLabelPosition(AutoSwingCenter.Y);
    public float2 AutoSwingValueEnvelopeMax => AutoSwingValueLabelPosition(
        AutoSwingCenter.Y + AutoSwingPillTravel)
        + new float2(AutoSwingValueWidth + AutoSwingValueShadowOffset,
            AutoSwingValueHeight + AutoSwingValueShadowOffset);
    public float SwingMarkerRadius => 5 * Scale;
    public float SwingTravelRadius => SwingRadius - SwingMarkerRadius;
    public float2 SwingIndicator(float2 offset) => SwingCenter + SaberOverlayDrag.ClampDisk(offset) * SwingTravelRadius;
    public float SomersaultRadius => SliceRadius;
    /// <summary>The checkbox, swatch and name area stays the original 28-pixel row height.</summary>
    public float TopRowHeight => 28 * Scale;
    public float ColorSwatchSize => TopRowHeight * .62f;
    public float2 ColorSwatchMin(int row, float scroll) => new(ListMax.X - Padding - ColorSwatchSize,
        ListBodyTop + row * RowHeight - scroll + TopRowHeight * .18f);
    public float DeploymentBarHeight => 5 * Scale;
    public float2 DeploymentBarMin(int row, float scroll) => new(ListMin.X + Padding,
        ListBodyTop + row * RowHeight - scroll + TopRowHeight
            + (RowHeight - TopRowHeight - DeploymentBarHeight) * .5f);
    public float2 DeploymentBarMax(int row, float scroll) => new(ListMax.X - Padding,
        DeploymentBarMin(row, scroll).Y + DeploymentBarHeight);
    public float2 ControlsMin => new(Math.Min(Math.Min(DropPillMin.X, DropSpinValueEnvelopeMin.X), Math.Min(IgnitePillMin.X, Math.Min(Math.Min(Math.Min(SwingCenter.X - SwingRadius,
            SliceCenter.X - SliceRadius), Math.Min(StatusMin.X, AutoSwingValueEnvelopeMin.X)), AutoSwingPillMin.X))),
        Math.Min(Math.Min(DropPillMin.Y, DropSpinValueEnvelopeMin.Y), Math.Min(IgnitePillMin.Y, Math.Min(Math.Min(Math.Min(SwingCenter.Y - SwingRadius, StatusMin.Y), AutoSwingPillMin.Y), AutoSwingValueEnvelopeMin.Y))));
    public float2 ControlsMax => new(Math.Max(Math.Max(DropPillMax.X, DropSpinValueEnvelopeMax.X), Math.Max(IgnitePillMax.X, Math.Max(Math.Max(StatusMax.X, AutoSwingPillMax.X), AutoSwingValueEnvelopeMax.X))),
        Math.Max(Math.Max(DropPillMax.Y, DropSpinValueEnvelopeMax.Y), Math.Max(IgnitePillMax.Y, Math.Max(Math.Max(StatusMax.Y, AutoSwingPillMax.Y), AutoSwingValueEnvelopeMax.Y))));

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
        if (!Valid || !ShowKittenList || row < 0 || !float.IsFinite(scroll) || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.Y < ListBodyTop || point.Y >= ListBodyTop + ListBodyHeight) return false;
        var min = ColorSwatchMin(row, scroll);
        return point.X >= min.X && point.X <= min.X + ColorSwatchSize
            && point.Y >= min.Y && point.Y <= min.Y + ColorSwatchSize;
    }
    public bool IsTopRow(float2 point, int row, float scroll)
    {
        if (!Valid || !ShowKittenList || row < 0 || !float.IsFinite(scroll) || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.Y < ListBodyTop || point.Y >= ListBodyTop + ListBodyHeight) return false;
        float top = ListBodyTop + row * RowHeight - scroll;
        return point.Y >= top && point.Y < top + TopRowHeight;
    }
    public bool IsDeploymentBar(float2 point, int row, float scroll)
    {
        if (!Valid || !ShowKittenList || row < 0 || !float.IsFinite(scroll) || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.Y < ListBodyTop || point.Y >= ListBodyTop + ListBodyHeight) return false;
        float rowTop = ListBodyTop + row * RowHeight - scroll;
        return point.X >= ListMin.X + Padding && point.X <= ListMax.X - Padding
            && point.Y >= rowTop + TopRowHeight && point.Y < rowTop + RowHeight;
    }
    public SaberOverlayHit Hit(float2 point)
    {
        if (!Valid) return SaberOverlayHit.None;
        if (ShowBFL9KControls)
        {
            if (SaberOverlayDrag.ContainsCircle(point, SwingCenter, SwingRadius)) return SaberOverlayHit.Swing;
            if (SaberOverlayDrag.ContainsCircle(point, SliceCenter, SliceRadius)) return SaberOverlayHit.Slice;
            if (SaberOverlayDrag.ContainsCircle(point, IgniteCenter, IgniteRadius)) return SaberOverlayHit.Ignite;
            if (SaberOverlayDrag.ContainsCircle(point, AutoSwingCenter, IgniteRadius)) return SaberOverlayHit.AutoSwing;
            if (SaberOverlayDrag.ContainsCircle(point, DropCenter, IgniteRadius)) return SaberOverlayHit.Drop;
            if (ShowSomersaultButtons)
            {
                if (SaberOverlayDrag.ContainsCircle(point, ForwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultForward;
                if (SaberOverlayDrag.ContainsCircle(point, BackwardCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultBackward;
                if (SaberOverlayDrag.ContainsCircle(point, LeftCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultLeft;
                if (SaberOverlayDrag.ContainsCircle(point, RightCenter, SomersaultRadius)) return SaberOverlayHit.SomersaultRight;
            }
        }
        // Actions draw after the fixed list, including when custom offsets place them over it.
        return ShowKittenList && point.X >= ListMin.X && point.X <= ListMax.X && point.Y >= ListMin.Y && point.Y <= ListMax.Y
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
        // The Auto Swing duration capsule extends below the new Ignite/Auto row.
        // Reserve that vertical footprint, including the optional somersault rows,
        // so compact 800x600 viewports still have a valid controls envelope.
        float scale = Math.Min(Math.Clamp(fontSize / 16f, .75f, 2), Math.Min(size.X / 840, availableHeight / 824));
        float pad = 12 * scale, margin = 20 * scale, row = 44 * scale;
        float radius = 132 * scale, smallRadius = 27 * scale;
        float listTop = origin.Y + Math.Max(margin, topInset);
        float minimumSwingY = listTop + (154 + 16) * scale + radius;
        float originalStep = smallRadius * 2 + 12 * scale;
        float step = originalStep - smallRadius / MathF.Sqrt(2);
        float somersaultRaise = smallRadius * 1.5f; // 0.75 button diameters across the whole diamond.
        float centerOffset = radius + 16 * scale + originalStep + smallRadius * 2 - somersaultRaise;
        float forwardOffset = centerOffset - step;
        if (showSomersaultButtons)
        {
            float autoRowBottom = radius + smallRadius * 2 + 12 * scale;
            forwardOffset = Math.Max(forwardOffset, autoRowBottom + 12 * scale + smallRadius);
        }
        const float statusGap = 25;
        const float statusHeight = 96;
        float maximumSwingY = origin.Y + size.Y
            - (forwardOffset + step * 2 + smallRadius + (statusGap + statusHeight) * scale + margin);
        if (minimumSwingY > maximumSwingY) return default;
        float swingY = Math.Clamp(origin.Y + size.Y * .5f, minimumSwingY, maximumSwingY);
        var swing = new float2(origin.X + size.X - 284 * scale, swingY);
        var slice = swing + ActionButtonOffset(0, scale);
        var min = new float2(origin.X + size.X - margin - 296 * scale, listTop);
        float header = 38 * scale;
        float maximumHeight = Math.Min(size.Y * .34f, swing.Y - radius - min.Y - 16 * scale);
        float desiredHeight = header + Math.Max(1, kittenCount) * row + pad;
        bool scroll = desiredHeight > maximumHeight;
        float footer = pad + (scroll ? 22 * scale : 0);
        float height = Math.Max(header + row + footer, Math.Min(desiredHeight, maximumHeight));
        var forward = new float2(swing.X, swing.Y + forwardOffset);
        var backward = forward + new float2(0, step * 2);
        float statusWidth = 444 * scale;
        var statusMin = new float2(swing.X - statusWidth * .5f,
            backward.Y + smallRadius + statusGap * scale);
        var statusMax = statusMin + new float2(statusWidth, statusHeight * scale);
        var layout = new SaberOverlayLayout(min, min + new float2(296 * scale, height), min.Y + header,
            height - header - footer, footer, row, pad, swing, radius, slice, smallRadius, scale,
            forward, backward, forward + new float2(-step, step), forward + new float2(step, step), statusMin, statusMax,
            showSomersaultButtons);
        // The transient Drop capsule and full-font label also belong to the
        // default bounds, including unusually short or narrow viewports.
        return layout.WithControlsOffset(origin, size, float2.Zero, topInset);
    }
}
