using Brutal.Numerics;
using Purr2D2.Autopilot;

internal static class EvaApproachChecks
{
    public static void Run()
    {
        VelocityFeedbackAndBraking();
        InvalidInputs();
        PulseApproaches();
    }

    private static void VelocityFeedbackAndBraking()
    {
        const double authority = .2;
        double3 longApproach = EvaApproachGuidance.Acceleration(new(100, 0, 0),
            double3.Zero, double3.Zero, double3.Zero, authority);
        Check(longApproach.X < 0 && Math.Abs(longApproach.Y) < 1e-12
            && longApproach.Length() <= authority + 1e-12,
            "approach commands bounded thrust toward a distant waypoint");

        double3 braking = EvaApproachGuidance.Acceleration(new(.5, 0, 0),
            new(-1, .4, -.3), double3.Zero, double3.Zero, authority);
        Check(braking.X > 0 && braking.Y < 0 && braking.Z > 0,
            "closing faster than the stopping schedule brakes and damps lateral drift");

        double3 settled = EvaApproachGuidance.Acceleration(double3.Zero, double3.Zero,
            double3.Zero, double3.Zero, authority);
        Near(settled, double3.Zero, "matched position and velocity request no thrust");
        double3 damping = EvaApproachGuidance.Acceleration(new(1, 0, 0),
            new(.1, -.2, .3), double3.Zero, double3.Zero, authority, speedLimit: 0);
        Check(double3.Dot(damping, new double3(.1, -.2, .3)) < 0,
            "zero approach speed still damps relative velocity");

        double3 position = new(10, -3, 2);
        double3 velocity = new(-.4, .2, .1);
        double3 offset = new(100, 200, -300);
        double3 frameVelocity = new(3, -2, 1);
        Near(EvaApproachGuidance.Acceleration(position + offset, velocity + frameVelocity,
                offset, frameVelocity, authority),
            EvaApproachGuidance.Acceleration(position, velocity, double3.Zero, double3.Zero, authority),
            "approach is unchanged by a shared position and velocity offset");

        double stoppingDistance = EvaApproachGuidance.StoppingDistance(1, authority);
        Check(stoppingDistance > 1 / (2 * authority)
            && EvaApproachGuidance.StoppingDistance(1, authority / 2) > stoppingDistance
            && EvaApproachGuidance.StoppingDistance(2, authority) > stoppingDistance * 2,
            "stopping lookahead includes latency and grows for speed or reduced thrust");
        Near(EvaApproachGuidance.StoppingDistance(-1, authority), stoppingDistance,
            "stopping lookahead accepts a signed speed magnitude");
        Near(EvaApproachGuidance.StoppingDistance(0, authority), 0,
            "a stationary EVA needs no stopping distance");
    }

    private static void InvalidInputs()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            double3 bad = new(invalid, 0, 0);
            Near(EvaApproachGuidance.Acceleration(bad, double3.Zero, double3.Zero, double3.Zero, .2),
                double3.Zero, "non-finite position requests no thrust");
            Near(EvaApproachGuidance.Acceleration(double3.Zero, bad, double3.Zero, double3.Zero, .2),
                double3.Zero, "non-finite velocity requests no thrust");
            Near(EvaApproachGuidance.Acceleration(double3.Zero, double3.Zero, bad, double3.Zero, .2),
                double3.Zero, "non-finite waypoint requests no thrust");
            Near(EvaApproachGuidance.Acceleration(double3.Zero, double3.Zero, double3.Zero, bad, .2),
                double3.Zero, "non-finite target velocity requests no thrust");
        }
        foreach (double invalid in new[] { 0, -.2, double.NaN, double.PositiveInfinity })
        {
            Near(EvaApproachGuidance.Acceleration(double3.UnitX, double3.Zero,
                double3.Zero, double3.Zero, invalid), double3.Zero,
                "invalid braking authority requests no thrust");
            Check(double.IsPositiveInfinity(EvaApproachGuidance.StoppingDistance(1, invalid)),
                "invalid braking authority cannot promise a finite stopping distance");
        }
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity })
            Near(EvaApproachGuidance.Acceleration(double3.UnitX, double3.Zero,
                double3.Zero, double3.Zero, .2, invalid), double3.Zero,
                "invalid speed limit requests no thrust");
        Check(double.IsPositiveInfinity(EvaApproachGuidance.StoppingDistance(double.NaN, .2)),
            "non-finite speed cannot promise a finite stopping distance");
        Near(EvaApproachGuidance.Acceleration(new(double.MaxValue, 0, 0), double3.Zero,
            new(-double.MaxValue, 0, 0), double3.Zero, .2), double3.Zero,
            "overflowing relative position requests no thrust");
    }

    private static void PulseApproaches()
    {
        Simulate(new(10, 0, 0), double3.Zero, .2, double3.Zero,
            "ten metre approach");
        Simulate(new(100, 0, 0), double3.Zero, .2, double3.Zero,
            "hundred metre approach");
        Simulate(new(10, 3, -2), new(-.25, .45, -.3), .12, double3.Zero,
            "approach with initial closing and lateral drift");
        Simulate(new(10, 0, 0), double3.Zero, .035, double3.Zero,
            "approach with reduced thrust availability");
        Simulate(new(10, -2, 1), new(-.2, .1, -.1), .18, new(.4, -.2, .15),
            "approach to a moving waypoint");
    }

    private static void Simulate(double3 initialOffset, double3 initialRelativeVelocity,
        double authority, double3 targetVelocity, string scenario)
    {
        // FlightComputer accepts discrete direction flags and emits minimum
        // impulses at 0.15 s intervals. A pulse changes velocity by authority *
        // interval, regardless of the guidance command's nonzero magnitude.
        const double dt = .01;
        const double pulseInterval = .15;
        const double duration = 240;
        double3 targetPosition = double3.Zero;
        double3 position = initialOffset;
        double3 velocity = initialRelativeVelocity + targetVelocity;
        double3 initialDirection = initialOffset / initialOffset.Length();
        double nextPulse = .073; // The control and native pulse clocks need not align.
        double deadband = Math.Min(.02, authority * .1);
        double maxOvershoot = 0;
        double holdPositionError = 0;
        double holdSpeedError = 0;
        double arrivalTime = double.PositiveInfinity;
        for (int step = 0; step < (int)(duration / dt); step++)
        {
            double time = step * dt;
            double3 command = EvaApproachGuidance.Acceleration(position, velocity,
                targetPosition, targetVelocity, authority);
            if (!Finite(command) || command.Length() > authority + 1e-10)
                throw new InvalidOperationException("EVA approach check failed: unbounded command during " + scenario);
            if (time + 1e-9 >= nextPulse)
            {
                double Flag(double component) => Math.Abs(component) > deadband ? Math.Sign(component) : 0;
                double3 flags = new(Flag(command.X), Flag(command.Y), Flag(command.Z));
                velocity += flags * (authority * pulseInterval);
                nextPulse += pulseInterval;
            }
            position += velocity * dt;
            targetPosition += targetVelocity * dt;
            double3 error = position - targetPosition;
            double distance = error.Length();
            double relativeSpeed = (velocity - targetVelocity).Length();
            maxOvershoot = Math.Max(maxOvershoot, -double3.Dot(error, initialDirection));
            if (distance < .15 && relativeSpeed < .1 && double.IsPositiveInfinity(arrivalTime))
                arrivalTime = time;
            if (time >= duration - 20)
            {
                holdPositionError = Math.Max(holdPositionError, distance);
                holdSpeedError = Math.Max(holdSpeedError, relativeSpeed);
            }
        }
        Check(arrivalTime < 180, scenario + " reaches the capture tolerance using native pulses");
        Check(maxOvershoot < .25, scenario + " avoids gross target overshoot");
        Check(holdPositionError < .15 && holdSpeedError < .1,
            $"{scenario} holds within 0.15 m and 0.1 m/s for twenty seconds "
            + $"(observed {holdPositionError:F3} m, {holdSpeedError:F3} m/s)");
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static void Near(double3 actual, double3 expected, string message) =>
        Check(Finite(actual) && (actual - expected).Length() < 1e-9, message);

    private static void Near(double actual, double expected, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) < 1e-9, message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("EVA approach check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
