using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Smooths a requested Force group offset without touching game state.</summary>
public sealed class ForceOffsetSmoother
{
    public const double NaturalFrequency = 18;
    private const double SettledPositionEpsilon = 1e-5;
    private const double SettledVelocityEpsilon = 1e-4;

    public double3 Offset { get; private set; }
    public double3 Velocity { get; private set; }
    public double3 Target { get; private set; }

    public void SetTarget(double3 target)
    {
        if (Finite(target)) Target = Clamp(target);
    }

    public void Reset(double3 offset = default)
    {
        Offset = Target = Finite(offset) ? Clamp(offset) : double3.Zero;
        Velocity = double3.Zero;
    }

    /// <summary>Advances an analytic critically damped spring by elapsed player time.</summary>
    public double3 Advance(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime <= 0) return Offset;

        double3 error = Target - Offset;
        double distance = error.Length();
        if (distance <= SettledPositionEpsilon && Velocity.Length() <= SettledVelocityEpsilon)
        {
            Offset = Target;
            Velocity = double3.Zero;
            return Offset;
        }

        // Limit only the velocity component that could carry the displayed offset
        // through a stationary target. This preserves smooth motion while the pointer
        // is moving, then lets the drag settle without ringing after release.
        if (distance > SettledPositionEpsilon)
        {
            double3 towardTarget = error / distance;
            double towardSpeed = double3.Dot(Velocity, towardTarget);
            double maximumTowardSpeed = NaturalFrequency * distance;
            if (towardSpeed > maximumTowardSpeed)
                Velocity -= towardTarget * (towardSpeed - maximumTowardSpeed);
        }

        double scaledTime = NaturalFrequency * deltaTime;
        if (!double.IsFinite(scaledTime) || scaledTime > 50)
        {
            Offset = Target;
            Velocity = double3.Zero;
            return Offset;
        }

        double decay = Math.Exp(-scaledTime);
        double3 displacement = Offset - Target;
        double3 accelerationTerm = Velocity + displacement * NaturalFrequency;
        double3 nextOffset = Target + (displacement + accelerationTerm * deltaTime) * decay;
        double3 nextVelocity = (Velocity - accelerationTerm * scaledTime) * decay;
        if (!Finite(nextOffset) || !Finite(nextVelocity))
        {
            Offset = Target;
            Velocity = double3.Zero;
            return Offset;
        }

        Offset = Clamp(nextOffset);
        Velocity = Offset == nextOffset ? nextVelocity : double3.Zero;
        if ((Target - Offset).Length() <= SettledPositionEpsilon && Velocity.Length() <= SettledVelocityEpsilon)
        {
            Offset = Target;
            Velocity = double3.Zero;
        }
        return Offset;
    }

    private static double3 Clamp(double3 value)
    {
        double largest = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        if (largest <= ForceOverlayDrag.MaximumOffsetMeters)
            return value.Length() <= ForceOverlayDrag.MaximumOffsetMeters
                ? value
                : value / value.Length() * ForceOverlayDrag.MaximumOffsetMeters;
        double3 scaled = value / largest;
        return scaled / scaled.Length() * ForceOverlayDrag.MaximumOffsetMeters;
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
