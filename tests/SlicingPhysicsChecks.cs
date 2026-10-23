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

        // Mass redistributes from the original COM to a geometrically correct but offset COM.
        // Verify both P and L about the FIXED old source COM, not merely fragment spin.
        double mass = 10;
        double3 velocity = new(5, 2, -1), oldOmega = new(.3, -.4, .6), offset = new(.4, -.2, .1);
        var sourceInertia = SlicePrincipalFrame.Tensor(8, 9, 10, .4, -.2, .1);
        double3 oldAngular = SlicePrincipalFrame.ApplyTensor(oldOmega, sourceInertia);
        double3 momentum = velocity * mass;
        var groupInertia = SlicePrincipalFrame.Tensor(12, 14, 18, -.3, .1, -.5);
        double3 wantedSpin = oldAngular - double3.Cross(offset, momentum);
        Check(SlicePrincipalFrame.TryAngularVelocity(groupInertia, wantedSpin, out var omega), "fragment angular velocity solves from the full inertia tensor");
        Near(SlicePrincipalFrame.ApplyTensor(omega, groupInertia) + double3.Cross(offset, momentum), oldAngular,
            "shifted geometric COM preserves original spin plus orbital angular momentum");
        double3 rA = new(-1, 0, 0), rB = new(1, 0, 0);
        double3 vA = velocity + double3.Cross(omega, rA), vB = velocity + double3.Cross(omega, rB);
        Near(vA * 5 + vB * 5, momentum, "fragment rigid velocity field conserves total linear momentum");
        Near(SlicePrincipalFrame.ApplyTensor(double3.UnitX, SlicePrincipalFrame.ParallelAxis(2, new double3(1, 2, 3))),
            new double3(26, -4, -6), "parallel-axis tensor includes offdiagonal products of inertia");
        Check(!SlicePrincipalFrame.TryAngularVelocity(default, momentum, out _), "singular fragment inertia cannot launch invalid bodies");
    }

    private static void Near(double3 actual, double3 expected, string message) => Check((actual - expected).Length() < 1e-8, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Slicing physics check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
