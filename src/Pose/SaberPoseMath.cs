using Brutal.Numerics;

namespace Purr2D2.Pose;

/// <summary>Game-free geometry used by the live arm solver and its managed checks.</summary>
internal static class SaberPoseMath
{
    internal static float3 Unit(float3 vector, float3 fallback) => vector.LengthSquared() > 1e-8f
        ? float3.Normalize(vector) : fallback;

    internal static ulong AgeFrames(bool hasFrame, ulong rendered, ulong current) => !hasFrame || current < rendered
        ? ulong.MaxValue : current - rendered;

    internal static double4x4 ModelToBody(float avatarScale, float3 centerOfMass) => double4x4.CreateScale(avatarScale)
        * double4x4.CreateRotationX(-Math.PI / 2)
        * double4x4.CreateRotationZ(-Math.PI / 2)
        * double4x4.CreateTranslation(-double3.Unpack(centerOfMass));

    internal static double3 ModelDirectionToBody(float3 direction) => double3.NormalizeOrZero(
        double3.TransformNormal(double3.Unpack(in direction), ModelToBody(1, float3.Zero)));

    internal static bool TryBladeSegment(double4x4 emitterToBody, double lengthMeters,
        out double3 baseBody, out double3 tipBody)
    {
        baseBody = tipBody = double3.Zero;
        double3 origin = emitterToBody.Translation;
        double3 axis = emitterToBody.Z.XYZ;
        double axisSquared = axis.LengthSquared();
        if (!double.IsFinite(lengthMeters) || lengthMeters < 0 || !double.IsFinite(axisSquared)
            || axisSquared < 1e-12 || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z)) return false;
        baseBody = origin;
        // Blade length is a physical metre setting, independent of avatar/hilt scale.
        tipBody = origin + axis * (lengthMeters / Math.Sqrt(axisSquared));
        return double.IsFinite(tipBody.X) && double.IsFinite(tipBody.Y) && double.IsFinite(tipBody.Z);
    }

    internal static floatQuat RotationFromTo(float3 from, float3 to)
    {
        if (from.LengthSquared() < 1e-8f || to.LengthSquared() < 1e-8f) return floatQuat.Identity;
        from = float3.Normalize(from);
        to = float3.Normalize(to);
        float dot = Math.Clamp(float3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f) return floatQuat.Identity;
        if (dot < -0.99999f)
        {
            float3 axis = float3.Cross(from, float3.UnitY);
            if (axis.LengthSquared() < 1e-6f) axis = float3.Cross(from, float3.UnitX);
            return floatQuat.CreateFromAxisAngle(float3.Normalize(axis), MathF.PI);
        }
        float3 cross = float3.Cross(from, to);
        return floatQuat.Normalize(new floatQuat(cross.X, cross.Y, cross.Z, 1f + dot));
    }

    internal static bool TrySolveArm(float3 shoulder, float3 elbow, float3 wrist, float3 target,
        out float3 elbowTarget, out float3 wristTarget)
    {
        elbowTarget = elbow;
        wristTarget = wrist;
        float upper = (elbow - shoulder).Length();
        float lower = (wrist - elbow).Length();
        if (!float.IsFinite(upper) || !float.IsFinite(lower) || upper < 0.01f || lower < 0.01f) return false;
        float3 toTarget = target - shoulder;
        float distanceToTarget = toTarget.Length();
        if (!float.IsFinite(distanceToTarget)) return false;
        float3 forward = Unit(toTarget, float3.UnitZ);
        float distance = Math.Clamp(distanceToTarget, MathF.Abs(upper - lower) + 0.01f, upper + lower - 0.01f);
        float along = (upper * upper - lower * lower + distance * distance) / (2f * distance);
        float height = MathF.Sqrt(MathF.Max(0f, upper * upper - along * along));
        float3 hint = new(-1f, -0.5f, 0f);
        hint -= forward * float3.Dot(hint, forward);
        hint = Unit(hint, Unit(float3.Cross(forward, float3.UnitY), float3.UnitX));
        float3 existingBend = elbow - shoulder;
        existingBend -= forward * float3.Dot(existingBend, forward);
        float3 bend = Unit(Unit(existingBend, hint) + hint, hint);
        elbowTarget = shoulder + forward * along + bend * height;
        wristTarget = shoulder + forward * distance;
        return true;
    }
}
