using Brutal.Numerics;

namespace Purr2D2.Core;

internal static class SaberDropMath
{
    internal static double ValidateReturnDuration(double seconds) => double.IsFinite(seconds)
        ? Math.Clamp(seconds, SaberDropSequence.MinimumReturnDuration, SaberDropSequence.DefaultReturnDuration)
        : SaberDropSequence.DefaultReturnDuration;

    internal static float ValidateSpinMultiplier(float multiplier) => float.IsFinite(multiplier)
        ? Math.Clamp(multiplier, 0, 2) : 0;

    // The baseline is the owner's native physics-frame angular velocity, not an
    // invented throw rate or the inertial body-rate cache (which includes planet spin).
    internal static double3 ReleaseAngularVelocity(double3 ownerAngularVelocityPhys, float multiplier) =>
        ownerAngularVelocityPhys * ValidateSpinMultiplier(multiplier);

    internal static double3 ReleaseVelocity(double3 ownerVelocityPhys, double3 ownerRatesBody,
        double3 releaseOffsetBody, doubleQuat bodyToPhys) => ownerVelocityPhys
        + double3.Transform(double3.Cross(ownerRatesBody, releaseOffsetBody), bodyToPhys);

    internal static bool ValidFrame(double4x4 frame)
    {
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            if (!double.IsFinite(frame[r,c])) return false;
        var x = frame.X.XYZ; var y = frame.Y.XYZ; var z = frame.Z.XYZ;
        double sx = x.Length(), sy = y.Length(), sz = z.Length();
        if (!(sx > 0) || !(sy > 0) || !(sz > 0) || !double.IsFinite(sx + sy + sz)) return false;
        double determinant = double3.Dot(x / sx, double3.Cross(y / sy, z / sz));
        return double.IsFinite(determinant) && Math.Abs(determinant) > 1e-6
            && Math.Abs(frame[0,3]) < 1e-6 && Math.Abs(frame[1,3]) < 1e-6
            && Math.Abs(frame[2,3]) < 1e-6 && Math.Abs(frame[3,3] - 1) < 1e-6;
    }

    internal static double4x4 Interpolate(double4x4 a, double4x4 b, float progress)
    {
        if (!ValidFrame(a)) return ValidFrame(b) ? b : double4x4.Identity;
        if (!ValidFrame(b)) return a;
        float t = float.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (t <= 0) return a;
        if (t >= 1) return b;
        // Interpolate rotations on the sphere. Linear basis interpolation collapses at 180 degrees.
        var qa = Rotation(a); var qb = Rotation(b);
        var rotation = double4x4.CreateFromQuaternion(doubleQuat.Unpack(floatQuat.Slerp(qa, qb, t)));
        var ra = double4x4.CreateFromQuaternion(doubleQuat.Unpack(qa));
        var rb = double4x4.CreateFromQuaternion(doubleQuat.Unpack(qb));
        if (!double4x4.Invert(ra, out var inverseA) || !double4x4.Invert(rb, out var inverseB)) return a;
        var linearA = a; linearA.Translation = double3.Zero;
        var linearB = b; linearB.Translation = double3.Zero;
        var stretchA = linearA * inverseA;
        var stretchB = linearB * inverseB;
        // Gram-Schmidt's rigid frames leave triangular stretch/shear with positive
        // diagonal entries for nonmirrored inputs. Interpolating this residual keeps
        // all authored affine scale and avoids an endpoint shape jump.
        var stretch = double4x4.Identity;
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
            stretch[row,column] = stretchA[row,column] + (stretchB[row,column] - stretchA[row,column]) * t;
        var position = a.Translation + (b.Translation - a.Translation) * t;
        return stretch * rotation * double4x4.CreateTranslation(position);
    }
    private static floatQuat Rotation(double4x4 frame)
    {
        var z = double3.NormalizeOrZero(frame.Z.XYZ);
        var x = double3.NormalizeOrZero(frame.X.XYZ - z * double3.Dot(frame.X.XYZ, z));
        var y = double3.NormalizeOrZero(double3.Cross(z, x));
        return floatQuat.Pack(doubleQuat.CreateFromRotationMatrix(new double4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
    }
}
