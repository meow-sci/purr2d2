using Brutal.Numerics;

namespace Purr2D2.Core;

internal enum SomersaultDirection { Forward, Backward, Left, Right }

internal readonly record struct SomersaultProfile(double Gravity, double Height, double Distance,
    double UpSpeed, double TravelSpeed, double FlightSeconds)
{
    internal static bool TryCreate(double gravity, out SomersaultProfile profile,
        double height = 1.3, double distance = 1.5)
    {
        profile = default;
        if (!double.IsFinite(gravity) || gravity < .1 || gravity > 100
            || !double.IsFinite(height) || height <= 0 || height > 3
            || !double.IsFinite(distance) || distance < 0 || distance > 5) return false;
        double upSpeed = Math.Sqrt(2 * gravity * height);
        double flight = 2 * upSpeed / gravity;
        profile = new(gravity, height, distance, upSpeed, distance / flight, flight);
        return true;
    }
}

internal readonly record struct SomersaultLaunch(SomersaultProfile Profile, double3 DeltaVelocityPhys,
    double3 FacingPhys, double3 UpPhys);

/// <summary>Unscaled player press times; one transaction may be pending at a time.</summary>
internal sealed class SomersaultCombo
{
    internal const int MaximumJumps = 3;
    internal const double PressWindowSeconds = .5;
    internal int JumpCount { get; private set; }
    internal bool Pending { get; private set; }
    private double _lastPress;
    private bool _closed;

    internal bool CanQueue(double pressTime) => !_closed && !Pending && JumpCount is > 0 and < MaximumJumps
        && double.IsFinite(pressTime) && pressTime >= _lastPress && pressTime <= _lastPress + PressWindowSeconds;

    internal bool Start(double pressTime)
    {
        if (JumpCount != 0 || !double.IsFinite(pressTime)) return false;
        JumpCount = 1; Pending = true; _lastPress = pressTime;
        return true;
    }

    internal bool Queue(double pressTime)
    {
        if (!CanQueue(pressTime)) return false;
        JumpCount++; Pending = true; _lastPress = pressTime;
        return true;
    }

    internal void Acknowledge() => Pending = false;
    internal void Close() { _closed = true; Pending = false; }
    internal void Reset() { JumpCount = 0; Pending = _closed = false; _lastPress = 0; }
}

/// <summary>All launch vectors are deltas; planet/frame translation is never replaced.</summary>
internal static class SomersaultMotion
{
    internal const double MaximumStepSeconds = .25;
    internal const double MaximumSurfaceSpeed = 8;
    // Allow a directional running flip to reverse up to 8 m/s of surface motion
    // into an 8.8 m/s launch, plus the jump, without removing the impulse bound.
    internal const double MaximumDeltaSpeed = 20;

    internal static bool NormalSpeed(double speed) => double.IsFinite(speed) && Math.Abs(speed - 1) < 1e-6;
    internal static bool ValidStep(double dt) => double.IsFinite(dt) && dt > 0 && dt <= MaximumStepSeconds;
    internal static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    /// <summary>Airborne follow-ups add only surface-up velocity, never cancel existing motion.</summary>
    internal static bool TryBoost(double3 positionCcf, doubleQuat body2Ccf, doubleQuat body2Phys,
        double upSpeed, out double3 deltaPhys)
    {
        deltaPhys = default;
        if (!Finite(positionCcf) || positionCcf.LengthSquared() < 1 || !Finite(body2Ccf) || !Finite(body2Phys)
            || !double.IsFinite(upSpeed) || upSpeed <= 0 || upSpeed > MaximumDeltaSpeed) return false;
        double3 up = double3.Normalize(positionCcf);
        deltaPhys = double3.Transform(double3.Transform(up * upSpeed, doubleQuat.Inverse(body2Ccf)), body2Phys);
        return Finite(deltaPhys);
    }

    internal static bool TryLaunch(double3 positionCcf, double3 velocityCcf, doubleQuat body2Ccf,
        doubleQuat body2Phys, SomersaultDirection direction, double gravity, out SomersaultLaunch launch, out string reason,
        double walkSpeed = 2, double runSpeed = 5)
    {
        launch = default;
        reason = "Local gravity is unsupported.";
        if (!SomersaultProfile.TryCreate(gravity, out var profile)) return false;
        reason = "The surface frame is unavailable.";
        if (!Enum.IsDefined(direction) || !Finite(positionCcf) || !Finite(velocityCcf)
            || positionCcf.LengthSquared() < 1 || !Finite(body2Ccf) || !Finite(body2Phys)) return false;
        double3 up = double3.Normalize(positionCcf);
        double3 forward = double3.Transform(double3.UnitX, body2Ccf);
        forward -= up * double3.Dot(forward, up);
        if (!Finite(forward) || forward.LengthSquared() < .0025) return false;
        forward = double3.Normalize(forward);
        double3 right = double3.Normalize(double3.Cross(up, forward));
        double3 travel = direction switch
        {
            SomersaultDirection.Backward => -forward,
            SomersaultDirection.Left => -right,
            SomersaultDirection.Right => right,
            _ => forward
        };
        reason = "Slow down before jumping.";
        if (velocityCcf.LengthSquared() > MaximumSurfaceSpeed * MaximumSurfaceSpeed) return false;
        reason = "Ground movement tuning is unsupported.";
        if (!double.IsFinite(walkSpeed) || !double.IsFinite(runSpeed) || walkSpeed <= 0 || runSpeed <= walkSpeed) return false;
        // Measure actual tangential speed, not the sprint key or the radial
        // velocity on a slope. Ramp the extra height/speed in while accelerating.
        double surfaceSpeed = (velocityCcf - up * double3.Dot(velocityCcf, up)).Length();
        double running = Math.Clamp((surfaceSpeed - walkSpeed) / (runSpeed - walkSpeed), 0, 1);
        if (!SomersaultProfile.TryCreate(gravity, out profile, height: profile.Height * (1 + .15 * running))) return false;
        double travelSpeed = Math.Max(profile.TravelSpeed, surfaceSpeed * (1 + .10 * running));
        // Moving launches travel farther; the standing hop's distance is not a cap.
        profile = profile with { TravelSpeed = travelSpeed, Distance = travelSpeed * profile.FlightSeconds };
        reason = "Slow down before jumping.";
        double3 deltaCcf = up * profile.UpSpeed + travel * profile.TravelSpeed - velocityCcf;
        if (!Finite(deltaCcf) || deltaCcf.LengthSquared() > MaximumDeltaSpeed * MaximumDeltaSpeed) return false;
        // GetStatesCcf includes the moving/rotating frame. Only rotate the delta;
        // transforming the desired small local velocity would discard frame motion.
        doubleQuat ccf2Body = doubleQuat.Inverse(body2Ccf);
        double3 ToPhys(double3 value) => double3.Transform(double3.Transform(value, ccf2Body), body2Phys);
        double3 deltaPhys = ToPhys(deltaCcf);
        double3 facingPhys = ToPhys(forward);
        double3 upPhys = ToPhys(up);
        if (!Finite(deltaPhys) || !Finite(facingPhys) || !Finite(upPhys)) return false;
        launch = new(profile, deltaPhys, double3.Normalize(facingPhys), double3.Normalize(upPhys));
        reason = "";
        return true;
    }

    private static bool Finite(doubleQuat value) => double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Z) && double.IsFinite(value.W) && Math.Abs(value.LengthSquared() - 1) < .01;
}
