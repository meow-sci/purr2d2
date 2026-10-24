using System;
using System.Globalization;
using System.Linq;
using Purr2D2.Core;
using Brutal.Numerics;

internal static class OverlayTextChecks
{
    internal static void Run()
    {
        var topAligned = OverlayStatusTextLayout.Create("A wide result\nabc def", new(20, 30), new(140, 90), 12,
            MeasureTextElements);
        Check(topAligned.Select(line => line.Text).SequenceEqual(new[] { "A wide", "result", "abc def" }),
            "words wrap at measured font width while explicit newlines start a new paragraph");
        Check(topAligned.Select((line, index) => Math.Abs(line.Position.Y - (30 + index * 12)) < 1e-5f).All(value => value),
            "top-aligned lines use consistent font-height spacing");
        Check(topAligned.All(line => Math.Abs(line.Position.X + line.Width * .5f - 80) < 1e-5f),
            "each wrapped line is centered independently inside the status box");

        var bottomAligned = OverlayStatusTextLayout.Create("A wide result\nabc def", new(20, 30), new(140, 90), 12,
            MeasureTextElements, bottomAligned: true);
        Check(bottomAligned.Select((line, index) => Math.Abs(line.Position.Y - (54 + index * 12)) < 1e-5f).All(value => value)
            && Math.Abs(bottomAligned[^1].Position.Y + 12 - 90) < 1e-5f,
            "bottom-aligned text keeps its final line at the bottom edge of the clipped status box");
        Check(bottomAligned.All(line => Math.Abs(line.Position.X + line.Width * .5f - 80) < 1e-5f),
            "bottom alignment preserves independent horizontal centering");

        var explicitLines = OverlayStatusTextLayout.Create("one\n\nTwo\r\nThree\rFour", new(0, 0), new(1000, 100), 10,
            MeasureTextElements);
        Check(explicitLines.Select(line => line.Text).SequenceEqual(new[] { "one", "", "Two", "Three", "Four" }),
            "empty lines and CR, CRLF, and LF breaks are preserved");

        const string longWord = "abcdef😀z";
        var longWordLines = OverlayStatusTextLayout.Create(longWord, new(0, 0), new(20, 100), 10, MeasureTextElements);
        Check(longWordLines.Select(line => line.Text).SequenceEqual(new[] { "ab", "cd", "ef", "😀z" })
            && string.Concat(longWordLines.Select(line => line.Text)) == longWord,
            "an overlong token breaks at Unicode text-element boundaries without losing characters");
        Check(longWordLines.All(line => line.Width <= 20 && IsWellFormedUtf16(line.Text)),
            "long-token segments stay within the measured width and do not split surrogate pairs");

        Check(OverlayStatusTextLayout.Create("safe", new(float.NaN, 0), new(100, 40), 12, MeasureTextElements).Length == 0
            && OverlayStatusTextLayout.Create("safe", new(0, 0), new(100, 40), float.PositiveInfinity, MeasureTextElements).Length == 0
            && OverlayStatusTextLayout.Create("safe", new(0, 0), new(100, 40), 12, _ => float.NaN)
                .All(line => float.IsFinite(line.Position.X) && float.IsFinite(line.Position.Y)),
            "invalid bounds and measurement results cannot create nonfinite draw positions");
    }

    private static float MeasureTextElements(string text)
    {
        int count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) count++;
        return count * 10;
    }

    private static bool IsWellFormedUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
