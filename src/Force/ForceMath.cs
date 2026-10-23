using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Force;

internal readonly record struct ForceRigidState(double3 Position, double3 Velocity, doubleQuat Body2Cci, double3 BodyRates);

/// <summary>Rigid-follow arithmetic in an inertial parent frame, independent of native objects.</summary>
internal static class ForceMath
{
    internal const double AngryRadius = 2;
    internal const double ThrowSpeed = 25;
    internal const double MaximumOffset = 25;

    internal static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    internal static bool Finite(doubleQuat value) => double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Z) && double.IsFinite(value.W);

    internal static bool TryFollow(double3 ownerPosition, double3 ownerVelocity, doubleQuat ownerBody2Cci,
        double3 ownerBodyRates, double3 initialPositionBody, doubleQuat sourceToOwner, double3 groupOffsetBody,
        out ForceRigidState state)
    {
        state = default;
        if (!Finite(ownerPosition) || !Finite(ownerVelocity) || !Finite(ownerBodyRates)
            || !Finite(initialPositionBody) || !Finite(groupOffsetBody) || !Finite(ownerBody2Cci)
            || !Finite(sourceToOwner)) return false;
        var ownerRotation = doubleQuat.NormalizeOrZero(ownerBody2Cci);
        var rotation = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(sourceToOwner, ownerRotation));
        if (ownerRotation == default || rotation == default) return false;
        var offset = double3.Transform(initialPositionBody + groupOffsetBody, ownerRotation);
        var angularVelocity = double3.Transform(ownerBodyRates, ownerRotation);
        var velocity = ownerVelocity + double3.Cross(angularVelocity, offset);
        var position = ownerPosition + offset;
        var bodyRates = double3.Transform(angularVelocity, doubleQuat.Inverse(rotation));
        if (!Finite(position) || !Finite(velocity) || !Finite(bodyRates)) return false;
        state = new(position, velocity, rotation, bodyRates);
        return true;
    }

    internal static double3 ThrowVelocity(ForceRigidState state, double3 ownerPosition, doubleQuat ownerBody2Cci)
    {
        var direction = state.Position - ownerPosition;
        if (direction.LengthSquared() < 1e-12) direction = double3.Transform(double3.UnitX, ownerBody2Cci);
        return state.Velocity + double3.NormalizeOrZero(direction) * ThrowSpeed;
    }

    internal static bool ValidOffset(double3 offset) => Finite(offset)
        && offset.LengthSquared() <= (MaximumOffset + .00001) * (MaximumOffset + .00001);

    internal static double SurfaceDistanceSquared(double3 point, double3 a, double3 b, double3 c)
    {
        if (!Finite(point) || !Finite(a) || !Finite(b) || !Finite(c)) return double.PositiveInfinity;
        var triangle = new CutTriangle(P(a), P(b), P(c));
        if (triangle.Normal.LengthSquared < .5) return double.PositiveInfinity;
        return (CutGeometry.ClosestPoint(P(point), triangle) - P(point)).LengthSquared;
    }

    private static CutPoint P(double3 value) => new(value.X, value.Y, value.Z);
}
