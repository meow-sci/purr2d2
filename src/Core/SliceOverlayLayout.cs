using Brutal.Numerics;

namespace Purr2D2.Core;

internal readonly record struct SliceOverlaySegment(float2 Start, float2 End);

internal readonly record struct SliceOverlayTextColor(byte R, byte G, byte B, byte A);

internal readonly record struct SliceOverlayTextLine(
    string Text,
    float Width,
    float2 Position,
    SliceOverlayTextColor Color);

/// <summary>Builds a clipped corner-bracket target and a readable status block for a manual slice.</summary>
internal readonly record struct SliceOverlayLayout(
    SliceOverlaySegment[] CornerSegments,
    SliceOverlayTextLine[] TextLines,
    float FontSize,
    float2 TextBoundsMin,
    float2 TextBoundsMax,
    bool TextBelowMarker)
{
    private const float MarkerHalfExtent = 22;
    private const float BracketLength = 9;
    private const float TextGap = 8;
    private const float MinimumTextScale = .65f;
    private static readonly SliceOverlayTextColor White = new(255, 255, 255, 255);

    internal static SliceOverlayLayout Create(float2 center, float2 viewportMin, float2 viewportMax,
        string? partName, float markerScale, float fontSize, float lineHeight,
        float safeMargin, Func<string, float>? measureText)
    {
        if (!IsFinite(center) || !IsFinite(viewportMin) || !IsFinite(viewportMax)
            || !float.IsFinite(markerScale) || markerScale <= 0
            || !float.IsFinite(fontSize) || fontSize <= 0
            || !float.IsFinite(lineHeight) || lineHeight <= 0
            || !float.IsFinite(safeMargin) || measureText is null
            || viewportMax.X <= viewportMin.X || viewportMax.Y <= viewportMin.Y)
            return Empty;

        float halfExtent = MarkerHalfExtent * markerScale;
        float arm = Math.Min(BracketLength * markerScale, halfExtent * .5f);
        var segments = CreateCornerSegments(center, halfExtent, arm);
        float viewportWidth = viewportMax.X - viewportMin.X;
        float viewportHeight = viewportMax.Y - viewportMin.Y;
        if (!float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight)) return Empty;

        float margin = Math.Clamp(safeMargin, 0, Math.Min(viewportWidth, viewportHeight) * .45f);
        float safeLeft = viewportMin.X + margin;
        float safeRight = viewportMax.X - margin;
        float safeTop = viewportMin.Y + margin;
        float safeBottom = viewportMax.Y - margin;
        float safeWidth = safeRight - safeLeft;
        if (safeWidth <= 0 || safeBottom <= safeTop)
            return new(segments, [], fontSize, default, default, false);

        // Keep long names to a readable width so the text remains near its target.
        float labelWidth = Math.Min(safeWidth, 320 * markerScale);
        float aboveHeight = Math.Max(0, center.Y - halfExtent - TextGap - safeTop);
        float belowHeight = Math.Max(0, safeBottom - center.Y - halfExtent - TextGap);
        string label = "slicing...\n" + (partName ?? string.Empty);

        var attempts = new List<(float FontSize, float LineHeight, float BlockHeight, OverlayStatusTextLine[] Lines)>();
        for (float scale = 1; scale >= MinimumTextScale; scale -= .05f)
        {
            float candidateLineHeight = lineHeight * scale;
            float candidateFontSize = fontSize * scale;
            float MeasureScaled(string text)
            {
                float measured = measureText(text);
                return float.IsFinite(measured) && measured >= 0 ? measured * scale : 0;
            }

            var wrapped = OverlayStatusTextLayout.Create(label,
                new(0, safeTop), new(labelWidth, safeBottom), candidateLineHeight, MeasureScaled);
            float blockHeight = wrapped.Length * candidateLineHeight;
            if (!float.IsFinite(blockHeight) || wrapped.Length == 0) continue;

            var attempt = (candidateFontSize, candidateLineHeight, blockHeight, wrapped);
            if (blockHeight <= aboveHeight) return Place(attempt, below: false);
            attempts.Add(attempt);
        }

        // Prefer above-target copy at every readable size. Only move it below the
        // marker when no readable above-target layout fits the viewport.
        foreach (var attempt in attempts)
            if (attempt.BlockHeight <= belowHeight) return Place(attempt, below: true);

        return new(segments, [], fontSize * MinimumTextScale, default, default, false);

        SliceOverlayLayout Place((float FontSize, float LineHeight, float BlockHeight,
            OverlayStatusTextLine[] Lines) attempt, bool below)
        {
            float top = below ? center.Y + halfExtent + TextGap
                : center.Y - halfExtent - TextGap - attempt.BlockHeight;
            float bottom = top + attempt.BlockHeight;
            if (top < safeTop - .01f || bottom > safeBottom + .01f)
                return new(segments, [], attempt.FontSize, default, default, below);

            var lines = new SliceOverlayTextLine[attempt.Lines.Length];
            for (int index = 0; index < attempt.Lines.Length; index++)
            {
                float x = Math.Clamp(center.X - attempt.Lines[index].Width * .5f,
                    safeLeft, safeRight - attempt.Lines[index].Width);
                lines[index] = new(attempt.Lines[index].Text, attempt.Lines[index].Width,
                    new(x, top + index * attempt.LineHeight), White);
            }
            return new(segments, lines, attempt.FontSize,
                new(safeLeft, top), new(safeRight, bottom), below);
        }
    }

    private static SliceOverlaySegment[] CreateCornerSegments(float2 center, float halfExtent, float arm)
    {
        float left = center.X - halfExtent;
        float right = center.X + halfExtent;
        float top = center.Y - halfExtent;
        float bottom = center.Y + halfExtent;
        return
        [
            new(new(left, top), new(left + arm, top)),
            new(new(left, top), new(left, top + arm)),
            new(new(right - arm, top), new(right, top)),
            new(new(right, top), new(right, top + arm)),
            new(new(left, bottom), new(left + arm, bottom)),
            new(new(left, bottom - arm), new(left, bottom)),
            new(new(right - arm, bottom), new(right, bottom)),
            new(new(right, bottom - arm), new(right, bottom)),
        ];
    }

    private static bool IsFinite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static SliceOverlayLayout Empty => new([], [], 0, default, default, false);
}
