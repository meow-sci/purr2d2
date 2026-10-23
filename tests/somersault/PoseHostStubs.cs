using Brutal.Numerics;
using RenderCore;
using RenderCore.Animation;

// Managed boundaries only. The actual pose controllers, ordering and Numerics run below;
// the fixture reproduces AnimatedRenderable's local-once/world-and-publication-per-view order.
namespace KSA
{
    internal static class Program
    {
        public static bool EditorFlag = false;
        public static ulong FrameNumber = 0;
        public static IViewport MainViewport = new Viewport(ViewportType.Main);
        public static IViewport RenderedViewport = MainViewport;
    }
    public enum LocomotionMode { Grounded, Airborne, Mmu, Ladder }
    public sealed class LocomotionState { public LocomotionMode Mode = LocomotionMode.Grounded; }
    public sealed class KittenEva
    {
        public bool IsDisposed;
        public readonly KittenRenderable Renderable = new();
        public readonly LocomotionState LocomotionState = new();
        public float3 CenterOfMassAsmbF = new(0, 0, -.35f);
        public doubleQuat Body2Cce => doubleQuat.Identity;
        public double3 GetPositionEcl() => double3.Zero;
    }
    public sealed class KittenRenderable
    {
        private readonly CharacterAvatar _characterAvatar = new();
        public CharacterAvatar Avatar => _characterAvatar;
    }
    public sealed class CharacterAvatar
    {
        public readonly CharacterCore Core = new();
        public readonly CharacterAttachments Attachments = new();
        public sealed class CharacterCore
        {
            public readonly CharacterModel CharacterModel = new();
            public float Scale = .01f;
        }
        public sealed class CharacterAttachments { public readonly MmuAttachment Mmu = new(); }
        public sealed class MmuAttachment { public bool Enabled = true; }
    }
    public sealed class CharacterModel
    {
        public Skeleton Skeleton = Skeleton.KnownRig();
        public readonly List<IAnimProcessor> AnimProcessors = [];
        public TransformTRS[] Sample()
        {
            TransformTRS[] local = Skeleton.DefaultLocalPose!.AsSpan().ToArray().Select(TransformTRS.FromMatrix).ToArray();
            foreach (IAnimProcessor processor in AnimProcessors)
                if (processor.ShouldUpdate) processor.UpdateLocalPose(float4x4.Identity, Skeleton, local, .016f);
            Rebuild(local);
            return local;
        }
        public void Rebuild(TransformTRS[] local)
        {
            int[] parents = Skeleton.NodeInfo.ToArray().Select(n => n.ParentIndex).ToArray();
            float4x4[] matrices = local.Select(p => p.CreateMatrix()).ToArray();
            if (!Purr2D2.Pose.SomersaultPoseMath.TryBuildWorld(parents, matrices, Skeleton.WorldTransforms))
                throw new InvalidOperationException("Invalid fixture skeleton.");
        }
        public void Publish()
        {
            foreach (IAnimProcessor processor in AnimProcessors)
                if (processor.ShouldUpdate) processor.UpdateSkeleton(float4x4.Identity, Skeleton, .016f);
        }
    }
    public interface IAnimProcessor
    {
        float Priority { get; }
        bool ShouldUpdate { get; set; }
        bool CanCacheAnimation { get; }
        void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt);
        void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt);
    }
    public enum ViewportType { Main, Secondary, CharacterPortrait }
    public interface IViewport { ViewportType Type { get; } Camera GetCamera(); }
    public sealed class Viewport(ViewportType type) : IViewport
    {
        public ViewportType Type => type;
        public Camera GetCamera() => new();
    }
    public sealed class Camera { public double3 PositionEcl => double3.Zero; }
}

namespace RenderCore
{
    public struct TransformNodeInfo { public int ParentIndex; }
    // Same TRS math as the inspected RenderCore.TransformTRS implementation.
    public struct TransformTRS
    {
        public float3 Translation;
        public floatQuat Rotation;
        public float3 Scale;
        public float4x4 CreateMatrix()
        {
            float4x4 result = float4x4.CreateFromQuaternion(Rotation);
            result.X *= Scale.X; result.Y *= Scale.Y; result.Z *= Scale.Z;
            result.Translation = Translation;
            return result;
        }
        public static TransformTRS FromMatrix(float4x4 value)
        {
            float4x4.Decompose(value, out float3 scale, out floatQuat rotation, out float3 translation);
            return new() { Translation = translation, Rotation = rotation, Scale = scale };
        }
        public static TransformTRS Lerp(TransformTRS a, TransformTRS b, float t) => new()
        {
            Translation = float3.Lerp(a.Translation, b.Translation, t),
            Rotation = floatQuat.Slerp(a.Rotation, b.Rotation, t), Scale = float3.Lerp(a.Scale, b.Scale, t),
        };
    }
}

namespace RenderCore.Animation
{
    public sealed class PoseList(float4x4[] values) { public Span<float4x4> AsSpan() => values; }
    public sealed class Skeleton(string[] names, int[] parents, float3[] offsets)
    {
        public List<string>? BoneNames = names.ToList();
        private readonly TransformNodeInfo[] _nodes = parents.Select(p => new TransformNodeInfo { ParentIndex = p }).ToArray();
        private readonly float4x4[] _local = offsets.Select(float4x4.CreateTranslation).ToArray();
        private readonly float4x4[] _world = new float4x4[names.Length];
        public PoseList? DefaultLocalPose = new(offsets.Select(float4x4.CreateTranslation).ToArray());
        public int BoneCount => _nodes.Length;
        public Span<TransformNodeInfo> NodeInfo => _nodes;
        public Span<float4x4> LocalTransforms => _local;
        public Span<float4x4> WorldTransforms => _world;
        public static Skeleton KnownRig() => new(
            ["Root", "Pelvis", "Spine1_M", "Chest_M", "Shoulder_R", "Elbow_R", "Wrist_R",
             "Shoulder_L", "Elbow_L", "Wrist_L", "Hip_L", "Knee_L", "Ankle_L", "Hip_R", "Knee_R", "Ankle_R", "ExtraRoot", "ExtraChild"],
            [-1, 0, 1, 2, 3, 4, 5, 3, 7, 8, 1, 10, 11, 1, 13, 14, -1, 16],
            [new(0,0,0), new(0,40,0), new(0,8,0), new(0,10,0), new(-8,0,0), new(-5,-12,0), new(0,-10,0),
             new(8,0,0), new(5,-12,0), new(0,-10,0), new(5,0,0), new(0,-17,0), new(0,-17,0),
             new(-5,0,0), new(0,-17,0), new(0,-17,0), new(4,20,0), new(0,3,0)]);
    }
}
