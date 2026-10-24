using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Pose;

internal static class ForceCaptureLiftChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected:R}, got {actual:R}");
    }

    private static double HeightAt(double elapsed)
    {
        var lift = new ForceCaptureLift();
        lift.Start();
        while (elapsed > 1e-12)
        {
            double step = Math.Min(ForceCaptureLift.MaximumDeltaTime, elapsed);
            lift.Advance(step);
            elapsed -= step;
        }
        return lift.DisplayedHeightMeters;
    }

    public static void Main()
    {
        var lift = new ForceCaptureLift();
        Check(lift.Start() == double3.Zero && lift.Progress == 0 && lift.DisplayedHeightMeters == 0
            && lift.IsActive, "capture lift starts at exactly zero while active");

        var modelUpBody = SaberPoseMath.ModelDirectionToBody(float3.UnitY);
        Check((modelUpBody - ForceCaptureLift.BodyUp).Length() < 1e-12
            && (modelUpBody + double3.UnitZ).Length() < 1e-12,
            "native model +Y maps to owner BODY -Z, the capture lift direction");

        var midpoint = new ForceCaptureLift();
        midpoint.Start();
        midpoint.Advance(.1);
        midpoint.Advance(.1);
        var halfHeight = midpoint.Advance(.025);
        Near(midpoint.Progress, .5, 1e-14, "quintic smootherstep is one half at the midpoint");
        Near(midpoint.DisplayedHeightMeters, .5, 1e-14, "midpoint lift height");
        Check(halfHeight == new double3(0, 0, -.5), "midpoint offset follows BODY up");
        double quarterHeight = HeightAt(ForceCaptureLift.DurationSeconds * .25);
        double threeQuarterHeight = HeightAt(ForceCaptureLift.DurationSeconds * .75);
        double sevenEighthsHeight = HeightAt(ForceCaptureLift.DurationSeconds * .875);
        double endHeight = HeightAt(ForceCaptureLift.DurationSeconds);
        Check(quarterHeight < .25 && threeQuarterHeight > .75
            && sevenEighthsHeight - threeQuarterHeight > endHeight - sevenEighthsHeight,
            "smootherstep eases in and out, with smaller progress near the endpoint than around three-quarter height");

        var previousProgress = 0.0;
        for (int i = 1; i <= 44; i++)
        {
            double3 offset = lift.Advance(.01);
            Check(lift.Progress >= previousProgress && lift.Progress <= 1 && offset.Z <= 0 && offset.Z >= -1,
                "lift progress and height remain monotone and bounded without overshoot");
            previousProgress = lift.Progress;
        }
        Check(previousProgress > .999 && lift.IsActive, "lift approaches its endpoint while remaining active");
        var end = lift.Advance(.01);
        Check(end == new double3(0, 0, -1) && lift.Progress == 1 && lift.DisplayedHeightMeters == 1
            && !lift.IsActive, "lift reaches exactly one metre and becomes inactive at its endpoint");
        Check(lift.Advance(.1) == end && lift.Progress == 1,
            "advancing after completion neither overshoots nor accumulates more height");

        var partitioned = new ForceCaptureLift();
        partitioned.Start();
        partitioned.Advance(.03);
        partitioned.Advance(.02);
        var singleStep = new ForceCaptureLift();
        singleStep.Start();
        singleStep.Advance(.05);
        Near(partitioned.DisplayedHeightMeters, singleStep.DisplayedHeightMeters, 1e-14,
            "partitioned and single-step elapsed time produce the same easing");

        var bounded = new ForceCaptureLift();
        bounded.Start();
        var initial = bounded.Advance(0);
        bounded.Advance(-1);
        bounded.Advance(double.NaN);
        bounded.Advance(double.PositiveInfinity);
        Check(bounded.OffsetBody == initial && bounded.Progress == 0,
            "zero, negative, and nonfinite dt do not move the lift");
        bounded.Advance(1);
        double boundedT = ForceCaptureLift.MaximumDeltaTime / ForceCaptureLift.DurationSeconds;
        double boundedProgress = boundedT * boundedT * boundedT
            * (boundedT * (6 * boundedT - 15) + 10);
        Near(bounded.DisplayedHeightMeters, boundedProgress, 1e-14,
            "a large dt is capped to the configured per-frame bound");

        bounded.Reset();
        Check(!bounded.IsActive && bounded.Progress == 0 && bounded.DisplayedHeightMeters == 0
            && bounded.OffsetBody == double3.Zero, "reset clears the lift to exact zero");
        Console.WriteLine("PASS: Force capture lift easing, bounds, direction, completion and reset");
    }
}
