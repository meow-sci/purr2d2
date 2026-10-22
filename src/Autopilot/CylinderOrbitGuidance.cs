using Brutal.Numerics;

namespace Purr2D2.Autopilot;

internal static class CylinderOrbitGuidance
{
    internal const double FullTurn = Math.PI * 2.0;

    internal static double3 Axis(int index, bool reversed)
    {
        double sign = reversed ? -1 : 1;
        return index switch
        {
            0 => double3.UnitX * sign,
            1 => double3.UnitY * sign,
            2 => double3.UnitZ * sign,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    internal static bool TryRadial(double3 relativePosition, double3 axis, out double axial, out double3 radial)
    {
        axial = double3.Dot(relativePosition, axis);
        double3 projected = relativePosition - axis * axial;
        double length = projected.Length();
        radial = length > 1e-6 && Finite(projected) ? projected / length : double3.Zero;
        return length > 1e-6 && double.IsFinite(axial) && Finite(radial);
    }

    internal static double3 Point(double3 axis, double axial, double3 startRadial, double radius, double angle)
    {
        var rotation = doubleQuat.CreateFromAxisAngle(axis, angle);
        return axis * axial + double3.Transform(startRadial, rotation) * radius;
    }

    internal static double3 Velocity(double3 axis, double3 startRadial, double radius,
        double angle, double angularVelocity)
    {
        double3 radial = double3.Transform(startRadial, doubleQuat.CreateFromAxisAngle(axis, angle));
        return double3.Cross(axis, radial) * (radius * angularVelocity);
    }

    internal static double3 Acceleration(double3 position, double3 velocity,
        double3 desiredPosition, double3 desiredVelocity, double maxAcceleration = 2.0)
    {
        double3 command = (desiredPosition - position) * 1.8 + (desiredVelocity - velocity) * 2.4;
        double length = command.Length();
        if (!Finite(command) || !double.IsFinite(maxAcceleration) || maxAcceleration <= 0) return double3.Zero;
        return length > maxAcceleration ? command * (maxAcceleration / length) : command;
    }

    internal static double AdvanceAngle(double angle, double speed, double radius, double dt, bool clockwise)
    {
        if (!double.IsFinite(angle) || !double.IsFinite(speed) || !double.IsFinite(radius)
            || !double.IsFinite(dt) || speed < 0 || radius <= 1e-6 || dt <= 0) return angle;
        return angle + (clockwise ? -1 : 1) * speed / radius * dt;
    }

    internal static double Progress(double angle) =>
        double.IsFinite(angle) ? Math.Clamp(Math.Abs(angle) / FullTurn, 0, 1) : 0;

    internal static doubleQuat DesiredBody2Cci(double3 bladeDirectionBody, double3 inwardCci,
        double3 cylinderAxisCci, int rollQuarterTurns)
    {
        double3 sourcePrimary = Unit(bladeDirectionBody, double3.UnitY);
        double3 sourceSecondary = Perpendicular(-double3.UnitZ, sourcePrimary);
        if (sourceSecondary.LengthSquared() < 1e-20) sourceSecondary = Perpendicular(double3.UnitX, sourcePrimary);
        double3 destinationPrimary = Unit(inwardCci, -double3.UnitX);
        double3 destinationSecondary = Perpendicular(cylinderAxisCci, destinationPrimary);
        if (destinationSecondary.LengthSquared() < 1e-20) destinationSecondary = Perpendicular(double3.UnitZ, destinationPrimary);
        int quarters = ((rollQuarterTurns % 4) + 4) % 4;
        destinationSecondary = double3.Transform(destinationSecondary,
            doubleQuat.CreateFromAxisAngle(destinationPrimary, quarters * Math.PI / 2.0));

        doubleQuat primaryRotation = FromTo(sourcePrimary, destinationPrimary);
        double3 mappedSecondary = Unit(double3.Transform(sourceSecondary, primaryRotation), destinationSecondary);
        double sin = double3.Dot(destinationPrimary, double3.Cross(mappedSecondary, destinationSecondary));
        double cos = Math.Clamp(double3.Dot(mappedSecondary, destinationSecondary), -1, 1);
        doubleQuat roll = doubleQuat.CreateFromAxisAngle(destinationPrimary, Math.Atan2(sin, cos));
        return doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(primaryRotation, roll));
    }

    private static doubleQuat FromTo(double3 from, double3 to)
    {
        from = Unit(from, double3.UnitX);
        to = Unit(to, double3.UnitX);
        double dot = Math.Clamp(double3.Dot(from, to), -1, 1);
        if (dot > 1 - 1e-12) return doubleQuat.Identity;
        if (dot < -1 + 1e-12)
        {
            double3 axis = Perpendicular(Math.Abs(from.X) < .8 ? double3.UnitX : double3.UnitY, from);
            return doubleQuat.CreateFromAxisAngle(axis, Math.PI);
        }
        double3 cross = double3.Cross(from, to);
        return doubleQuat.CreateFromAxisAngle(cross / cross.Length(), Math.Acos(dot));
    }

    private static double3 Perpendicular(double3 candidate, double3 normal)
    {
        double3 result = candidate - normal * double3.Dot(candidate, normal);
        return Unit(result, double3.Zero);
    }

    private static double3 Unit(double3 value, double3 fallback)
    {
        double length = value.Length();
        return length > 1e-12 && Finite(value) ? value / length : fallback;
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
