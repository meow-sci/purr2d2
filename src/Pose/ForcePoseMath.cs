using Brutal.Numerics;

namespace Purr2D2.Pose;

/// <summary>Pure left-arm geometry in model centimetres; never changes a physical body.</summary>
internal static class ForcePoseMath
{
    internal static float3 AimModel(double3? aimBody)
    {
        if (aimBody is not { } body || !double.IsFinite(body.X) || !double.IsFinite(body.Y)
            || !double.IsFinite(body.Z)) return float3.UnitZ;
        double largest = Math.Max(Math.Abs(body.X), Math.Max(Math.Abs(body.Y), Math.Abs(body.Z)));
        if (largest < 1e-6) return float3.UnitZ;
        // Inverse of KSA's model-to-body -90 X, -90 Z rotations.
        body = double3.NormalizeOrZero(body / largest);
        return new((float)-body.Y, (float)-body.Z, (float)body.X);
    }

    internal static float3 ReachDirection(float3 aim)
    {
        if (!SomersaultPoseMath.Finite(aim)) aim = float3.UnitZ;
        // Keep the left paw outward, raised and in front even when a held object
        // moves behind the kitten; the arm never bends back across the right paw.
        return SaberPoseMath.Unit(new float3(.45f + Math.Clamp(aim.X, -.15f, .8f) * .5f,
            .2f + Math.Clamp(aim.Y, -.2f, .65f) * .6f, .75f + Math.Clamp(aim.Z, .2f, 1) * .4f), float3.UnitZ);
    }

    internal static bool TrySolve(float3 shoulder, float3 elbow, float3 wrist, float3 aim,
        out float3 elbowTarget, out float3 wristTarget)
    {
        elbowTarget = elbow; wristTarget = wrist;
        if (!SomersaultPoseMath.Finite(shoulder) || !SomersaultPoseMath.Finite(elbow)
            || !SomersaultPoseMath.Finite(wrist)) return false;
        float reach = ((elbow - shoulder).Length() + (wrist - elbow).Length()) * .88f;
        float3 target = shoulder + ReachDirection(aim) * reach;
        // The saber solver's elbow hint points right. Mirror its inputs and outputs
        // to obtain the same measured-length solve with a left/outward elbow hint.
        if (!SaberPoseMath.TrySolveArm(Mirror(shoulder), Mirror(elbow), Mirror(wrist), Mirror(target),
            out float3 reflectedElbow, out float3 reflectedWrist)) return false;
        elbowTarget = Mirror(reflectedElbow); wristTarget = Mirror(reflectedWrist);
        return true;
    }

    private static float3 Mirror(float3 point) => new(-point.X, point.Y, point.Z);
}
