namespace Purr2D2.Core;

/// <summary>Bounds how many replacement vehicles one Force breakup action may create.</summary>
internal static class ForceBreakupLimits
{
    internal const int Minimum = 2;
    internal const int Default = 50;
    internal const int Maximum = ForceCaptureLimits.Maximum;

    internal static int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}
