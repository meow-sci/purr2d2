using System;
using Brutal.Numerics;

namespace Purr2D2.Capes;

/// <summary>One cached local-space pose for a cape surface node.</summary>
internal readonly struct CapeNodePose
{
    internal CapeNodePose(float3 translation, floatQuat rotation)
    {
        Translation = translation;
        Rotation = rotation;
    }

    internal float3 Translation { get; }
    internal floatQuat Rotation { get; }
}

/// <summary>
/// Immutable authored cape rest positions shared by the simulation and pose builders.
/// The trigonometric rest shape is evaluated once, on first use.
/// </summary>
internal static class CapeRestPose
{
    internal const float BackCollisionZ = -.22f;

    private static readonly float3[] Positions = BuildPositions();
    private static readonly float[] BackCollisionLimits = BuildBackCollisionLimits();

    internal static ReadOnlySpan<float3> All => Positions;

    internal static float3 Position(int index) => Positions[index];
    internal static float BackCollisionLimitZ(int index) => BackCollisionLimits[index];

    private static float3[] BuildPositions()
    {
        var positions = new float3[CapeGeometry.NodeCount];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = CapeGeometry.RestPosition(i);
        return positions;
    }

    private static float[] BuildBackCollisionLimits()
    {
        var limits = new float[Positions.Length];
        for (int i = 0; i < limits.Length; i++)
        {
            // Preserve authored shoulder wrap while keeping the torso on the
            // existing conservative rear plane.
            limits[i] = MathF.Max(BackCollisionZ, Positions[i].Z);
        }
        return limits;
    }
}

/// <summary>
/// Builds skinned local poses from cloth positions and retains them until the cloth
/// publishes a new revision. Skeleton bone mapping stays with the render processor.
/// </summary>
internal sealed class CapePoseCache
{
    private static readonly float4x4[] InverseRestFrames = BuildInverseRestFrames();

    private readonly CapeNodePose[] _poses = new CapeNodePose[CapeGeometry.NodeCount];
    private ulong _revision;
    private bool _hasPose;

    internal ReadOnlySpan<CapeNodePose> Poses => _poses;

    /// <returns>True when the node poses were recalculated for this revision.</returns>
    internal bool Update(ReadOnlySpan<float3> positions, ulong revision)
    {
        if (positions.Length != CapeGeometry.NodeCount)
            throw new ArgumentException("Cape pose input must contain all cloth nodes.", nameof(positions));
        if (_hasPose && _revision == revision) return false;

        Compute(positions, _poses);
        _revision = revision;
        _hasPose = true;
        return true;
    }

    private static void Compute(ReadOnlySpan<float3> positions, Span<CapeNodePose> poses)
    {
        for (int node = 0; node < CapeGeometry.NodeCount; node++)
        {
            float4x4 currentFrame = SurfaceFrame(positions, node);
            float4x4 delta = InverseRestFrames[node] * currentFrame;
            poses[node] = new CapeNodePose(positions[node], floatQuat.CreateFromRotationMatrix(delta));
        }
    }

    private static float4x4[] BuildInverseRestFrames()
    {
        var inverseFrames = new float4x4[CapeGeometry.NodeCount];
        ReadOnlySpan<float3> rest = CapeRestPose.All;
        for (int node = 0; node < inverseFrames.Length; node++)
        {
            float4x4 frame = SurfaceFrame(rest, node);
            if (!float4x4.Invert(frame, out inverseFrames[node]))
                throw new InvalidOperationException("The cape bind surface contains a degenerate node frame.");
        }
        return inverseFrames;
    }

    private static float4x4 SurfaceFrame(ReadOnlySpan<float3> positions, int index)
    {
        int row = index / CapeGeometry.Columns;
        int column = index % CapeGeometry.Columns;
        int left = Math.Max(column - 1, 0);
        int right = Math.Min(column + 1, CapeGeometry.Columns - 1);
        int above = Math.Max(row - 1, 0);
        int below = Math.Min(row + 1, CapeGeometry.Rows - 1);
        float3 across = positions[row * CapeGeometry.Columns + right] - positions[row * CapeGeometry.Columns + left];
        float3 upward = positions[above * CapeGeometry.Columns + column] - positions[below * CapeGeometry.Columns + column];
        float3 x = Normalize(across, float3.UnitX);
        float3 y = upward - x * float3.Dot(upward, x);
        y = Normalize(y, float3.UnitY);
        float3 z = Normalize(float3.Cross(x, y), float3.UnitZ);
        y = Normalize(float3.Cross(z, x), y);
        return new float4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private static float3 Normalize(float3 value, float3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-10f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}
