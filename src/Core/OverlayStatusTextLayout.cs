using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Brutal.Numerics;

namespace Purr2D2.Core;

internal readonly record struct OverlayStatusTextLine(string Text, float Width, float2 Position);

/// <summary>Wraps status text using measured font widths and lays out every line around the box center.</summary>
internal static class OverlayStatusTextLayout
{
    internal static OverlayStatusTextLine[] Create(string? text, float2 min, float2 max, float lineHeight,
        Func<string, float>? measure, bool bottomAligned = false)
    {
        if (text is null || measure is null || !IsFinite(min) || !IsFinite(max)
            || !float.IsFinite(lineHeight) || lineHeight <= 0
            || max.X <= min.X || max.Y <= min.Y)
            return [];

        float width = max.X - min.X;
        float height = max.Y - min.Y;
        if (!float.IsFinite(width) || !float.IsFinite(height)) return [];

        var lines = new List<string>();
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var paragraph in normalized.Split('\n'))
            WrapParagraph(paragraph, width, measure, lines);

        double blockHeight = (double)lines.Count * lineHeight;
        if (!double.IsFinite(blockHeight)) return [];
        float firstY = bottomAligned ? (float)(max.Y - blockHeight) : min.Y;
        if (!float.IsFinite(firstY)) return [];

        var result = new OverlayStatusTextLine[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            float measuredWidth = Measure(lines[i], measure);
            double x = (double)min.X + ((double)width - measuredWidth) * .5;
            double y = (double)firstY + (double)i * lineHeight;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < -float.MaxValue || x > float.MaxValue
                || y < -float.MaxValue || y > float.MaxValue)
                return [];
            result[i] = new(lines[i], measuredWidth, new((float)x, (float)y));
        }
        return result;
    }

    private static void WrapParagraph(string paragraph, float maxWidth, Func<string, float> measure,
        List<string> lines)
    {
        if (paragraph.Length == 0 || string.IsNullOrWhiteSpace(paragraph))
        {
            lines.Add("");
            return;
        }

        var words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string current = "";
        foreach (var word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(candidate, measure) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
                current = "";
            }

            if (Measure(word, measure) <= maxWidth)
            {
                current = word;
                continue;
            }

            var chunks = SplitLongWord(word, maxWidth, measure);
            for (int i = 0; i < chunks.Count - 1; i++) lines.Add(chunks[i]);
            current = chunks.Count == 0 ? "" : chunks[^1];
        }

        if (current.Length > 0) lines.Add(current);
        else if (words.Length == 0) lines.Add("");
    }

    private static List<string> SplitLongWord(string word, float maxWidth, Func<string, float> measure)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            string candidate = current.ToString() + element;
            if (current.Length > 0 && Measure(candidate, measure) > maxWidth)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(element);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static float Measure(string text, Func<string, float> measure)
    {
        float width = measure(text);
        return float.IsFinite(width) && width >= 0 ? width : 0;
    }

    private static bool IsFinite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
