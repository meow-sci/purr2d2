namespace Purr2D2.Core;

/// <summary>The original full swing and recovery, measured in player seconds.</summary>
internal static class SaberSwing
{
    internal const double Duration = .62;
    internal static float Angle(double progress) => progress is <= 0 or >= 1 ? 0
        : (float)(Math.Sin(progress * Math.PI * 2) * 1.05);
}
