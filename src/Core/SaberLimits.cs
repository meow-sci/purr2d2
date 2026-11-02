namespace Purr2D2.Core;

/// <summary>Shared limits for the visible blade, assisted selection and simulation timing.</summary>
internal static class SaberLimits
{
    internal const float MaximumBladeLength = 50;
    internal const float MinimumSliceReachMultiplier = 1;
    internal const float MaximumSliceReachMultiplier = 4;
    internal const float DefaultSliceReachMultiplier = 2;
    internal const double MaximumSimulationSpeed = 5;
    internal const double MaximumSimulationStep = .25;

    internal static float ValidateSliceReachMultiplier(float multiplier) => float.IsFinite(multiplier)
        ? Math.Clamp(multiplier, MinimumSliceReachMultiplier, MaximumSliceReachMultiplier) : DefaultSliceReachMultiplier;

    // This allowance belongs to assisted SLICE selection, never physical contact or blade rendering.
    internal static double SliceSelectionReach(double visibleBladeLength, float multiplier) =>
        double.IsFinite(visibleBladeLength) && visibleBladeLength > 0
            ? visibleBladeLength * ValidateSliceReachMultiplier(multiplier) : 0;

    internal static bool SupportsSpeed(double speed) => double.IsFinite(speed) && speed > 0 && speed <= MaximumSimulationSpeed;
    internal static double AppliedDelta(bool newStep, double speed, double delta) =>
        newStep && SupportsSpeed(speed) && double.IsFinite(delta) && delta > 0 && delta <= MaximumSimulationStep ? delta : 0;
}
