namespace Purr2D2.Core;

/// <summary>Bounds the configurable maximum shared Force group spread.</summary>
internal static class ForceSpreadLimits
{
    internal const float MinimumMaximum = 1f;
    internal const float DefaultMaximum = 20f;
    internal const float Maximum = 100f;

    internal static double ValidateMaximum(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinimumMaximum, Maximum) : DefaultMaximum;
}
