using Brutal.Numerics;
using Purr2D2.Core;

internal static class SomersaultMotionChecks
{
    internal static void Run()
    {
        GravityAndTravel();
        FramesAndVelocityBounds();
        RunningMomentum();
        SequenceAcknowledgementAndContact();
        EarlyImpactAndTimeout();
    }

    private static void GravityAndTravel()
    {
        foreach (double gravity in new[] { 9.81, 3.71, 1.62 })
        {
            Check(SomersaultProfile.TryCreate(gravity, out var p), "Earth/Mars/Luna gravity is supported");
            Near(p.UpSpeed * p.UpSpeed / (2 * gravity), 1.3, "gravity-derived launch reaches the 30% higher apex");
            Near(p.UpSpeed * p.FlightSeconds - .5 * gravity * p.FlightSeconds * p.FlightSeconds, 0,
                "estimated airtime is the ballistic return time");
            Near(p.TravelSpeed * p.FlightSeconds, 1.5, "chosen travel is independent of local gravity");
        }
        SomersaultProfile.TryCreate(9.81, out var earth);
        SomersaultProfile.TryCreate(1.62, out var luna);
        Check(luna.FlightSeconds > earth.FlightSeconds * 2 && luna.UpSpeed < earth.UpSpeed,
            "the same height yields a slower, longer low-gravity move");
        foreach (double gravity in new[] { 0, -.1, .01, 101, double.NaN, double.PositiveInfinity })
            Check(!SomersaultProfile.TryCreate(gravity, out _), "invalid and near-zero gravity cannot arm a leap");
        Check(!SomersaultProfile.TryCreate(9.81, out _, height: 0)
            && !SomersaultProfile.TryCreate(9.81, out _, distance: double.NaN), "invalid movement profiles are rejected");
        Check(!SomersaultMotion.NormalSpeed(0) && SomersaultMotion.NormalSpeed(1) && !SomersaultMotion.NormalSpeed(2),
            "only normal speed launches; pause and warp cannot arm requests");
        Check(!SomersaultMotion.ValidStep(0) && SomersaultMotion.ValidStep(.25)
            && !SomersaultMotion.ValidStep(.251) && !SomersaultMotion.ValidStep(double.NaN),
            "the actual upcoming solver step has a finite bounded duration");
    }

    private static void FramesAndVelocityBounds()
    {
        var position = new double3(0, 0, -1000);
        var up = new double3(0, 0, -1);
        var right = new double3(0, -1, 0);
        foreach (SomersaultDirection direction in Enum.GetValues<SomersaultDirection>())
        {
            Check(SomersaultMotion.TryLaunch(position, double3.Zero, doubleQuat.Identity, doubleQuat.Identity,
                direction, 9.81, out var launch, out _), "each heading-relative direction produces a valid launch");
            double3 expected = direction switch
            {
                SomersaultDirection.Backward => -double3.UnitX,
                SomersaultDirection.Left => -right,
                SomersaultDirection.Right => right,
                _ => double3.UnitX
            };
            Near(launch.DeltaVelocityPhys, expected * launch.Profile.TravelSpeed + up * launch.Profile.UpSpeed,
                "forward/backward/left/right use the native body +X forward / -Z up basis");
            Near(launch.FacingPhys, double3.UnitX, "sideways/backward motion retains launch heading");
        }
        doubleQuat body2Ccf = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .73);
        doubleQuat body2Phys = doubleQuat.CreateFromAxisAngle(double3.UnitY, -.39);
        var walking = new double3(2.4, -.7, .1);
        Check(SomersaultMotion.TryLaunch(position, walking, body2Ccf, body2Phys,
            SomersaultDirection.Forward, 9.81, out var rotated, out _), "nontrivial CCF/physics frames support a walking launch");
        double3 deltaCcf = double3.Transform(double3.Transform(rotated.DeltaVelocityPhys, doubleQuat.Inverse(body2Phys)), body2Ccf);
        var projectedForward = double3.Transform(double3.UnitX, body2Ccf);
        projectedForward -= up * double3.Dot(projectedForward, up);
        projectedForward = double3.Normalize(projectedForward);
        var desired = projectedForward * rotated.Profile.TravelSpeed + up * rotated.Profile.UpSpeed;
        Near(walking + deltaCcf, desired, "walking speed is corrected toward the profile instead of added twice");
        var frameVelocity = new double3(4200, -1700, 300);
        Near(frameVelocity + walking + deltaCcf, frameVelocity + desired,
            "adding a rotated delta preserves a large moving-frame velocity");
        Check(!SomersaultMotion.TryLaunch(position, new(9, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out _, out var reason) && reason.Contains("Slow"),
            "fast surface motion is declined rather than abruptly cancelled");
        Check(SomersaultMotion.TryLaunch(position, new(8, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Backward, 9.81, out _, out _), "the bounded correction allows a backward running flip");
        Check(!SomersaultMotion.TryLaunch(position, new(8, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Backward, 100, out _, out _), "an excessive combined reversal and vertical impulse is still rejected");
        Check(SomersaultMotion.TryLaunch(position, new(8, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out _, out _), "direction-specific correction can allow the same speed forward");
        Check(!SomersaultMotion.TryLaunch(double3.Zero, double3.Zero, doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out _, out _), "a missing radial surface frame is rejected");
        Check(!SomersaultMotion.TryLaunch(position, double3.Zero, new(0, 0, 0, 0), doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out _, out _), "invalid orientations cannot enter velocity transforms");
    }

    private static void RunningMomentum()
    {
        var position = new double3(0, 0, -1000);
        foreach (double gravity in new[] { 9.81, 3.71, 1.62 })
        foreach (SomersaultDirection direction in Enum.GetValues<SomersaultDirection>())
        {
            var incoming = new double3(5, 0, .2);
            Check(SomersaultMotion.TryLaunch(position, incoming, doubleQuat.Identity, doubleQuat.Identity,
                direction, gravity, out var launch, out _), "all four running flips launch on Earth/Mars/Luna");
            double3 travel = direction switch
            {
                SomersaultDirection.Backward => -double3.UnitX,
                SomersaultDirection.Left => double3.UnitY,
                SomersaultDirection.Right => -double3.UnitY,
                _ => double3.UnitX
            };
            var velocity = incoming + launch.DeltaVelocityPhys;
            Near(velocity, travel * 5.5 - double3.UnitZ * launch.Profile.UpSpeed,
                "a running launch is 10% faster in the selected direction without double-counting incoming velocity");
            Near(velocity.Z * velocity.Z / (2 * gravity), 1.495, "a running launch has a 15% higher apex");
            Near(launch.Profile.Distance, 5.5 * launch.Profile.FlightSeconds,
                "travel grows with momentum and airtime instead of being capped at the standing distance");
            Near(velocity.Z * launch.Profile.FlightSeconds + .5 * gravity * Math.Pow(launch.Profile.FlightSeconds, 2), 0,
                "animation timing follows the higher ballistic arc");
        }
        foreach (double speed in new[] { 0, 2, 3.5, 5, 8 })
        {
            Check(SomersaultMotion.TryLaunch(position, new(speed, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
                SomersaultDirection.Forward, 9.81, out var launch, out _), "standing through bounded running speeds are supported");
            double blend = Math.Clamp((speed - 2) / 3, 0, 1);
            Near(launch.Profile.Height, 1.3 * (1 + .15 * blend), "height blends smoothly from walking to running");
            Check(launch.Profile.TravelSpeed >= speed, "launch never reduces the incoming ground speed");
            if (speed == 0) Near(launch.Profile.Distance, 1.5, "standing flips retain their original travel");
            if (speed == 2) Near(launch.Profile.TravelSpeed, 2, "walking momentum is retained without a running boost");
        }
        Check(SomersaultMotion.TryLaunch(position, new(3, 0, 0), doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out var tuned, out _, walkSpeed: 1, runSpeed: 3),
            "custom native walking/running thresholds are supported");
        Near(tuned.Profile.TravelSpeed, 3.3, "boost follows native tuning rather than a hardcoded run threshold");
        Check(!SomersaultMotion.TryLaunch(position, double3.Zero, doubleQuat.Identity, doubleQuat.Identity,
            SomersaultDirection.Forward, 9.81, out _, out _, runSpeed: double.NaN), "invalid tuning cannot produce a launch");
    }

    private static void SequenceAcknowledgementAndContact()
    {
        SomersaultProfile.TryCreate(9.81, out var profile);
        var sequence = new SomersaultSequence();
        Check(sequence.Queue() && !sequence.Queue(), "one pending request blocks repeat commands");
        sequence.AdvanceTo(.3, true, false, true, false, .2);
        Check(sequence.RotationRadians == 0 && sequence.Phase == SomersaultPhase.Queued,
            "accepting a click cannot start visual flight");
        Check(sequence.Dispatched(profile) && !sequence.Dispatched(profile), "the worker launch token can be consumed only once");
        sequence.AdvanceTo(.3, true, false, true, false, .2);
        Check(sequence.RotationRadians == 0, "dispatch still waits for applied native state acknowledgement");
        Check(sequence.Acknowledge() && !sequence.Acknowledge(), "an applied launch acknowledges only once");
        sequence.AdvanceTo(.1, true, true, false, false, .2);
        Check(sequence.Active && !sequence.HasLeftGround, "the takeoff frame's stale contact is not a landing");
        sequence.AdvanceTo(profile.FlightSeconds * .45, true, false, true, false, .2);
        float rotation = sequence.RotationRadians, tuck = sequence.Tuck;
        Check(rotation > 1 && rotation < MathF.Tau && tuck > .9f && sequence.HasLeftGround,
            "confirmed airborne progress rotates continuously and reaches a tuck");
        foreach (double elapsed in new[] { double.NaN, double.PositiveInfinity, 0, sequence.Elapsed })
            sequence.AdvanceTo(elapsed, false, true, true, false, .2);
        Check(sequence.RotationRadians == rotation && sequence.Tuck == tuck,
            "pause, repeated applied time and invalid time cannot advance or land a move");
        sequence.AdvanceTo(profile.FlightSeconds * .9, true, false, true, false, .2);
        Near(sequence.RotationRadians, MathF.Tau, "the scalar angle reaches a complete turn before estimated contact");
        Check(sequence.Tuck == 0, "the kitten extends before expected touchdown");
        sequence.AdvanceTo(profile.FlightSeconds * 1.8, true, false, true, false, .2);
        Check(sequence.Active && sequence.RotationRadians == MathF.Tau,
            "falling beyond the predicted landing time holds one turn without inventing ground");
        sequence.AdvanceTo(profile.FlightSeconds * 1.9, false, true, true, false, .2);
        Check(sequence.Phase == SomersaultPhase.Recovering, "actual native contact starts recovery");
        sequence.AdvanceTo(sequence.Elapsed + SomersaultSequence.RecoverySeconds + .01, false, true, true, false, .2);
        Check(!sequence.Active && sequence.RotationRadians == 0 && sequence.Tuck == 0,
            "completed recovery releases the action and all pose offsets");
    }

    private static void EarlyImpactAndTimeout()
    {
        SomersaultProfile.TryCreate(1.62, out var profile);
        var sequence = new SomersaultSequence();
        sequence.Queue(); sequence.Dispatched(profile); sequence.Acknowledge();
        sequence.AdvanceTo(profile.FlightSeconds * .2, true, false, true, false, .2);
        float before = sequence.RotationRadians;
        sequence.AdvanceTo(profile.FlightSeconds * .21, false, true, true, true, .2);
        sequence.AdvanceTo(sequence.Elapsed + SomersaultSequence.RecoverySeconds / 2, false, true, true, false, .2);
        Check(sequence.RotationRadians < before && sequence.RotationRadians > 0,
            "an early impact blends along the short return to upright");
        sequence.Reset();
        Check(!sequence.Active && !sequence.HasLeftGround, "cancellation clears liftoff and animation ownership");
        sequence.Queue(); sequence.Dispatched(profile); sequence.Acknowledge();
        sequence.AdvanceTo(profile.FlightSeconds * 3 + 1, true, false, true, false, .2);
        Check(sequence.Phase == SomersaultPhase.Recovering, "an extended fall times out without altering physical motion");
    }

    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 1e-6, message);
    private static void Near(double3 actual, double3 expected, string message) => Check((actual - expected).Length() < 1e-8, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Somersault motion check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
