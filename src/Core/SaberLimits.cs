namespace Purr2D2.Core;

/// <summary>Shared by the UI, contact geometry and renderer so visible reach matches cutting reach.</summary>
internal static class SaberLimits
{
    internal const float MaximumBladeLength = 12;
    internal const double MaximumSimulationSpeed = 5;
    internal const double MaximumSimulationStep = .25;

    internal static bool SupportsSpeed(double speed) => double.IsFinite(speed) && speed > 0 && speed <= MaximumSimulationSpeed;
    internal static double AppliedDelta(bool newStep, double speed, double delta) =>
        newStep && SupportsSpeed(speed) && double.IsFinite(delta) && delta > 0 && delta <= MaximumSimulationStep ? delta : 0;
}
