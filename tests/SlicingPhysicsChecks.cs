using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

internal static class SlicingPhysicsChecks
{
    public static void Run()
    {
        var originalFrame = double4x4.CreateFromQuaternion(doubleQuat.CreateFromAxisAngle(double3.UnitZ, .4))
            * double4x4.CreateTranslation(new double3(3, 4, 5));
        var isolationFrame = double4x4.CreateFromQuaternion(doubleQuat.CreateFromAxisAngle(double3.UnitY, -.8))
            * double4x4.CreateTranslation(new double3(-7, 2, 6));
        Check(SliceSourceFrame.SameTransform(originalFrame * isolationFrame, originalFrame * isolationFrame),
            "capture geometry can be rebased through a rotated native docking isolation");
        Check(!SliceSourceFrame.SameTransform(originalFrame * isolationFrame, originalFrame),
            "isolation validation rejects an unrebased assembly transform");
        var changed = originalFrame * isolationFrame * double4x4.CreateTranslation(new double3(.001, 0, 0));
        Check(!SliceSourceFrame.SameTransform(changed, originalFrame * isolationFrame),
            "a subsequent local geometry change invalidates the isolated capture");
        var invalidFrame = originalFrame; invalidFrame.M41 = double.NaN;
        Check(!SliceSourceFrame.SameTransform(invalidFrame, originalFrame), "nonfinite capture transforms cannot pass freshness validation");
        // Symmetric tensor rotated 45 degrees about Z has a known pair of principal moments.
        var piece = new SlicePiece(Array.Empty<SliceFace>(), 2, new(3, 4, 5), new(6, 6, 10, -2, 0, 0), true);
        Check(SlicePrincipalFrame.TryCreate(piece, out var frame, out _), "finite full fragment tensor has a principal frame");
        Near(double3.Transform(new double3(3, 4, 5), frame.SourceToFragment), double3.Zero, "principal authoring recentres the physical mass COM");
        Near(frame.PrincipalSpecificInertia, new double3(2, 4, 5), "principal specific moments include density normalization");
        var rotation = double4x4.CreateFromQuaternion(frame.FragmentToSource);
        var reconstructed = double4x4.Transpose(rotation)
            * SlicePrincipalFrame.Tensor(4, 8, 10, 0, 0, 0) * rotation;
        Near(SlicePrincipalFrame.ApplyTensor(new double3(1, 2, 3), reconstructed), new double3(2, 10, 30),
            "durable diagonal inertia and orientation reconstruct the original offdiagonal tensor");
        double3 sample = new(8, -3, 2);
        double3 local = double3.Transform(sample, frame.SourceToFragment);
        Near(double3.Transform(local, frame.FragmentToSource) + new double3(3, 4, 5), sample, "principal mesh transform roundtrips without mirroring");
        Check(!SlicePrincipalFrame.TryCreate(piece with { Inertia = new(-1, 2, 3, 0, 0, 0) }, out _, out _),
            "negative fragment inertia is rejected");

        var bodyToPhys = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .7);
        double3 oldCom = new(2, -1, 0), drainedCom = new(-.5, .3, .8), oldPosition = new(5, 8, 2), point = new(3, 4, 5);
        double3 omegaPhys = new(.1, .2, -.3), sourceVelocity = new(0, 7800, 0);
        var correction = QuietSliceMotion.DrainCorrection(oldCom, drainedCom, bodyToPhys, omegaPhys);
        var oldPointOffset = double3.Transform(point - oldCom, bodyToPhys);
        var newPointOffset = double3.Transform(point - drainedCom, bodyToPhys);
        Near(oldPosition + correction.Position + newPointOffset, oldPosition + oldPointOffset,
            "off-centre store depletion preserves every visible point's physical pose");
        Near(sourceVelocity + correction.Velocity + double3.Cross(omegaPhys, newPointOffset),
            sourceVelocity + double3.Cross(omegaPhys, oldPointOffset),
            "off-centre store depletion preserves the inherited rotating velocity field");

        QuietFragmentBody[] bodies = [new(2, new(8, 4, 2), false), new(8, new(11, 5, 3), true)];
        double3 velocity = new(5, 2, -1), oldOmega = new(.3, -.4, .6);
        var motion = QuietSliceMotion.Assign(bodies, velocity, oldOmega, double3.UnitY);
        Near(motion[0].Velocity * 2 + motion[1].Velocity * 8, velocity * 10,
            "off-centre unequal-mass fragments conserve total linear momentum");
        foreach (var boost in new[] { double3.Zero, new double3(400, -120, 30), new double3(0, 7800, 0) })
        {
            var boosted = QuietSliceMotion.Assign(bodies, velocity + boost, oldOmega, double3.UnitY);
            for (int i = 0; i < motion.Length; i++)
            {
                Near(boosted[i].Velocity - boost, motion[i].Velocity, "fragment velocity is invariant under uniform surface/orbital boosts");
                Near(boosted[i].AngularVelocity, oldOmega, "bulk velocity never becomes newly introduced fragment spin");
            }
        }
        var opening = QuietSliceMotion.Assign(bodies, double3.Zero, double3.Zero, double3.UnitY);
        Near(opening[1].Velocity - opening[0].Velocity, double3.UnitY * QuietSliceMotion.RelativeOpeningSpeed,
            "opposite sides open at the named tiny relative speed");
        double energy = opening.Select((m, i) => .5 * bodies[i].Mass * double3.Dot(m.Velocity, m.Velocity)).Sum();
        Check(energy <= .5 * 10 * Math.Pow(QuietSliceMotion.RelativeOpeningSpeed / 2, 2), "added opening kinetic energy is bounded independently of bulk speed");
        var stationary = QuietSliceMotion.Assign(bodies, new(0, 7800, 0), double3.Zero, double3.UnitY);
        Near(stationary[0].AngularVelocity, double3.Zero, "an offset geometric COM at orbital speed cannot generate spin");
        CheckThrows(() => QuietSliceMotion.Assign([new(double.NaN, double3.Zero, true), bodies[0]], velocity, oldOmega, double3.UnitY),
            "nonfinite motion inputs refuse before native assignment");
        Near(SlicePrincipalFrame.ApplyTensor(double3.UnitX, SlicePrincipalFrame.ParallelAxis(2, new double3(1, 2, 3))),
            new double3(26, -4, -6), "parallel-axis tensor includes offdiagonal products of inertia");
        Check(!SlicePrincipalFrame.TryAngularVelocity(default, velocity, out _), "singular fragment inertia cannot launch invalid bodies");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { Check(true, message); return; }
        Check(false, message);
    }
    private static void Near(double3 actual, double3 expected, string message) => Check((actual - expected).Length() < 1e-8, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Slicing physics check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
