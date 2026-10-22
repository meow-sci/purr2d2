using Brutal.Numerics;
using Purr2D2.Pose;

internal static class PoseChecks
{
    public static void Run()
    {
        Near(float3.Transform(float3.UnitX, SaberPoseMath.RotationFromTo(float3.UnitX, float3.UnitY)), float3.UnitY,
            "arm quaternion follows engine row-vector convention");
        Near(float3.Transform(float3.UnitY, SaberPoseMath.RotationFromTo(float3.UnitY, -float3.UnitY)), -float3.UnitY,
            "antiparallel arm direction remains finite and turns exactly");
        Near(float3.Transform(float3.UnitX, SaberPoseMath.RotationFromTo(float3.Zero, float3.UnitY)), float3.UnitX,
            "zero-length source cannot inject an invalid quaternion");

        var shoulder = new float3(0, 0, 0);
        var elbow = new float3(-3, -4, 0);
        var wrist = new float3(-3, -4, 4);
        foreach (float3 target in new[] { new float3(1, -2, 5), new float3(0, 0, 100), shoulder, new float3(-100, -50, 0) })
        {
            Check(SaberPoseMath.TrySolveArm(shoulder, elbow, wrist, target, out var solvedElbow, out var solvedWrist),
                "valid arm solves at ordinary, unreachable, folded and collinear targets");
            Check(MathF.Abs((solvedElbow - shoulder).Length() - 5f) < 2e-4f
                && MathF.Abs((solvedWrist - solvedElbow).Length() - 4f) < 2e-4f,
                "clamped IK preserves both measured bone lengths");
            Check((solvedWrist - shoulder).Length() < 9 && (solvedWrist - shoulder).Length() > 1,
                "IK avoids fully straight and fully folded singularities");
        }
        Check(!SaberPoseMath.TrySolveArm(shoulder, shoulder, wrist, wrist, out _, out _),
            "missing arm segment disables the solve");

        // Independently expected from KSA's -90 X, then -90 Z axis convention:
        // model (0.05,0.40,0.20)m -> body (0.20,-0.05,-0.40)m, then subtract COM.
        double4x4 emitter = double4x4.CreateScale(100)
            * double4x4.CreateTranslation(5, 40, 20)
            * SaberPoseMath.ModelToBody(.01f, new float3(.1f, .2f, .3f));
        Near(emitter.Translation, new double3(.1, -.25, -.7), "model-to-body chain includes the centre-of-mass offset");
        Near(double3.Transform(new double3(0, 0, 1), emitter), new double3(1.1, -.25, -.7),
            "metre-authored blade stays one metre and points along the correct body axis");
        double4x4 largeKitten = double4x4.CreateScale(100)
            * double4x4.CreateTranslation(5, 40, 20)
            * SaberPoseMath.ModelToBody(.02f, new float3(.1f, .2f, .3f));
        Check(SaberPoseMath.TryBladeSegment(largeKitten, .85, out var largeBase, out var largeTip)
            && Math.Abs((largeTip - largeBase).Length() - .85) < 1e-12,
            "nondefault kitten scale cannot change physical blade/contact length");
        Check(!SaberPoseMath.TryBladeSegment(default, .85, out _, out _),
            "degenerate emitter direction cannot create a contact segment");

        Check(SaberPoseMath.AgeFrames(true, 10, 10) == 0 && SaberPoseMath.AgeFrames(true, 10, 11) == 1,
            "main-view pose survives the frame-end increment for the next safe simulation seam");
        Check(SaberPoseMath.AgeFrames(true, 10, 12) > 1
            && SaberPoseMath.AgeFrames(false, 0, 0) == ulong.MaxValue
            && SaberPoseMath.AgeFrames(true, 11, 10) == ulong.MaxValue,
            "missing, stale and reset-frame snapshots cannot pass the one-frame freshness gate");
    }

    private static void Near(float3 actual, float3 expected, string message) => Check((actual - expected).Length() < 2e-4f, message);
    private static void Near(double3 actual, double3 expected, string message) => Check((actual - expected).Length() < 2e-7, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
