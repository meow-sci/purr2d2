using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Force;

internal static class ForceMathChecks
{
    public static void Main()
    {
        RigidVelocityAndFrames();
        ThrowAndBounds();
        GroupSpreadArithmetic();
        ActualSurfaceDistance();
        InvalidFrames();
        Console.WriteLine("Force math checks passed (rigid position/spin, inertial tangential velocity, 1–250m/s throws, bounded movement and exact surface proximity).");
    }

    private static void RigidVelocityAndFrames()
    {
        Check(ForceMath.TryFollow(new(10, 20, 30), new(1, 2, 3), doubleQuat.Identity, new(0, 0, 2),
            new(3, 0, 0), doubleQuat.Identity, new(0, 1, 0), out var state), "valid rigid state");
        Equal(state.Position, new(13, 21, 30), "group offset remains in kitten frame");
        Equal(state.Velocity, new(-1, 8, 3), "release inherits velocity plus omega cross radius");
        Equal(state.BodyRates, new(0, 0, 2), "identity source inherits angular velocity");

        double s = Math.Sqrt(.5);
        var ownerRotation = new doubleQuat(0, 0, s, s);
        var relativeRotation = new doubleQuat(s, 0, 0, s);
        Check(ForceMath.TryFollow(double3.Zero, double3.Zero, ownerRotation, new(0, 1, 0), new(2, 0, 0),
            relativeRotation, double3.Zero, out state), "rotated rigid state");
        Equal(state.Position, new(0, 2, 0), "rotated kitten axes move the group coherently");
        Equal(state.Velocity, new(0, 0, -2), "inertial point velocity includes rotated owner spin");
        Equal(double3.Transform(state.BodyRates, state.Body2Cci), new(-1, 0, 0), "source rates use source axes, preserving inertial group spin");
        var expected = doubleQuat.Concatenate(relativeRotation, ownerRotation);
        Check(Math.Abs(doubleQuat.Dot(state.Body2Cci, expected)) > 1 - 1e-12, "relative rotation stays rigid");
    }

    private static void ThrowAndBounds()
    {
        var state = new ForceRigidState(new(3, 4, 0), new(10, -2, 7), doubleQuat.Identity, double3.Zero);
        var thrown = ForceMath.ThrowVelocity(state, double3.Zero, doubleQuat.Identity);
        Equal(thrown, new(10.6, -1.2, 7), "throw adds the 1m/s default outward, retaining inherited velocity");
        Equal(ForceMath.ThrowVelocity(state, double3.Zero, doubleQuat.Identity, 200), new(130, 158, 7),
            "a chosen 200m/s throw speed remains within the expanded bounds");
        Equal(ForceMath.ThrowVelocity(state, double3.Zero, doubleQuat.Identity, double.PositiveInfinity), thrown,
            "nonfinite throw speed falls back to the safe 1m/s default");
        Equal(ForceMath.ThrowVelocity(state, double3.Zero, doubleQuat.Identity, 500), new(160, 198, 7),
            "throw speed is clamped to the 250m/s control maximum");
        state = state with { Position = double3.Zero };
        Equal(ForceMath.ThrowVelocity(state, double3.Zero, doubleQuat.Identity), new(11, -2, 7), "coincident object throws along kitten forward");
        Check(ForceMath.ValidOffset(new(15, 20, 0)), "25m diagonal is legal");
        Check(ForceMath.ValidOffset(new(25.000001, 0, 0)), "floating point widget boundary is tolerated");
        Check(!ForceMath.ValidOffset(new(25.001, 0, 0)), "real offset excess rejected");
        Check(!ForceMath.ValidOffset(new(double.NaN, 0, 0)), "nonfinite offset rejected");
    }

    private static void GroupSpreadArithmetic()
    {
        Check(ForceMath.ScaleGroupSpread(1, 1) == .9,
            "one positive wheel tick compresses spacing by ten percent");
        Check(Math.Abs(ForceMath.ScaleGroupSpread(.9, -1) - 1) < 1e-12,
            "one negative tick reverses one compress tick multiplicatively");
        Check(ForceMath.ScaleGroupSpread(1, 1000) == ForceMath.MinimumGroupSpread
            && ForceMath.ScaleGroupSpread(1, -1000) == ForceSpreadLimits.Maximum
            && double.IsFinite(ForceMath.ScaleGroupSpread(double.PositiveInfinity, -1000)),
            "extreme wheel input remains finite and bounded to the positive .05 to 100 range");
        Check(Math.Abs(ForceMath.ScaleGroupSpread(3.9, -.5) - 3.9 * Math.Pow(.9, -.5)) < 1e-12
            && ForceMath.ScaleGroupSpread(3.9, -.5) > 4,
            "fractional wheel input smoothly crosses the former 4x ceiling");
        Check(ForceMath.ScaleGroupSpread(1, -100, 20) == 20
            && ForceMath.ScaleGroupSpread(1, -100, 80) == 80
            && ForceMath.ClampGroupSpread(200, 80) == 80,
            "an explicit 20x or 80x preference caps expansion independently of the 100x safety limit");
        Check(ForceMath.ClampGroupSpread(50, 0) == 1
            && ForceMath.ClampGroupSpread(50, double.NaN) == ForceSpreadLimits.DefaultMaximum,
            "requested maxima normalize to 1x or the finite 20x fallback");
        Check(ForceMath.ScaleGroupSpread(2, double.NaN) == 2,
            "invalid wheel input cannot alter the current group spread");
        Check(ForceMath.TryFollow(new(10, 0, 0), double3.Zero, doubleQuat.Identity, double3.Zero,
            new(4, 0, 0), new(-2, 0, 0), doubleQuat.Identity, new(0, 1, 0), .5, out var state),
            "a bounded group scale produces a rigid follow state");
        Equal(state.Position, new(13, 1, 0), "spread scales centered offsets while preserving centroid and translation");
        Check(ForceMath.TryFollow(new(10, 0, 0), double3.Zero, doubleQuat.Identity, double3.Zero,
            new(4, 0, 0), new(1, 0, 0), doubleQuat.Identity, new(0, 3, 0), 80, out var wideState),
            "an 80x group scale produces a valid rigid follow state");
        Check(ForceMath.TryFollow(new(10, 0, 0), double3.Zero, doubleQuat.Identity, double3.Zero,
            new(4, 0, 0), new(-1, 0, 0), doubleQuat.Identity, new(0, 3, 0), 80, out var otherWideState),
            "opposite offset at an 80x group scale remains valid");
        Equal((wideState.Position + otherWideState.Position) / 2, new(14, 3, 0),
            "wide spreads preserve the shared centroid while moving members farther apart");
        Equal(wideState.Position, new(94, 3, 0), "80x spread changes only the centered object offset");
        Check(Math.Abs(doubleQuat.Dot(wideState.Body2Cci, doubleQuat.Identity)) > 1 - 1e-12,
            "wide spread preserves object orientation");
        Check(!ForceMath.TryFollow(double3.Zero, double3.Zero, doubleQuat.Identity, double3.Zero,
            new(4, 0, 0), new(-2, 0, 0), doubleQuat.Identity, double3.Zero,
            ForceMath.MinimumGroupSpread / 2, out _), "out-of-range spread cannot corrupt a held object's state");
    }

    private static void ActualSurfaceDistance()
    {
        // Nonuniformly scaled/rotated authored geometry is transformed before distance testing.
        var a = new double3(10, -3, 1); var b = new double3(10, 5, 1); var c = new double3(10, -3, 7);
        double inside = ForceMath.SurfaceDistanceSquared(new(11.999, -1, 2), a, b, c);
        double outside = ForceMath.SurfaceDistanceSquared(new(12.001, -1, 2), a, b, c);
        Check(inside < 4 && outside > 4, "two-metre radius uses actual transformed surface");
        Check(ForceMath.SurfaceDistanceSquared(new(10, 20, 1), a, b, c) > 4, "large object bounds alone never authorize a capture");
        Check(double.IsPositiveInfinity(ForceMath.SurfaceDistanceSquared(double3.Zero, a, a, a)), "degenerate geometry cannot authorize a capture");
        Check(double.IsPositiveInfinity(ForceMath.SurfaceDistanceSquared(new(double.NaN, 0, 0), a, b, c)), "nonfinite geometry rejected");
    }

    private static void InvalidFrames()
    {
        Check(!ForceMath.TryFollow(double3.Zero, double3.Zero, default, double3.Zero, double3.UnitX,
            doubleQuat.Identity, double3.Zero, out _), "zero owner quaternion rejected");
        Check(!ForceMath.TryFollow(double3.Zero, new(double.PositiveInfinity, 0, 0), doubleQuat.Identity,
            double3.Zero, double3.UnitX, doubleQuat.Identity, double3.Zero, out _), "nonfinite motion rejected");
    }

    private static void Equal(double3 actual, double3 expected, string message) =>
        Check((actual - expected).Length() < 1e-9, message + $"; actual {actual}, expected {expected}");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
