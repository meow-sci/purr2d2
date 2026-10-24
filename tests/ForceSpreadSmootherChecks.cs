using Purr2D2.Core;

internal static class ForceSpreadSmootherChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Main() => Run();

    public static void Run()
    {
        EaseInAndMonotoneEndpoints();
        RetargetingKeepsVelocity();
        FrameRateAndPartitionConsistency();
        InvalidTimeAndBounds();
        Console.WriteLine("Force spread smoother checks passed (log-space easing, endpoints, retargeting, rates, dt, bounds).");
    }

    private static void EaseInAndMonotoneEndpoints()
    {
        var compression = new ForceSpreadSmoother();
        compression.SetTarget(.05);
        double previous = compression.Spread;
        double first = compression.Advance(1.0 / 60);
        double onePoleFirstFraction = 1 - Math.Exp(-ForceOffsetSmoother.NaturalFrequency / 60);
        Check(first < previous && first > .05
            && Math.Abs(Math.Log(first / previous)) < Math.Abs(Math.Log(.05 / previous)) * onePoleFirstFraction,
            "a rest-start compression has a visibly gentler first increment than the old one-pole blend");

        double second = compression.Advance(1.0 / 60);
        double third = compression.Advance(1.0 / 60);
        double firstStep = Math.Log(previous / first);
        double secondStep = Math.Log(first / second);
        double thirdStep = Math.Log(second / third);
        Check(firstStep < secondStep && secondStep < thirdStep,
            "critical damping eases into motion across the first samples");

        previous = third;
        for (int i = 0; i < 600 && compression.Spread != .05; i++)
        {
            double next = compression.Advance(1.0 / 60);
            Check(next <= previous && next >= .05 && double.IsFinite(next),
                "compression remains finite, monotone, and inside the hard lower bound");
            previous = next;
        }
        Check(compression.Spread == .05 && compression.Target == .05 && compression.Velocity == 0,
            "compression settles at the exact lower endpoint with zero velocity");

        compression.SetTarget(100);
        previous = compression.Spread;
        for (int i = 0; i < 600 && compression.Spread != 100; i++)
        {
            double next = compression.Advance(1.0 / 60);
            Check(next >= previous && next <= 100 && double.IsFinite(next),
                "expansion remains finite, monotone, and inside the hard upper bound");
            previous = next;
        }
        Check(compression.Spread == 100 && compression.Target == 100 && compression.Velocity == 0,
            "expansion settles at the exact upper endpoint with zero velocity");
    }

    private static void RetargetingKeepsVelocity()
    {
        var smoother = new ForceSpreadSmoother();
        smoother.SetTarget(Math.Pow(.9, .125));
        smoother.Advance(.016);
        smoother.Advance(.016);
        double positionBefore = smoother.Spread;
        double velocityBefore = smoother.Velocity;
        Check(velocityBefore < 0, "fractional compression produces a negative log-spread velocity");

        smoother.SetTarget(Math.Pow(.9, -1.25));
        Check(smoother.Spread == positionBefore && smoother.Velocity == velocityBefore,
            "rapid retargeting preserves both displayed position and current velocity");
        double afterReversal = smoother.Advance(.001);
        Check(afterReversal < positionBefore && smoother.Velocity < 0,
            "a reversal carries finite existing motion briefly instead of restarting from rest");

        smoother.SetTarget(.05);
        for (int i = 0; i < 600 && smoother.Spread != .05; i++) smoother.Advance(.016);
        Check(smoother.Spread == .05 && smoother.Velocity == 0,
            "a second rapid retarget settles without ringing or crossing its stationary target");
    }

    private static void FrameRateAndPartitionConsistency()
    {
        static double RunAtRate(int framesPerSecond)
        {
            var smoother = new ForceSpreadSmoother();
            smoother.SetTarget(80);
            for (int i = 0; i < framesPerSecond * 2 / 3; i++)
                smoother.Advance(1.0 / framesPerSecond);
            return smoother.Spread;
        }

        double at30 = RunAtRate(30);
        double at60 = RunAtRate(60);
        double at120 = RunAtRate(120);
        Check(Math.Abs(at30 - at60) < 1e-9 && Math.Abs(at60 - at120) < 1e-9,
            "analytic critical damping gives approximately the same result at 30, 60, and 120 Hz");

        var partitioned = new ForceSpreadSmoother();
        partitioned.SetTarget(80);
        foreach (double step in new[] { .125, .125, .125, .125, 1.0 / 6 }) partitioned.Advance(step);
        Check(Math.Abs(partitioned.Spread - at60) < 1e-9,
            "equivalent partitioned time advances reach approximately the same state");
    }

    private static void InvalidTimeAndBounds()
    {
        var smoother = new ForceSpreadSmoother();
        smoother.SetTarget(.5);
        double start = smoother.Spread;
        Check(smoother.Advance(0) == start && smoother.Advance(-.1) == start
            && smoother.Advance(double.NaN) == start && smoother.Advance(double.PositiveInfinity) == start
            && smoother.Velocity == 0,
            "zero, negative, and nonfinite time are rejected without changing motion state");

        smoother.SetTarget(.001);
        Check(smoother.Target == .05, "targets below the physical bound clamp to 0.05x");
        smoother.SetTarget(20);
        Check(smoother.Target == 20, "the default 20x setting is a valid smooth target");
        smoother.SetTarget(80);
        Check(smoother.Target == 80, "an 80x configured target remains available");
        smoother.SetTarget(100);
        Check(smoother.Target == 100, "the 100x hard limit remains available");
        smoother.SetTarget(1000);
        Check(smoother.Target == 100, "targets above the hard bound clamp to 100x");

        smoother.Reset(10);
        smoother.SetTarget(2);
        Check(smoother.Advance(double.MaxValue) == 2 && smoother.Velocity == 0,
            "an extreme finite step safely snaps to the exact endpoint");
        smoother.Reset(.001);
        Check(smoother.Spread == .05 && smoother.Target == .05 && smoother.Velocity == 0,
            "reset clamps the seed to the hard lower bound and clears velocity");
        smoother.Reset(double.NaN);
        Check(smoother.Spread == 1 && smoother.Target == 1 && smoother.Velocity == 0,
            "an invalid reset seed safely returns to the stationary default factor");
    }
}
