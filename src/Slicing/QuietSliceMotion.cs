using Brutal.Numerics;

namespace Purr2D2.Slicing;

internal readonly record struct QuietFragmentBody(double Mass, double3 Position, bool PositiveSide);
internal readonly record struct QuietFragmentMotion(double3 Velocity, double3 AngularVelocity);

/// <summary>Uniform geometric density changes COM/inertia. Preserve bulk linear
/// momentum and inherited rotation rather than converting that approximation into
/// spin. All newly added motion is relative and independent of orbital speed.</summary>
internal static class QuietSliceMotion
{
    internal const double RelativeOpeningSpeed = .24; // m/s between opposite sides

    internal static (double3 Position, double3 Velocity) DrainCorrection(double3 oldCom, double3 newCom,
        doubleQuat bodyToPhys, double3 angularVelocityPhys)
    {
        var shift = double3.Transform(newCom - oldCom, bodyToPhys);
        return (shift, double3.Cross(angularVelocityPhys, shift));
    }

    internal static QuietFragmentMotion[] Assign(IReadOnlyList<QuietFragmentBody> bodies,
        double3 velocity, double3 angularVelocity, double3 normal)
    {
        if (bodies.Count is < 2 or > 8 || !Finite(velocity) || !Finite(angularVelocity) || !Finite(normal))
            throw new InvalidDataException("Fragment motion contains invalid source data.");
        double mass = 0, meanSign = 0;
        // Differences keep the centroid calculation accurate far from the origin.
        var origin = bodies[0].Position;
        double3 centre = double3.Zero;
        foreach (var body in bodies)
        {
            if (!double.IsFinite(body.Mass) || body.Mass <= 0 || !Finite(body.Position))
                throw new InvalidDataException("Fragment motion contains invalid mass or position.");
            mass += body.Mass;
            centre += (body.Position - origin) * body.Mass;
            meanSign += (body.PositiveSide ? 1 : -1) * body.Mass;
        }
        if (!double.IsFinite(mass) || !Finite(centre)) throw new InvalidDataException("Fragment motion exceeds finite bounds.");
        centre /= mass;
        meanSign /= mass;
        normal = double3.NormalizeOrZero(normal);
        var result = new QuietFragmentMotion[bodies.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var body = bodies[i];
            var opening = normal * ((body.PositiveSide ? 1 : -1) - meanSign) * (RelativeOpeningSpeed / 2);
            // Recentring this rigid velocity field removes the spurious net momentum
            // caused by the geometric COM shift without changing inherited spin.
            var assigned = velocity + double3.Cross(angularVelocity, body.Position - origin - centre) + opening;
            if (!Finite(assigned) || opening.Length() > RelativeOpeningSpeed * (1 + 1e-12))
                throw new InvalidDataException("Fragment opening motion exceeds its relative-speed limit.");
            result[i] = new(assigned, angularVelocity);
        }
        return result;
    }

    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
