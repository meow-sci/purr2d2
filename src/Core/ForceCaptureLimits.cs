namespace Purr2D2.Core;

/// <summary>Bounds the total vessels and native clutter in one Force capture.</summary>
internal static class ForceCaptureLimits
{
    internal const int Minimum = 1;
    internal const int Default = 128;
    internal const int Maximum = 512;

    internal static bool Valid(int value) => value is >= Minimum and <= Maximum;
}
