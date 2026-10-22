using Brutal.Numerics;

namespace Purr2D2.Autopilot;

/// <summary>Relative-motion guidance for approaching a stationary or moving EVA waypoint.</summary>
internal static class EvaApproachGuidance
{
    // Native manual RCS pulses are spaced by 0.15 seconds. Reserve two intervals
    // for a newly requested braking pulse and leave authority for lateral damping.
    private const double PulseLatency = .30;
    // The speed schedule must sit inside the collision lookahead envelope:
    // a full native pulse can put actual speed above its continuously sampled
    // target. Using the same envelope for both can reject a safe approach at
    // the very frame where guidance has already requested the braking pulse.
    private const double ApproachBrakingReserve = .6;
    private const double StoppingBrakingReserve = .8;
    private const double PositionGain = .6;
    private const double VelocityGain = 2;

    /// <param name="brakingAcceleration">
    /// Conservative acceleration available from repeated manual pulses, including
    /// their duty cycle; this is not necessarily the continuous full-thrust value.
    /// </param>
    /// <remarks>
    /// The result supplies a direction and bounded velocity feedback. Native
    /// translation flags select whole pulses, so callers must apply a pulse-sized
    /// deadband rather than assume that a smaller command throttles the jets.
    /// </remarks>
    internal static double3 Acceleration(double3 position, double3 velocity,
        double3 desiredPosition, double3 desiredVelocity, double brakingAcceleration,
        double speedLimit = 1.5)
    {
        if (!Finite(position) || !Finite(velocity) || !Finite(desiredPosition)
            || !Finite(desiredVelocity) || !double.IsFinite(brakingAcceleration)
            || brakingAcceleration <= 0 || !double.IsFinite(speedLimit) || speedLimit < 0)
            return double3.Zero;

        double3 error = desiredPosition - position;
        double3 relativeVelocity = velocity - desiredVelocity;
        double distance = error.Length();
        if (!Finite(error) || !Finite(relativeVelocity) || !double.IsFinite(distance))
            return double3.Zero;

        double3 closingVelocity = double3.Zero;
        if (distance > 1e-9)
        {
            double authority = brakingAcceleration * ApproachBrakingReserve;
            if (authority <= 0) return double3.Zero;

            // Solve distance = speed * latency + speed² / (2 * authority).
            // This form avoids subtracting two nearly equal values close in.
            double brakingSpeed = distance / (.5 * (Math.Sqrt(
                PulseLatency * PulseLatency + 2 * (distance / authority)) + PulseLatency));
            double closingSpeed = Math.Min(speedLimit, Math.Min(brakingSpeed, distance * PositionGain));
            closingVelocity = error * (closingSpeed / distance);
        }

        // A velocity target only along the line of approach also removes any
        // relative lateral motion; it must not be added to the closing speed.
        double3 command = (closingVelocity - relativeVelocity) * VelocityGain;
        double length = command.Length();
        if (!Finite(command) || !double.IsFinite(length)) return double3.Zero;
        return length > brakingAcceleration ? command * (brakingAcceleration / length) : command;
    }

    /// <summary>Conservative stopping distance, including the approach pulse latency and thrust reserve.</summary>
    internal static double StoppingDistance(double speed, double brakingAcceleration)
    {
        if (!double.IsFinite(speed) || !double.IsFinite(brakingAcceleration) || brakingAcceleration <= 0)
            return double.PositiveInfinity;
        speed = Math.Abs(speed);
        if (speed == 0) return 0;
        double authority = brakingAcceleration * StoppingBrakingReserve;
        if (authority <= 0) return double.PositiveInfinity;
        return speed * PulseLatency + speed * (.5 * (speed / authority));
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
