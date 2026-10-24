using System;
using System.Linq;
using Brutal.Numerics;
using Purr2D2.Core;

internal static class SliceOverlayLayoutChecks
{
    internal static void Run()
    {
        var center = new float2(400, 300);
        var layout = SliceOverlayLayout.Create(center, new(0, 0), new(800, 600),
            "Structural Support Panel", 1, 16, 18, 8, Measure);

        Check(layout.CornerSegments.Length == 8,
            "the manual target is made from exactly eight corner-bracket strokes");
        Check(layout.CornerSegments.All(segment => IsHorizontal(segment) || IsVertical(segment)),
            "corner brackets use only square-edge horizontal and vertical strokes");
        Check(layout.CornerSegments.All(segment => Math.Abs(Length(segment) - 9) < .01f),
            "each bracket is a short corner stroke rather than part of a closed square outline");
        Check(layout.CornerSegments.All(segment => !CrossesCenter(segment, center)),
            "the target has no center dot or crosshair strokes");
        Check(layout.TextLines.Select(line => line.Text).SequenceEqual(new[] { "slicing...", "Structural Support Panel" }),
            "manual status puts the exact lowercase slicing label above the part name");
        Check(layout.TextLines.All(line => line.Color == new SliceOverlayTextColor(255, 255, 255, 255)),
            "both manual status lines are white");
        Check(!layout.TextBelowMarker && layout.TextBoundsMax.Y <= center.Y - 22 - 8 + .01f,
            "status remains centered above a target when the viewport has room");
        Check(layout.TextLines.All(line => Math.Abs(line.Position.X + line.Width * .5f - center.X) < .01f),
            "each status line is centered over the projected target");

        const string longPartName = "VeryLongPartNameWithoutSpaces";
        var longLayout = SliceOverlayLayout.Create(center, new(0, 0), new(800, 600),
            string.Concat(Enumerable.Repeat(longPartName, 8)), 1, 16, 18, 8, Measure);
        var nameLines = longLayout.TextLines.Skip(1).ToArray();
        Check(nameLines.Length > 1
            && string.Concat(nameLines.Select(line => line.Text)) == string.Concat(Enumerable.Repeat(longPartName, 8)),
            "long part names wrap without dropping or reordering characters");
        Check(longLayout.TextLines.All(line => line.Width <= 320 + .01f)
            && longLayout.TextBoundsMin.Y >= 0 && longLayout.TextBoundsMax.Y <= 600,
            "wrapped label lines fit inside the viewport bounds");

        var nearTop = SliceOverlayLayout.Create(new(400, 30), new(0, 0), new(800, 600),
            "Edge Target", 1, 16, 18, 8, Measure);
        Check(nearTop.TextBelowMarker
            && nearTop.TextBoundsMin.Y >= 30 + 22 + 8 - .01f
            && nearTop.TextBoundsMax.Y <= 600,
            "labels move below the projected target when the top edge leaves no safe space");

        var compactAbove = SliceOverlayLayout.Create(new(400, 125), new(0, 0), new(800, 600),
            string.Concat(Enumerable.Repeat(longPartName, 8)), 1, 16, 18, 8, Measure);
        Check(!compactAbove.TextBelowMarker && compactAbove.FontSize < 16
            && compactAbove.TextBoundsMax.Y <= 125 - 22 - 8 + .01f,
            "a smaller readable label stays above the target when the full-size label would fit below");

        var nearBottom = SliceOverlayLayout.Create(new(400, 570), new(0, 0), new(800, 600),
            "Edge Target", 1, 16, 18, 8, Measure);
        Check(!nearBottom.TextBelowMarker && nearBottom.TextBoundsMin.Y >= 0
            && nearBottom.TextBoundsMax.Y <= 570 - 22 - 8 + .01f,
            "labels stay above a target near the bottom viewport edge");

        var nearLeft = SliceOverlayLayout.Create(new(3, 300), new(0, 0), new(800, 600),
            "Edge Target", 1, 16, 18, 8, Measure);
        Check(nearLeft.TextBoundsMin.X >= 8 && nearLeft.TextBoundsMax.X <= 792,
            "near-edge labels remain within the safe viewport while the marker stays at its projected point");
    }

    private static float Measure(string text) => text.Length * 8;

    private static bool IsHorizontal(SliceOverlaySegment segment) =>
        Math.Abs(segment.Start.Y - segment.End.Y) < .001f && segment.Start.X != segment.End.X;

    private static bool IsVertical(SliceOverlaySegment segment) =>
        Math.Abs(segment.Start.X - segment.End.X) < .001f && segment.Start.Y != segment.End.Y;

    private static float Length(SliceOverlaySegment segment)
    {
        float x = segment.End.X - segment.Start.X;
        float y = segment.End.Y - segment.Start.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    private static bool CrossesCenter(SliceOverlaySegment segment, float2 center) =>
        IsHorizontal(segment)
            ? Math.Abs(segment.Start.Y - center.Y) < .001f
            : Math.Abs(segment.Start.X - center.X) < .001f;

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
