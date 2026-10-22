using System.Reflection;
using Brutal.Numerics;
using KSA;
using RenderCore;
using RenderCore.Animation;

namespace Purr2D2.Pose;

/// <summary>
/// Owns one EVA kitten's arm overlay. Bind, Update and Unbind must run on the main
/// thread outside rendering; Draw runs during skeleton evaluation for each view.
/// </summary>
public sealed class SaberPoseController : IDisposable
{
    private static readonly FieldInfo? AvatarField = typeof(KittenRenderable).GetField(
        "_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);

    private CharacterAvatar? _avatar;
    private PoseProcessor? _processor;
    private bool _disposed;
    private bool _equipped;
    private bool _mmuWasEnabled;
    private bool _changedMmu;
    private bool _hasBodyFrame;
    private double4x4 _emitterToBody;
    private float _swingTime = SwingDuration;
    private string? _failure;
    private const float SwingDuration = 0.62f;

    public SaberPoseController(Action<KittenEva, IViewport, float4x4>? draw = null)
    {
        if (draw is not null) Draw += draw;
    }

    /// <summary>Emitter-local metres to the viewport's camera-relative frame.</summary>
    public event Action<KittenEva, IViewport, float4x4>? Draw;

    public KittenEva? Kitten { get; private set; }
    public float Blend { get; private set; }
    public bool Active => Eligible && Blend > 0.001f;
    public bool Eligible => Kitten is { IsDisposed: false } kitten && _processor is { Resolved: true }
        && Supported(kitten.LocomotionState.Mode) && _failure is null;
    /// <summary>
    /// Advances only after a valid gameplay-main-view pose is published. Unlike
    /// Program.FrameNumber it distinguishes successful poses across skipped renders.
    /// Kept across unbind so consumers cannot confuse a new owner with an old generation.
    /// </summary>
    public ulong MainPoseGeneration { get; private set; }
    public ulong FrameNumber { get; private set; } = ulong.MaxValue;
    public ulong FreshnessFrames => SaberPoseMath.AgeFrames(_hasBodyFrame, FrameNumber, Program.FrameNumber);
    public bool HasFreshBodyFrame => Active && !Program.EditorFlag
        && ReferenceEquals(Kitten, Program.ControlledVehicle) && FreshnessFrames <= 1;
    public string Status => _failure ?? (Kitten is null ? "Control an EVA kitten to equip the saber."
        : !Supported(Kitten.LocomotionState.Mode) ? $"Saber stowed while {Kitten.LocomotionState.Mode.ToString().ToLowerInvariant()}."
        : Active ? "Saber held in the right paw." : "Saber stowed.");

    /// <summary>Stock MMU hand controllers cover the paw. Restore their previous state on release.</summary>
    public bool HideMmuWhenEquipped { get; set; } = true;

    /// <summary>Chest-relative target in stock skeleton centimetres; +X is the kitten's left.</summary>
    public float3 GripOffsetFromChestCm { get; set; } = new(-13f, -8f, 18f);

    /// <summary>The neutral blade direction in model space, normalized when used.</summary>
    public float3 BladeDirectionModel { get; set; } = new(0.05f, 0.80f, 0.60f);

    /// <summary>Additional local +Y finger curl, where finger bones can be discovered.</summary>
    public float FingerCurlRadians { get; set; } = 0.12f;

    public bool Bind(KittenEva kitten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(Kitten, kitten)) return _processor is { Resolved: true };
        Unbind();
        Kitten = kitten;
        try
        {
            if (kitten.IsDisposed) return Fail("The selected kitten is no longer available.");
            if (AvatarField?.GetValue(kitten.Renderable) is not CharacterAvatar avatar)
                return Fail("This KSA build does not expose the expected kitten avatar field.");
            _avatar = avatar;
            _processor = new PoseProcessor(this, avatar.Core.CharacterModel.Skeleton);
            if (!_processor.Resolved)
                return Fail("The kitten rig is missing Shoulder_R, Elbow_R or Wrist_R.");
            _mmuWasEnabled = avatar.Attachments.Mmu.Enabled;
            avatar.Core.CharacterModel.AnimProcessors.Add(_processor);
            return true;
        }
        catch (Exception error)
        {
            return Fail($"The saber holding pose could not attach: {error.Message}");
        }
    }

    /// <summary>Advance equip and swing on player time; no simulation state is modified.</summary>
    public void Update(double dtPlayer, bool equipped)
    {
        if (_disposed) return;
        if (Kitten is { IsDisposed: true })
        {
            Unbind();
            return;
        }
        _equipped = equipped;
        float dt = double.IsFinite(dtPlayer) ? (float)Math.Clamp(dtPlayer, 0.0, 0.1) : 0f;
        float target = equipped && Eligible ? 1f : 0f;
        Blend = MoveTowards(Blend, target, dt / 0.18f);
        _swingTime = MathF.Min(SwingDuration, _swingTime + dt);
        if (!equipped || !Eligible) _hasBodyFrame = false;
        if (_avatar is not null)
        {
            bool hide = HideMmuWhenEquipped && equipped && Eligible;
            if (hide && !_changedMmu && _avatar.Attachments.Mmu.Enabled)
            {
                _mmuWasEnabled = true;
                _avatar.Attachments.Mmu.Enabled = false;
                _changedMmu = true;
            }
            else if (!hide) RestoreMmu();
        }
    }

    public void Swing()
    {
        if (Active && _equipped && _swingTime >= SwingDuration * 0.75f) _swingTime = 0f;
    }

    /// <summary>
    /// A fresh emitter-local metres to body-COM-relative metres matrix. Rejects
    /// offscreen/stale poses; callers must transform it with this kitten's current body pose.
    /// </summary>
    public bool TryGetBodyFrame(out double4x4 emitterToBody)
    {
        emitterToBody = _emitterToBody;
        return HasFreshBodyFrame;
    }

    public bool TryGetBladeSegment(double lengthMeters, out double3 baseBody, out double3 tipBody)
    {
        baseBody = tipBody = double3.Zero;
        if (!double.IsFinite(lengthMeters) || lengthMeters < 0 || !TryGetBodyFrame(out double4x4 frame)) return false;
        return SaberPoseMath.TryBladeSegment(frame, lengthMeters, out baseBody, out tipBody);
    }

    /// <summary>Reproject the latest body-local pose for early light submission.</summary>
    public bool TryGetEmitterToEgo(IViewport viewport, out float4x4 emitterToEgo)
    {
        emitterToEgo = float4x4.Identity;
        if (!GameplayView(viewport) || Kitten is not { } kitten || !TryGetBodyFrame(out double4x4 body)) return false;
        double4x4 bodyToEgo = double4x4.CreateFromQuaternion(kitten.Body2Cce)
            * double4x4.CreateTranslation(kitten.GetPositionEcl() - viewport.GetCamera().PositionEcl);
        emitterToEgo = float4x4.Pack(body * bodyToEgo);
        return true;
    }

    public void Unbind()
    {
        // This list belongs to the render thread/main thread, never a solver worker.
        if (_avatar is not null && _processor is not null)
            _avatar.Core.CharacterModel.AnimProcessors.Remove(_processor);
        RestoreMmu();
        Kitten = null;
        _avatar = null;
        _processor = null;
        _equipped = false;
        Blend = 0f;
        _swingTime = SwingDuration;
        _hasBodyFrame = false;
        _failure = null;
        FrameNumber = ulong.MaxValue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unbind();
        Draw = null;
        _disposed = true;
    }

    private bool Fail(string reason)
    {
        _failure = reason;
        _hasBodyFrame = false;
        return false;
    }

    private void RestoreMmu()
    {
        if (_changedMmu && _avatar is not null)
            _avatar.Attachments.Mmu.Enabled = _mmuWasEnabled;
        _changedMmu = false;
    }

    private static bool Supported(LocomotionMode mode) => mode is LocomotionMode.Grounded
        or LocomotionMode.Airborne or LocomotionMode.Mmu;

    internal bool Swinging => _swingTime < SwingDuration;
    private float SwingAngle => _swingTime >= SwingDuration ? 0f
        : MathF.Sin((_swingTime / SwingDuration) * MathF.PI * 2f) * 1.05f;

    private float3 Direction => Unit(float3.Transform(BladeDirectionModel,
        floatQuat.CreateFromAxisAngle(float3.UnitY, SwingAngle)), float3.UnitY);

    private void Publish(float4x4 emitterInModelCm, float4x4 modelToEgo)
    {
        if (!Active || Kitten is not { } kitten || _avatar is null) return;
        IViewport viewport = Program.RenderedViewport;
        if (!GameplayView(viewport) || !ReferenceEquals(kitten, Program.ControlledVehicle)) return;
        // Model centimetres to emitter metres. The translation is already in model cm.
        float4x4 metresToModel = float4x4.CreateScale(100f) * emitterInModelCm;
        // Secondary cameras can render the saber, but only the gameplay main view
        // may refresh the pose used for audio and destructive contact.
        if (ReferenceEquals(viewport, Program.MainViewport) && viewport.Type == ViewportType.Main)
        {
            _emitterToBody = double4x4.Unpack(metresToModel)
                * SaberPoseMath.ModelToBody(_avatar.Core.Scale, kitten.CenterOfMassAsmbF);
            FrameNumber = Program.FrameNumber;
            _hasBodyFrame = Finite(_emitterToBody.Translation) && Finite(_emitterToBody.Z.XYZ);
            if (_hasBodyFrame) MainPoseGeneration++;
        }
        Draw?.Invoke(kitten, viewport, metresToModel * modelToEgo);
    }

    private static bool GameplayView(IViewport viewport) => !Program.EditorFlag
        && viewport.Type is ViewportType.Main or ViewportType.Secondary;

    private static float MoveTowards(float from, float to, float amount) => from < to
        ? MathF.Min(to, from + amount) : MathF.Max(to, from - amount);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static float3 Unit(float3 vector, float3 fallback) => SaberPoseMath.Unit(vector, fallback);

    private sealed class PoseProcessor : IAnimProcessor
    {
        private readonly SaberPoseController _owner;
        private readonly int _shoulder;
        private readonly int _elbow;
        private readonly int _wrist;
        private readonly int _chest;
        private readonly int _index;
        private readonly int _pinky;
        private readonly int[] _fingerRoots;
        private readonly float4x4[] _world;
        private readonly byte[] _visit;

        public PoseProcessor(SaberPoseController owner, Skeleton skeleton)
        {
            _owner = owner;
            _shoulder = Find(skeleton, "Shoulder_R");
            _elbow = Find(skeleton, "Elbow_R");
            _wrist = Find(skeleton, "Wrist_R");
            _chest = Find(skeleton, "Chest_M");
            if (_chest < 0) _chest = Find(skeleton, "Spine1_M");
            _index = FindFinger(skeleton, _wrist, "index");
            _pinky = FindFinger(skeleton, _wrist, "pinky", "little");
            int middle = FindFinger(skeleton, _wrist, "middle");
            int ring = FindFinger(skeleton, _wrist, "ring");
            _fingerRoots = new[] { _index, middle, ring, _pinky }.Where(i => i >= 0).Distinct().ToArray();
            _world = new float4x4[skeleton.BoneCount];
            _visit = new byte[skeleton.BoneCount];
        }

        public bool Resolved => _shoulder >= 0 && _elbow >= 0 && _wrist >= 0;
        public float Priority => 5f;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => true;

        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
            if (!Resolved || _owner.Blend <= 0f || !_owner.Eligible) return;
            try
            {
                if (localPose.Length != _world.Length || skeleton.NodeInfo.Length != _world.Length) return;
                World(skeleton, localPose);
                float3 shoulder = _world[_shoulder].Translation;
                float3 elbow = _world[_elbow].Translation;
                float3 wrist = _world[_wrist].Translation;
                float3 chest = _chest >= 0 ? _world[_chest].Translation : shoulder + new float3(12, -3, 0);
                float3 target = chest + _owner.GripOffsetFromChestCm;
                target.X += MathF.Sin(_owner.SwingAngle) * 7f;
                // A discovered knuckle centroid gives the actual paw centre rather than the wrist joint.
                target -= Grip(_world) - wrist;
                TransformTRS shoulderBefore = localPose[_shoulder];
                TransformTRS elbowBefore = localPose[_elbow];
                TransformTRS wristBefore = localPose[_wrist];
                SolveArm(skeleton, localPose, shoulder, elbow, wrist, target);

                if (_index >= 0 && _pinky >= 0)
                {
                    float3 fistAxis = _world[_index].Translation - _world[_pinky].Translation;
                    Turn(skeleton, localPose, _wrist, SaberPoseMath.RotationFromTo(fistAxis, _owner.Direction));
                }
                float blend = _owner.Blend;
                localPose[_shoulder] = TransformTRS.Lerp(shoulderBefore, localPose[_shoulder], blend);
                localPose[_elbow] = TransformTRS.Lerp(elbowBefore, localPose[_elbow], blend);
                localPose[_wrist] = TransformTRS.Lerp(wristBefore, localPose[_wrist], blend);
                float curl = Math.Clamp(_owner.FingerCurlRadians, 0f, 0.5f) * blend;
                foreach (int finger in _fingerRoots)
                    localPose[finger].Rotation = floatQuat.Concatenate(
                        floatQuat.CreateFromAxisAngle(float3.UnitY, curl), localPose[finger].Rotation);
            }
            catch (Exception error)
            {
                _owner.Fail($"The saber pose stopped safely: {error.Message}");
            }
        }

        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
        {
            if (!_owner.Active || skeleton.BoneCount != _world.Length) return;
            try
            {
                Span<float4x4> world = skeleton.WorldTransforms;
                float3 grip = Grip(world);
                float3 direction = _owner.Direction;
                if (_index >= 0 && _pinky >= 0)
                    direction = Unit(world[_index].Translation - world[_pinky].Translation, direction);
                float3 across = Unit(float3.Cross(float3.UnitY, direction), float3.UnitX);
                float3 up = Unit(float3.Cross(direction, across), float3.UnitY);
                float3 emitter = grip + direction * 12f;
                var emitterInModel = new float4x4(
                    across.X, across.Y, across.Z, 0,
                    up.X, up.Y, up.Z, 0,
                    direction.X, direction.Y, direction.Z, 0,
                    emitter.X, emitter.Y, emitter.Z, 1);
                _owner.Publish(emitterInModel, transform);
            }
            catch (Exception error)
            {
                _owner.Fail($"The saber hand transform stopped safely: {error.Message}");
            }
        }

        private float3 Grip(ReadOnlySpan<float4x4> world)
        {
            if (_fingerRoots.Length > 0)
            {
                float3 sum = float3.Zero;
                foreach (int i in _fingerRoots) sum += world[i].Translation;
                return sum / _fingerRoots.Length;
            }
            // Unknown hand rig: keep a conservative, configurable arm pose, attached to its real wrist.
            return world[_wrist].Translation + _owner.Direction * 3f;
        }

        private void SolveArm(Skeleton skeleton, Span<TransformTRS> local, float3 shoulder, float3 elbow, float3 wrist, float3 target)
        {
            if (!SaberPoseMath.TrySolveArm(shoulder, elbow, wrist, target, out float3 elbowTarget, out float3 clampedTarget)) return;
            Turn(skeleton, local, _shoulder, SaberPoseMath.RotationFromTo(elbow - shoulder, elbowTarget - shoulder));
            float3 elbowAfter = _world[_elbow].Translation;
            float3 wristAfter = _world[_wrist].Translation;
            Turn(skeleton, local, _elbow, SaberPoseMath.RotationFromTo(wristAfter - elbowAfter, clampedTarget - elbowAfter));
        }

        private void Turn(Skeleton skeleton, Span<TransformTRS> local, int bone, floatQuat delta)
        {
            int parent = skeleton.NodeInfo[bone].ParentIndex;
            floatQuat above = parent >= 0 ? TransformTRS.FromMatrix(_world[parent]).Rotation : floatQuat.Identity;
            floatQuat desired = floatQuat.Concatenate(floatQuat.Concatenate(local[bone].Rotation, above), delta);
            local[bone].Rotation = floatQuat.Normalize(floatQuat.Concatenate(desired, floatQuat.Inverse(above)));
            World(skeleton, local);
        }

        private void World(Skeleton skeleton, Span<TransformTRS> local)
        {
            Array.Clear(_visit);
            for (int i = 0; i < local.Length; i++) Build(i, skeleton.NodeInfo, local);
        }

        private void Build(int i, Span<TransformNodeInfo> nodes, Span<TransformTRS> local)
        {
            if (_visit[i] == 2) return;
            if (_visit[i] == 1) throw new InvalidOperationException("Cyclic kitten skeleton.");
            _visit[i] = 1;
            float4x4 matrix = local[i].CreateMatrix();
            int parent = nodes[i].ParentIndex;
            if (parent >= 0 && parent < local.Length)
            {
                Build(parent, nodes, local);
                matrix *= _world[parent];
            }
            _world[i] = matrix;
            _visit[i] = 2;
        }

        private static int Find(Skeleton skeleton, string name)
        {
            if (skeleton.BoneNames is not { } names) return -1;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int FindFinger(Skeleton skeleton, int wrist, params string[] fragments)
        {
            if (wrist < 0 || skeleton.BoneNames is not { } names) return -1;
            int best = -1, bestDepth = int.MaxValue;
            for (int i = 0; i < names.Count; i++)
            {
                if (!fragments.Any(f => names[i].Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                int parent = i, depth = 0;
                while (parent >= 0 && parent < skeleton.BoneCount && parent != wrist && depth <= skeleton.BoneCount)
                {
                    parent = skeleton.NodeInfo[parent].ParentIndex;
                    depth++;
                }
                if (parent == wrist && depth > 0 && depth < bestDepth) { best = i; bestDepth = depth; }
            }
            return best;
        }

    }
}
