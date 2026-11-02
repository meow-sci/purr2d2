using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;
using Purr2D2.Runtime;
using RenderCore;
using RenderCore.Animation;
using RenderCore.Systems;

namespace Purr2D2.Capes;

/// <summary>Owns one opted-in kitten's skinned cape and local cloth state.</summary>
internal sealed class CapeController : IDisposable
{
    private const string CapeAssetId = "Purr2D2_Cape_Glb";
    private const string LightMaterialId = "Purr2D2_Cloth_Light";
    private const string DarkMaterialId = "Purr2D2_Cloth_Dark";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const float IdleReleaseSeconds = CapeUpdatePolicy.ReleaseAfterIdle;

    private static readonly FieldInfo? AvatarField = typeof(KittenRenderable).GetField("_characterAvatar", PrivateInstance);

    private readonly CharacterAvatar _avatar;
    private readonly CapeCloth _cloth;
    private readonly int _chestBone;
    private AnimatedRenderable _cape = null!;
    private SaberType _type;
    private float _stepAccumulator;
    private float _idleSeconds;
    private bool _unsupportedSpeed;
    private bool _idleReleased;
    private bool _simulationSuspended;
    private bool _disposed;
    private float4x4 _bodyToCape = float4x4.Identity;
    private readonly CapeScaleState _scaleState = new();
    private bool _hasBodyToCape;

    internal static string? HookFailure { get; private set; }
    internal KittenEva Owner { get; }
    internal string? Failure => _cloth.Failure;
    internal ulong LastMainDrawFrame { get; private set; } = ulong.MaxValue;
    internal bool IsSimulationCandidate { get; set; }
    internal bool IsStepDue => CapeUpdatePolicy.IsDue(_stepAccumulator);

    internal static void Install(Harmony harmony)
    {
        try
        {
            MethodInfo target = AccessTools.Method(typeof(KittenRenderable), nameof(KittenRenderable.UpdateRenderData))
                ?? throw new MissingMethodException(typeof(KittenRenderable).FullName, nameof(KittenRenderable.UpdateRenderData));
            MethodInfo postfix = AccessTools.Method(typeof(CapeController), nameof(RenderPostfix))
                ?? throw new MissingMethodException(typeof(CapeController).FullName, nameof(RenderPostfix));
            if (Harmony.GetPatchInfo(target)?.Postfixes.Any(patch =>
                    patch.owner == harmony.Id && patch.PatchMethod == postfix) == true)
            {
                HookFailure = null;
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            HookFailure = null;
        }
        catch (Exception error)
        {
            HookFailure = "Cape rendering is unavailable on this KSA build.";
            SaberRuntime.Report("Cape render hook could not be installed", error);
        }
    }

    private static void RenderPostfix(KittenRenderable __instance, IViewport viewport, float4x4 transform)
    {
        try
        {
            SaberRuntime? runtime = GameHooks.Runtime;
            if (runtime is null || !runtime.HasCapeRenderers) return;
            runtime.DrawCape(__instance, viewport, transform);
        }
        catch (Exception error)
        {
            try { SaberRuntime.Report("Cape render hook failed", error); }
            catch { /* Cosmetic rendering must not interrupt the game render. */ }
        }
    }

    internal CapeController(KittenEva owner, SaberType type)
    {
        Owner = owner;
        if (owner.IsDisposed) throw new ObjectDisposedException(owner.Id);
        if (AvatarField?.GetValue(owner.Renderable) is not CharacterAvatar avatar)
            throw new InvalidOperationException("This KSA build does not expose the expected kitten avatar field.");
        _avatar = avatar;
        Skeleton kittenSkeleton = avatar.Core.CharacterModel.Skeleton;
        _chestBone = FindBone(kittenSkeleton, "Chest_M");
        if (_chestBone < 0)
            throw new InvalidOperationException("The kitten rig is missing Chest_M.");

        _cloth = new CapeCloth();
        try { RebuildRenderables(type == SaberType.Dark ? SaberType.Dark : SaberType.Light); }
        catch
        {
            _cape?.Dispose();
            _cloth.Dispose();
            throw;
        }
    }

    internal void SetType(SaberType type)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        type = type == SaberType.Dark ? SaberType.Dark : SaberType.Light;
        if (_type == type) return;
        RebuildRenderables(type);
    }

    private void RebuildRenderables(SaberType type)
    {
        SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
        string materialId = type == SaberType.Dark ? DarkMaterialId : LightMaterialId;
        AnimatedRenderable? nextCape = null;
        try
        {
            nextCape = CreateRenderable(system, CapeAssetId, materialId);
            var pose = new CapePoseProcessor(_cloth, nextCape.Skeleton);
            nextCape.AnimProcessors.Add(pose);
            nextCape.FreezeAnimation = true;
            nextCape.SkinningPoseIsViewportInvariant = true;
        }
        catch
        {
            nextCape?.Dispose();
            throw;
        }

        _cape?.Dispose();
        _cape = nextCape;
        _type = type;
    }

    private static AnimatedRenderable CreateRenderable(SuperMeshRenderSystem system, string assetId, string materialId)
    {
        GltfPbrAssetRef asset = system.GltfSystemSkinned.GetOrLoad(assetId);
        if (asset.Materials.Length == 0)
            throw new InvalidOperationException($"Cape asset '{assetId}' has no materials to override.");

        GpuObjectAssetRef[] materials = ArrayPool<GpuObjectAssetRef>.Shared.Rent(asset.Materials.Length);
        try
        {
            Array.Copy(asset.Materials, materials, asset.Materials.Length);
            GpuObjectAssetRef material = system.MaterialSystem.GetOrLoad(materialId);
            for (int i = 0; i < asset.Materials.Length; i++) materials[i] = material;
            return new AnimatedRenderable(system.MeshRendererSkinnedPbrTwoSided, asset, float4x4.Identity,
                materials, system.MeshRendererSkinnedPrePassTwoSided);
        }
        finally { ArrayPool<GpuObjectAssetRef>.Shared.Return(materials, clearArray: true); }
    }

    internal bool Draw(KittenRenderable renderable, IViewport viewport, float4x4 bodyToCamera, ulong frame)
    {
        if (_disposed || Owner.IsDisposed || viewport.Type is not (ViewportType.Main or ViewportType.Secondary)) return false;
        AnimatedRenderable character = _avatar.Core.CharacterModel;
        Skeleton skeleton = character.Skeleton;
        if (_chestBone >= skeleton.BoneCount) return false;

        bool mainView = ReferenceEquals(viewport, Program.MainViewport);
        // This final game-owned matrix includes scalar, axis and draw-only size
        // changes, even when the cape is enabled after a kitten was already resized.
        if (!KittenVisualScale.TryModelToBody(character.Transform, Owner.Body2Cce,
                viewport.GetCamera().GetPositionEgo(Owner), out var modelToBody)
            || !KittenVisualScale.TryRelativeScale(modelToBody, out var visualScale)
            || !TrySocketFrame(character, skeleton, _chestBone, modelToBody,
                out var capeFrame, out var bodyToCape))
        {
            if (mainView) _hasBodyToCape = false;
            return false;
        }
        if (mainView)
        {
            _bodyToCape = bodyToCape;
            _hasBodyToCape = true;
            if (_scaleState.Observe(visualScale))
            {
                // Native cloth ownership remains on the settled manager path.
                _stepAccumulator = 0f;
            }
        }
        _cape.Transform = capeFrame;
        ViewHandle view = Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport);
        _cape.UpdateAnimation(0d);
        _cape.Draw(view);

        if (mainView) LastMainDrawFrame = frame;
        return true;
    }

    private static bool TrySocketFrame(AnimatedRenderable character, Skeleton skeleton, int bone,
        double4x4 modelToBody, out float4x4 socketToCamera, out float4x4 bodyToSocket)
    {
        socketToCamera = bodyToSocket = float4x4.Identity;
        if (skeleton.InverseBindPose is not { } inverseBind || inverseBind.Count <= bone)
            throw new InvalidOperationException("The kitten skeleton has no inverse bind pose for the cape socket.");

        float4x4 boneWorld = skeleton.WorldTransforms[bone];
        float4x4 inverseBindRotation = KittenVisualScale.RotationOnly(inverseBind[bone]);
        float4x4 correctedBone = inverseBindRotation * boneWorld;
        if (!KittenVisualScale.TrySocketFrame(correctedBone, character.Transform,
            float3.Transform(boneWorld.Translation, character.Transform), out socketToCamera)) return false;
        double4x4 socketToBody = double4x4.CreateScale(KittenVisualScale.ModelUnitsPerMetre)
            * double4x4.Unpack(KittenVisualScale.RotationOnly(correctedBone)) * modelToBody;
        socketToBody.Translation = double3.Zero;
        if (!double4x4.Invert(socketToBody, out var inverse)) return false;
        bodyToSocket = float4x4.Pack(inverse);
        return KittenVisualScale.TryRelativeScale(double4x4.Unpack(bodyToSocket), out _);
    }

    internal void Step(float dt, float3 bodyVelocity)
    {
        if (_disposed || _cloth.Failure is not null || !_hasBodyToCape) return;
        ApplyScaleReset();
        _cloth.Step(dt, float3.TransformNormal(bodyVelocity, _bodyToCape));
    }

    internal void Accumulate(double playerDt)
    {
        ApplyScaleReset();
        CapeUpdatePolicy.Accumulate(ref _stepAccumulator, playerDt, advancing: true);
    }

    private void ApplyScaleReset()
    {
        if (_disposed || !_scaleState.ConsumeReset()) return;
        _stepAccumulator = 0f;
        if (_cloth.Failure is null) _cloth.Reset();
    }
    internal float ConsumeStepTime() => CapeUpdatePolicy.Consume(ref _stepAccumulator);
    internal void ClearAccumulatedTime() => _stepAccumulator = 0f;

    internal void MarkActive(bool supportedSpeed)
    {
        if (_disposed) return;
        _idleSeconds = 0f;
        if (supportedSpeed)
        {
            _idleReleased = false;
            _unsupportedSpeed = false;
            _simulationSuspended = false;
        }
    }

    internal void SuspendForUnsupportedSpeed()
    {
        if (_disposed) return;
        if (_unsupportedSpeed) return;
        _unsupportedSpeed = true;
        _idleReleased = true;
        _simulationSuspended = true;
        _stepAccumulator = 0f;
        _idleSeconds = 0f;
        if (_cloth.Failure is null) _cloth.Reset();
    }

    internal void SuspendSimulation()
    {
        if (_disposed) return;
        _stepAccumulator = 0f;
        _idleSeconds = 0f;
        _idleReleased = true;
        if (_simulationSuspended) return;
        _simulationSuspended = true;
        // A failed cloth remains failed until the runtime's explicit failure recovery path.
        if (_cloth.Failure is null) _cloth.Reset();
    }

    internal void AdvanceIdle(double playerDt)
    {
        if (_disposed || _idleReleased || !double.IsFinite(playerDt) || playerDt <= 0d) return;
        _idleSeconds = MathF.Min(IdleReleaseSeconds, _idleSeconds + (float)Math.Min(playerDt, .1d));
        if (_idleSeconds < IdleReleaseSeconds) return;
        _idleReleased = true;
        _simulationSuspended = true;
        _stepAccumulator = 0f;
        if (_cloth.Failure is null)
            _cloth.Reset(); // Drops the private Bepu world; the next near frame starts from a light rest pose.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stepAccumulator = 0f;
        try { _cape.Dispose(); }
        finally { _cloth.Dispose(); }
    }

    private static int FindBone(Skeleton skeleton, string name)
    {
        if (skeleton.BoneNames is not { } names) return -1;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private sealed class CapePoseProcessor : IAnimProcessor
    {
        private readonly CapeCloth _cloth;
        private readonly int _root;
        private readonly int[] _bones = new int[CapeGeometry.NodeCount];

        public float Priority => 1f;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => false;

        internal CapePoseProcessor(CapeCloth cloth, Skeleton skeleton)
        {
            _cloth = cloth;
            _root = FindBone(skeleton, "CapeRoot");
            if (_root < 0) throw new InvalidOperationException("The cape asset is missing CapeRoot.");
            if (skeleton.InverseBindPose is not { } inverseBind || inverseBind.Count != skeleton.BoneCount)
                throw new InvalidOperationException("The cape asset has no complete inverse bind pose.");
            for (int node = 0; node < CapeGeometry.NodeCount; node++)
            {
                _bones[node] = FindBone(skeleton, $"Cape_{node:000}");
                if (_bones[node] < 0) throw new InvalidOperationException($"The cape asset is missing Cape_{node:000}.");
            }
        }

        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
            if (!ShouldUpdate || skeleton.BoneCount != localPose.Length || _root >= localPose.Length) return;
            _poseCache.Update(_cloth.Positions, _cloth.PoseRevision);
            SetPose(localPose, _root, float3.Zero, floatQuat.Identity);
            ReadOnlySpan<CapeNodePose> poses = _poseCache.Poses;
            for (int node = 0; node < CapeGeometry.NodeCount; node++)
            {
                CapeNodePose pose = poses[node];
                SetPose(localPose, _bones[node], pose.Translation, pose.Rotation);
            }
        }

        private static void SetPose(Span<TransformTRS> localPose, int bone, float3 translation, floatQuat rotation)
        {
            TransformTRS pose = localPose[bone];
            pose.Translation = translation;
            pose.Rotation = rotation;
            pose.Scale = float3.One;
            localPose[bone] = pose;
        }

        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt) { }

        private readonly CapePoseCache _poseCache = new();
    }
}
