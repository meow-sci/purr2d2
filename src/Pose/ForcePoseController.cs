using System.Reflection;
using Brutal.Numerics;
using KSA;
using RenderCore;
using RenderCore.Animation;

namespace Purr2D2.Pose;

/// <summary>
/// An equipment-independent left-paw overlay. Bind, Update and Unbind run on the
/// settled main thread before rendering. The callback writes only the left arm
/// and its discovered finger rotations; physics and MMU cosmetics are untouched.
/// </summary>
internal sealed class ForcePoseController : IDisposable
{
    private static readonly FieldInfo? AvatarField = typeof(KittenRenderable).GetField(
        "_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);
    private CharacterAvatar? _avatar;
    private PoseProcessor? _processor;
    private bool _disposed;
    private string? _failure;
    private float3 _aimModel = float3.UnitZ;

    public KittenEva? Kitten { get; private set; }
    public float Blend { get; private set; }
    public bool Active => Eligible && Blend > .001f;
    public bool Eligible => !_disposed && _failure is null && _processor is not null
        && Kitten is { IsDisposed: false } kitten && kitten.LocomotionState.Mode is
            LocomotionMode.Grounded or LocomotionMode.Airborne or LocomotionMode.Mmu
            or LocomotionMode.Tumbling or LocomotionMode.Rightening;
    public string Status => _failure ?? (Kitten is null ? "Control an EVA kitten to use the Force."
        : !Eligible ? "The Force hand pose is unavailable during this movement."
        : Active ? "Force held in the left paw." : "Force hand at rest.");

    public bool Bind(KittenEva kitten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(kitten, Kitten)) return _processor is not null && _failure is null;
        Unbind(); Kitten = kitten;
        try
        {
            if (kitten.IsDisposed) return Fail("The selected kitten is no longer available.");
            if (AvatarField?.GetValue(kitten.Renderable) is not CharacterAvatar avatar)
                return Fail("This KSA build does not expose the expected kitten avatar field.");
            var processor = new PoseProcessor(this, avatar.Core.CharacterModel.Skeleton);
            if (!processor.Resolved) return Fail("The kitten rig has no safe left shoulder, elbow and wrist chain.");
            _avatar = avatar; _processor = processor;
            avatar.Core.CharacterModel.AnimProcessors.Add(processor);
            SaberPoseController.NotifyProcessorsChanged(kitten);
            return true;
        }
        catch (Exception error)
        {
            if (_avatar is not null && _processor is not null)
                _avatar.Core.CharacterModel.AnimProcessors.Remove(_processor);
            _processor = null;
            return Fail($"The Force hand could not attach: {error.Message}");
        }
    }

    /// <summary>Optional aim is a direction in the kitten's body frame, not a world position.</summary>
    public void Update(double dtPlayer, bool active, double3? aimBody = null)
    {
        if (_disposed) return;
        if (Kitten is { IsDisposed: true }) { Unbind(); return; }
        float dt = double.IsFinite(dtPlayer) ? (float)Math.Clamp(dtPlayer, 0, .1) : 0;
        float target = active && Eligible ? 1 : 0;
        float amount = dt / .2f;
        Blend = Blend < target ? Math.Min(target, Blend + amount) : Math.Max(target, Blend - amount);
        if (dt > 0) _aimModel = SaberPoseMath.Unit(float3.Lerp(_aimModel, ForcePoseMath.AimModel(aimBody),
            Math.Min(1, dt * 8)), float3.UnitZ);
    }

    public void Unbind()
    {
        KittenEva? previous = Kitten;
        if (_avatar is not null && _processor is not null)
            _avatar.Core.CharacterModel.AnimProcessors.Remove(_processor);
        Kitten = null; _avatar = null; _processor = null;
        Blend = 0; _aimModel = float3.UnitZ; _failure = null;
        if (previous is not null) SaberPoseController.NotifyProcessorsChanged(previous);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unbind(); _disposed = true;
    }

    private bool Fail(string reason) { _failure = reason; return false; }

    private sealed class PoseProcessor : SaberPoseController.IArmPoseProcessor
    {
        private readonly ForcePoseController _owner;
        private readonly int[] _parents;
        private readonly float4x4[] _local, _world;
        private readonly int _shoulder = -1, _elbow = -1, _wrist = -1;
        private readonly int[] _fingers = [], _knuckles = [];
        private readonly floatQuat[] _fingerRest = [], _fingerBefore = [];
        public bool Resolved { get; }

        public PoseProcessor(ForcePoseController owner, Skeleton skeleton)
        {
            _owner = owner;
            _parents = new int[skeleton.BoneCount];
            _local = new float4x4[_parents.Length]; _world = new float4x4[_parents.Length];
            if (skeleton.NodeInfo.Length != _parents.Length) return;
            for (int i = 0; i < _parents.Length; i++) _parents[i] = skeleton.NodeInfo[i].ParentIndex;
            ReadOnlySpan<float4x4> rest = skeleton.DefaultLocalPose is { } defaults
                ? defaults.AsSpan() : skeleton.LocalTransforms;
            if (!SomersaultPoseMath.TryBuildWorld(_parents, rest, _world)
                || !SomersaultPoseMath.TryGuardArm(skeleton.BoneNames, _parents, _world, true, out var arm)) return;
            _shoulder = arm.Hip; _elbow = arm.Knee; _wrist = arm.Ankle;
            if (skeleton.BoneNames is { } armNames)
                for (int i = 0; i < armNames.Count; i++)
                    if ((string.Equals(armNames[i], "Shoulder_R", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(armNames[i], "Elbow_R", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(armNames[i], "Wrist_R", StringComparison.OrdinalIgnoreCase))
                        && (Descendant(_shoulder, i) || Descendant(i, _shoulder))) return;
            var fingers = new List<int>(); var knuckles = new List<int>();
            if (skeleton.BoneNames is { } names)
                for (int i = 0; i < names.Count; i++)
                {
                    if (!FingerName(names[i]) || !Descendant(i, _wrist)) continue;
                    fingers.Add(i);
                    if (_parents[i] == _wrist) knuckles.Add(i);
                }
            _fingers = fingers.ToArray(); _knuckles = knuckles.ToArray();
            _fingerRest = new floatQuat[_fingers.Length];
            for (int i = 0; i < _fingers.Length; i++)
                _fingerRest[i] = TransformTRS.FromMatrix(rest[_fingers[i]]).Rotation;
            _fingerBefore = new floatQuat[_fingers.Length];
            Resolved = true;
        }

        public KittenEva? Owner => _owner.Kitten;
        public bool Active => _owner.Active;
        public bool LeftHand => true;
        public float Priority => 5;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => true;

        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
            if (!Active || !Resolved) return;
            TransformTRS shoulderBefore = default, elbowBefore = default, wristBefore = default;
            bool saved = false;
            try
            {
                if (localPose.Length != _parents.Length || skeleton.NodeInfo.Length != _parents.Length)
                    throw new InvalidOperationException("The kitten skeleton changed.");
                for (int i = 0; i < _parents.Length; i++)
                    if (_parents[i] != skeleton.NodeInfo[i].ParentIndex)
                        throw new InvalidOperationException("The kitten hierarchy changed.");
                World(localPose);
                float3 shoulder = _world[_shoulder].Translation, elbow = _world[_elbow].Translation;
                float3 wrist = _world[_wrist].Translation;
                if (!ForcePoseMath.TrySolve(shoulder, elbow, wrist, _owner._aimModel,
                    out float3 elbowTarget, out float3 wristTarget)) return;
                shoulderBefore = localPose[_shoulder]; elbowBefore = localPose[_elbow]; wristBefore = localPose[_wrist];
                for (int i = 0; i < _fingers.Length; i++) _fingerBefore[i] = localPose[_fingers[i]].Rotation;
                saved = true;
                Turn(localPose, _shoulder, SaberPoseMath.RotationFromTo(elbow - shoulder, elbowTarget - shoulder));
                Turn(localPose, _elbow, SaberPoseMath.RotationFromTo(_world[_wrist].Translation - _world[_elbow].Translation,
                    wristTarget - _world[_elbow].Translation));
                if (_knuckles.Length > 0)
                {
                    float3 knuckle = float3.Zero;
                    foreach (int finger in _knuckles) knuckle += _world[finger].Translation;
                    knuckle /= _knuckles.Length;
                    Turn(localPose, _wrist, SaberPoseMath.RotationFromTo(knuckle - _world[_wrist].Translation,
                        ForcePoseMath.ReachDirection(_owner._aimModel)));
                }
                float blend = _owner.Blend;
                localPose[_shoulder] = TransformTRS.Lerp(shoulderBefore, localPose[_shoulder], blend);
                localPose[_elbow] = TransformTRS.Lerp(elbowBefore, localPose[_elbow], blend);
                localPose[_wrist] = TransformTRS.Lerp(wristBefore, localPose[_wrist], blend);
                // Open only the discovered left fingers towards their reference rotations.
                for (int i = 0; i < _fingers.Length; i++)
                    localPose[_fingers[i]].Rotation = floatQuat.Slerp(_fingerBefore[i], _fingerRest[i], blend);
            }
            catch (Exception error)
            {
                if (saved)
                {
                    localPose[_shoulder] = shoulderBefore; localPose[_elbow] = elbowBefore; localPose[_wrist] = wristBefore;
                    for (int i = 0; i < _fingers.Length; i++) localPose[_fingers[i]].Rotation = _fingerBefore[i];
                }
                _owner.Fail($"The Force hand stopped safely: {error.Message}");
            }
        }

        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt) { }

        private void Turn(Span<TransformTRS> local, int bone, floatQuat delta)
        {
            int parent = _parents[bone];
            floatQuat above = parent >= 0 ? TransformTRS.FromMatrix(_world[parent]).Rotation : floatQuat.Identity;
            floatQuat desired = floatQuat.Concatenate(floatQuat.Concatenate(local[bone].Rotation, above), delta);
            local[bone].Rotation = floatQuat.Normalize(floatQuat.Concatenate(desired, floatQuat.Inverse(above)));
            World(local);
        }

        private void World(Span<TransformTRS> pose)
        {
            for (int i = 0; i < pose.Length; i++) _local[i] = pose[i].CreateMatrix();
            if (!SomersaultPoseMath.TryBuildWorld(_parents, _local, _world))
                throw new InvalidOperationException("Invalid kitten pose geometry.");
        }

        private bool Descendant(int bone, int ancestor)
        {
            for (int steps = 0; bone >= 0 && bone < _parents.Length && steps < _parents.Length; steps++)
            {
                if (bone == ancestor) return true;
                bone = _parents[bone];
            }
            return false;
        }

        private static bool FingerName(string name) => name.Contains("index", StringComparison.OrdinalIgnoreCase)
            || name.Contains("middle", StringComparison.OrdinalIgnoreCase) || name.Contains("ring", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pinky", StringComparison.OrdinalIgnoreCase) || name.Contains("little", StringComparison.OrdinalIgnoreCase)
            || name.Contains("thumb", StringComparison.OrdinalIgnoreCase);
    }
}
