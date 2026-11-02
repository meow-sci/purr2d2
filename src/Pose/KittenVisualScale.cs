using System;
using Brutal.Numerics;

namespace Purr2D2.Pose;

/// <summary>
/// Reads the final game-owned character transform rather than assuming the avatar's
/// scalar is its whole visual size. No knowledge of the code that changed it is needed.
/// </summary>
internal static class KittenVisualScale
{
    internal const float ModelUnitsPerMetre = 100f;

    /// <summary>
    /// Removes only the physical body's rigid camera-relative frame. The observed
    /// matrix retains all visual scale, axis corrections and assembly/COM offsets.
    /// Use the transform passed to the final skeleton callback, for the same viewport.
    /// </summary>
    internal static bool TryModelToBody(float4x4 observedModelToEgo, doubleQuat bodyToCce,
        double3 physicalBodyPositionEgo, out double4x4 modelToBody)
    {
        modelToBody = double4x4.Identity;
        if (!Finite(observedModelToEgo) || !Finite(physicalBodyPositionEgo)) return false;
        // Match the game's packed rotation and translation. Subtract the common
        // camera origin before multiplying so large world coordinates cancel once.
        float4x4 rotation = float4x4.CreateFromQuaternion(floatQuat.Pack(bodyToCce));
        if (!Finite(rotation) || !double4x4.Invert(double4x4.Unpack(rotation), out var inverseRotation)) return false;
        var relative = double4x4.Unpack(observedModelToEgo);
        relative.Translation -= double3.Unpack(float3.Pack(physicalBodyPositionEgo));
        var result = relative * inverseRotation;
        if (!TryRelativeScale(result, out _)) return false;
        modelToBody = result;
        return true;
    }

    /// <summary>Lengths of the model axes relative to the stock centimetre-to-metre transform.</summary>
    internal static bool TryRelativeScale(double4x4 modelToBody, out float3 relativeModelAxes)
    {
        relativeModelAxes = float3.One;
        if (!Finite(modelToBody)) return false;
        double3 x = modelToBody.X.XYZ, y = modelToBody.Y.XYZ, z = modelToBody.Z.XYZ;
        double sx = x.Length(), sy = y.Length(), sz = z.Length();
        if (!Positive(sx) || !Positive(sy) || !Positive(sz)) return false;
        // Relative determinant also catches collinear/sheared singular frames,
        // without rejecting legitimate very small or large uniform scales.
        double handedness = double3.Dot(x / sx, double3.Cross(y / sy, z / sz));
        if (!double.IsFinite(handedness) || Math.Abs(handedness) < 1e-6) return false;
        float3 result = new((float)(sx * ModelUnitsPerMetre), (float)(sy * ModelUnitsPerMetre),
            (float)(sz * ModelUnitsPerMetre));
        if (!Positive(result.X) || !Positive(result.Y) || !Positive(result.Z)) return false;
        relativeModelAxes = result;
        return true;
    }

    /// <summary>Builds a metre-authored attachment frame while preserving scale and shear.</summary>
    internal static bool TrySocketFrame(float4x4 correctedBone, float4x4 observedModelToEgo,
        float3 socketPositionEgo, out float4x4 socketFrame)
    {
        socketFrame = float4x4.Identity;
        if (!Finite(correctedBone) || !Finite(observedModelToEgo) || !Finite(socketPositionEgo)) return false;
        float4x4 rotation = RotationOnly(correctedBone);
        var result = float4x4.CreateScale(ModelUnitsPerMetre) * rotation * observedModelToEgo;
        result.Translation = socketPositionEgo;
        if (!TryRelativeScale(double4x4.Unpack(result), out _)) return false;
        socketFrame = result;
        return true;
    }

    internal static bool Changed(float3 previous, float3 current) => Changed(previous.X, current.X)
        || Changed(previous.Y, current.Y) || Changed(previous.Z, current.Z);

    internal static float4x4 RotationOnly(float4x4 matrix)
    {
        float3 x = Unit(matrix.X.XYZ, float3.UnitX);
        float3 y = Unit(matrix.Y.XYZ - x * float3.Dot(matrix.Y.XYZ, x), float3.UnitY);
        float3 z = Unit(float3.Cross(x, y), float3.UnitZ);
        y = Unit(float3.Cross(z, x), y);
        return new(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
    }

    private static bool Changed(float a, float b) => !Positive(a) || !Positive(b)
        || MathF.Abs(a - b) > MathF.Max(a, b) * .0002f;
    private static float3 Unit(float3 value, float3 fallback)
    {
        float squared = value.LengthSquared();
        return float.IsFinite(squared) && squared > 1e-20f ? value / MathF.Sqrt(squared) : fallback;
    }
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Finite(float3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static bool Finite(float4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
            if (!float.IsFinite(matrix[row, column])) return false;
        return MathF.Abs(matrix[0, 3]) <= 1e-6f && MathF.Abs(matrix[1, 3]) <= 1e-6f
            && MathF.Abs(matrix[2, 3]) <= 1e-6f && MathF.Abs(matrix[3, 3] - 1f) <= 1e-6f;
    }
    private static bool Finite(double4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
            if (!double.IsFinite(matrix[row, column])) return false;
        return Math.Abs(matrix[0, 3]) <= 1e-6 && Math.Abs(matrix[1, 3]) <= 1e-6
            && Math.Abs(matrix[2, 3]) <= 1e-6 && Math.Abs(matrix[3, 3] - 1d) <= 1e-6;
    }
}
