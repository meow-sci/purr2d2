using Brutal.Numerics;
using Purr2D2.Core;

internal static class SaberDropChecks
{
    internal static void Run()
    {
        var drop = new SaberDropSequence();
        Check(drop.Held && !drop.Detached && drop.BeginDrop() && !drop.Held,
            "drop immediately reserves the hand before the reach animation");
        drop.Advance(double.NaN); drop.Advance(-1); drop.Advance(0);
        Check(drop.Time == 0 && !drop.ReadyToRelease, "invalid and paused deltas never release a collider");
        drop.Advance(100);
        Check(drop.Time == .1 && !drop.ReadyToRelease, "a delayed frame cannot skip the release animation");
        for (int i = 0; i < 3; i++) drop.Advance(.1);
        Check(drop.ReadyToRelease && !drop.Detached, "the held hilt remains attached until native release succeeds");
        drop.Released();
        Check(drop.Detached && !drop.Held && drop.BeginReturn(), "native release exposes Undrop without changing ignition");
        drop.Advance(.1);
        Check(drop.ReturnBlend > 0 && drop.ReturnBlend < 1 && !drop.ReadyToAttach,
            "recall visibly interpolates before attaching");
        for (int i = 0; i < 4; i++) drop.Advance(.1);
        Check(drop.ReadyToAttach && !drop.Held, "recall waits for host hand-frame validation before attachment");
        drop.Reset();
        Check(drop.Held && !drop.Detached && drop.Time == 0, "cleanup clears all transient sequence state");
        ReturnEaseOut();
        ReleaseSpin();
        var drift = new double3(1000,-500,20);
        Check(SaberDropMath.ReleaseVelocity(drift,double3.Zero,new(2,0,0),doubleQuat.Identity) == drift,
            "a space release inherits the full owner drift without an artificial throw");
        var rotating = SaberDropMath.ReleaseVelocity(drift,new(0,0,2),new(2,0,0),
            doubleQuat.CreateFromAxisAngle(double3.UnitZ,Math.PI/2));
        Check((rotating - new double3(996,-500,20)).Length() < 1e-10,
            "release inherits rotational point velocity in the correct native physics frame");

        var start = double4x4.CreateScale(2, 3, 4) * double4x4.CreateRotationY(Math.PI)
            * double4x4.CreateTranslation(1e12, -1e12, 4e9);
        var end = double4x4.CreateScale(4, 5, 6) * double4x4.CreateTranslation(1e12 + 10, -1e12 + 20, 4e9 + 30);
        var middle = SaberDropMath.Interpolate(start, end, .5f);
        Check(Math.Abs(middle.X.XYZ.Length() - 3) < 1e-5 && Math.Abs(middle.Y.XYZ.Length() - 4) < 1e-5
            && Math.Abs(middle.Z.XYZ.Length() - 5) < 1e-5 && Math.Abs(double3.Dot(middle.X.XYZ, middle.Z.XYZ)) < 1e-5,
            "180-degree recall keeps a rigid nonsingular orientation and interpolated nonuniform scale");
        Check((middle.Translation - new double3(1e12 + 5, -1e12 + 10, 4e9 + 15)).Length() < 1e-6,
            "recall position preserves double precision away from the world origin");
        Check(SaberDropMath.Interpolate(start, end, 0).Equals(start)
            && SaberDropMath.Interpolate(start, end, 1).Equals(end), "recall endpoints preserve exact attachment frames");
        var invalid = end; invalid[0,0] = double.NaN;
        Check(!SaberDropMath.ValidFrame(invalid) && !SaberDropMath.ValidFrame(default)
            && SaberDropMath.Interpolate(start, invalid, .5f).Equals(start),
            "invalid or singular world frames cannot enter recall rendering");
        var shearA = new double4x4(2,0,.6,0, .4,3,.9,0, 0,0,4,0, 1,2,3,1) * double4x4.CreateRotationY(Math.PI);
        var shearB = new double4x4(4,0,-.3,0, .2,5,1.1,0, 0,0,6,0, 8,9,10,1) * double4x4.CreateRotationZ(.4);
        Check(NearFrame(SaberDropMath.Interpolate(shearA,shearB,.000001f),shearA,.0001)
            && NearFrame(SaberDropMath.Interpolate(shearA,shearB,.999999f),shearB,.0001),
            "sheared return frames remain continuous at both attachment endpoints");
        Check(SaberDropMath.ValidFrame(SaberDropMath.Interpolate(shearA,shearB,.5f)),
            "rotation plus interpolated triangular shear stays nonsingular through recall");
        Check(SaberLimits.MaximumBladeLength == 50
            && SaberLimits.SliceSelectionReach(50, 4) == 200, "50-metre blade and assisted-reach bounds share the canonical limit");
    }
    private static void ReleaseSpin()
    {
        var oldRate = new double3(1.25,-2.5,.625);
        Check(SaberDropMath.ReleaseAngularVelocity(oldRate,0) == double3.Zero,
            "zero spin releases with no angular velocity in the native physics frame");
        Check(SaberDropMath.ReleaseAngularVelocity(oldRate,1) == oldRate
            && SaberDropMath.ReleaseAngularVelocity(oldRate,2) == oldRate * 2,
            "one and two times spin preserve or double the exact existing release angular vector");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -5f })
            Check(SaberDropMath.ReleaseAngularVelocity(oldRate,invalid) == double3.Zero,
                "invalid and negative drop spin recover the zero-spin default");
        Check(SaberDropMath.ValidateSpinMultiplier(3) == 2 && SaberDropMath.ValidateSpinMultiplier(.75f) == .75f,
            "finite drop spin clamps at two while retaining fractional selections");
        Check(SaberDropMath.ReleaseAngularVelocity(double3.Zero,2) == double3.Zero,
            "a stationary native release never gains an invented angular rate");
        var drop = new SaberDropSequence();
        Check(drop.SpinMultiplier == 0 && drop.BeginDrop(1.5f) && !drop.BeginDrop(0) && drop.SpinMultiplier == 1.5f,
            "each drop snapshots spin once before the reaching animation");
        drop.Advance(.1); drop.Released(); drop.BeginReturn();
        Check(drop.SpinMultiplier == 1.5f, "release and Undrop preserve the captured drop setting");
        drop.Reset(); drop.BeginDrop(float.NaN);
        Check(drop.SpinMultiplier == 0, "reset and an invalid next drop restore zero spin");
    }
    private static void ReturnEaseOut()
    {
        var recall = new SaberDropSequence();
        recall.Released(); recall.BeginReturn();
        recall.Advance(-1); recall.Advance(double.NaN); recall.Advance(double.NegativeInfinity);
        Check(recall.ReturnBlend == 0 && recall.Time == 0, "recall easing preserves its exact start and ignores invalid time");
        float previousBlend = 0, previousTravel = float.PositiveInfinity;
        for (int interval = 1; interval <= 8; interval++)
        {
            recall.Advance(SaberDropSequence.ReturnDuration / 8);
            float blend = recall.ReturnBlend;
            float travel = blend - previousBlend;
            Check(travel > 0 && travel < previousTravel,
                "each equal recall interval travels less distance as the saber eases toward the hand");
            if (interval == 4)
                Check(Math.Abs(blend - .875f) < 1e-6f,
                    "half the return duration covers seven eighths of the path with cubic ease-out");
            previousBlend = blend; previousTravel = travel;
        }
        Check(recall.ReturnBlend == 1 && recall.ReadyToAttach,
            "ease-out reaches the exact hand endpoint at the existing return duration");
        for (int i = 0; i < 20; i++) recall.Advance(.1);
        Check(recall.ReturnBlend == 1, "return progress clamps at the hand without overshoot after completion");

        recall.Released(); recall.BeginReturn();
        for (int i = 0; i < 4; i++) recall.Advance(.1);
        recall.Advance(SaberDropSequence.ReturnDuration * .99 - recall.Time);
        float nearEnd = recall.ReturnBlend;
        double finalInterval = SaberDropSequence.ReturnDuration - recall.Time;
        recall.Advance(finalInterval);
        Check(recall.ReturnBlend == 1 && (1 - nearEnd) / finalInterval < .001,
            "the final one percent of recall time approaches zero arrival speed");
        recall.Reset();
        Check(recall.ReturnBlend == 0, "reset restores the exact zero return endpoint");
    }
    private static bool NearFrame(double4x4 a, double4x4 b, double tolerance)
    {
        for (int row=0;row<4;row++)
        for (int column=0;column<4;column++)
            if (Math.Abs(a[row,column]-b[row,column]) > tolerance) return false;
        return true;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Drop check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
