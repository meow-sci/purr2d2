using System.Reflection;
using Brutal.Numerics;
using KSA;
using RenderCore;
using RenderCore.Animation;
using Purr2D2.Core;

namespace Purr2D2.Pose;

/// <summary>
/// An equipment-independent visual overlay. Bind, SetMotion and Unbind run on the main
/// thread before rendering. The native physical orientation, trajectory and collider are untouched.
/// </summary>
internal sealed class SomersaultPoseController : IDisposable
{
    private static readonly FieldInfo? AvatarField = typeof(KittenRenderable).GetField(
        "_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);
    private CharacterAvatar? _avatar;
    private LocalProcessor? _tuckProcessor, _rootProcessor;
    private IAnimProcessor[] _hands = [];
    private int[] _parents = [], _roots = [];
    private float4x4[] _local = [], _world = [];
    private SomersaultPoseMath.TuckRig _rig;
    private SomersaultPoseMath.Leg _leftArm, _rightArm;
    private bool _hasLeftArm, _hasRightArm;
    private float3 _pivot;
    private bool _disposed, _active;
    private string? _failure;
    private SomersaultDirection _direction;
    private float _radians, _tuck;

    public KittenEva? Kitten { get; private set; }
    public bool SupportsTuck { get; private set; }
    public bool Eligible => !_disposed && _failure is null && _rootProcessor is not null
        && Kitten is { IsDisposed: false } kitten
        && kitten.LocomotionState.Mode is LocomotionMode.Grounded or LocomotionMode.Airborne;
    public string Status => _failure ?? (Kitten is null ? "Control an EVA kitten to somersault."
        : !Eligible ? "Somersaults require a walking EVA kitten."
        : SupportsTuck ? "Somersault pose ready." : "Somersault ready; this rig uses a full-body turn without tuck.");

    public bool Bind(KittenEva kitten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(Kitten, kitten)) return _rootProcessor is not null && _failure is null;
        Unbind();
        Kitten = kitten;
        try
        {
            if (kitten.IsDisposed) return Fail("The selected kitten is no longer available.");
            if (AvatarField?.GetValue(kitten.Renderable) is not CharacterAvatar avatar)
                return Fail("This KSA build does not expose the expected kitten avatar field.");
            Skeleton skeleton = avatar.Core.CharacterModel.Skeleton;
            if (skeleton.BoneCount == 0 || skeleton.NodeInfo.Length != skeleton.BoneCount)
                return Fail("The kitten skeleton has no safe visual hierarchy.");
            _parents = new int[skeleton.BoneCount];
            for (int i = 0; i < _parents.Length; i++) _parents[i] = skeleton.NodeInfo[i].ParentIndex;
            if (!SomersaultPoseMath.TryRoots(_parents, out _roots)
                || !SomersaultPoseMath.TryPivot(avatar.Core.Scale, kitten.CenterOfMassAsmbF, out _pivot))
                return Fail("The kitten rig has no safe root transform or centre-of-mass pivot.");
            _local = new float4x4[_parents.Length];
            _world = new float4x4[_parents.Length];
            ReadOnlySpan<float4x4> rest = skeleton.DefaultLocalPose is { } defaults
                ? defaults.AsSpan() : skeleton.LocalTransforms;
            if (rest.Length != _parents.Length || !SomersaultPoseMath.TryBuildWorld(_parents, rest, _world))
                return Fail("The kitten's reference skeleton is invalid.");
            if (!SomersaultPoseMath.PivotNearRig(_pivot, _world))
                return Fail("The kitten's centre-of-mass pivot is outside its visual rig.");
            SupportsTuck = SomersaultPoseMath.TryResolveTuck(skeleton.BoneNames, _parents, _world, out _rig);
            _hasLeftArm = SomersaultPoseMath.TryGuardArm(skeleton.BoneNames, _parents, _world, true, out _leftArm);
            _hasRightArm = SomersaultPoseMath.TryGuardArm(skeleton.BoneNames, _parents, _world, false, out _rightArm);
            _avatar = avatar;
            _tuckProcessor = new(this, root: false);
            _rootProcessor = new(this, root: true);
            SaberPoseController.ProcessorsChanged += OnHandProcessorsChanged;
            OrderProcessors();
            return true;
        }
        catch (Exception error)
        {
            DetachProcessors();
            return Fail($"The somersault pose could not attach: {error.Message}");
        }
    }

    /// <summary>Read a confirmed motion snapshot; time never advances from rendering.</summary>
    public void SetMotion(SomersaultDirection direction, float rotationRadians, float tuck, bool active)
    {
        if (_disposed) return;
        if (Kitten is { IsDisposed: true }) { Unbind(); return; }
        _active = active && Eligible && float.IsFinite(rotationRadians) && float.IsFinite(tuck)
            && SomersaultPoseMath.Axis(direction).LengthSquared() > .9f;
        _direction = direction;
        _radians = _active ? Math.Clamp(rotationRadians, 0, SomersaultCombo.MaximumJumps * MathF.Tau) : 0;
        _tuck = _active ? Math.Clamp(tuck, 0, 1) : 0;
        OrderProcessors();
        if (!_active) SetSaberVisualTransform(float4x4.Identity);
    }

    public void Unbind()
    {
        DetachProcessors();
        Kitten = null;
        _avatar = null;
        _parents = _roots = [];
        _local = _world = [];
        _active = false;
        _radians = _tuck = 0;
        SupportsTuck = false;
        _hasLeftArm = _hasRightArm = false;
        _failure = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unbind();
        _disposed = true;
    }

    private bool Fail(string reason) { _active = false; _failure = reason; return false; }

    private void DetachProcessors()
    {
        SaberPoseController.ProcessorsChanged -= OnHandProcessorsChanged;
        SetSaberVisualTransform(float4x4.Identity);
        if (_avatar is not null)
        {
            if (_tuckProcessor is not null) _avatar.Core.CharacterModel.AnimProcessors.Remove(_tuckProcessor);
            if (_rootProcessor is not null) _avatar.Core.CharacterModel.AnimProcessors.Remove(_rootProcessor);
        }
        _hands = [];
        _tuckProcessor = _rootProcessor = null;
    }

    private void OnHandProcessorsChanged(KittenEva kitten)
    {
        if (ReferenceEquals(kitten, Kitten)) OrderProcessors();
    }

    private void OrderProcessors()
    {
        if (_avatar is null || Kitten is null || _tuckProcessor is null || _rootProcessor is null) return;
        List<IAnimProcessor> processors = _avatar.Core.CharacterModel.AnimProcessors;
        int start = processors.Count - _hands.Length - 2;
        if (start >= 0 && ReferenceEquals(processors[start], _tuckProcessor)
            && ReferenceEquals(processors[^1], _rootProcessor))
        {
            bool ordered = true;
            for (int i = 0; i < _hands.Length; i++)
                ordered &= ReferenceEquals(processors[start + 1 + i], _hands[i]);
            if (ordered) return;
        }
        _hands = processors.Where(p => SaberPoseController.IsArmProcessorFor(p, Kitten)).ToArray();
        // Priority is a distance-LOD multiplier, not execution order. Keep our local composition
        // explicitly last, including when either hand attaches after the motion layer.
        processors.Remove(_tuckProcessor);
        processors.Remove(_rootProcessor);
        foreach (IAnimProcessor saber in _hands) processors.Remove(saber);
        processors.Add(_tuckProcessor);
        foreach (IAnimProcessor saber in _hands) processors.Add(saber);
        processors.Add(_rootProcessor);
    }

    private void SetSaberVisualTransform(float4x4 transform)
    {
        foreach (IAnimProcessor saber in _hands) SaberPoseController.SetVisualRootTransform(saber, transform);
    }

    private void ApplyTuck(Span<TransformTRS> local)
    {
        if (!SupportsTuck || _tuck <= 0) return;
        World(local);
        // Guard only unowned measured arms; active saber and Force hands each
        // retain exclusive ownership of their following arm solve.
        if (_rig.Spine >= 0)
            Turn(local, _rig.Spine, floatQuat.CreateFromAxisAngle(_rig.Across, .28f * _tuck));
        TuckLeg(local, _rig.Left);
        TuckLeg(local, _rig.Right);
        if (_hasLeftArm && !_hands.Any(p => SaberPoseController.IsActiveArmProcessor(p, left: true)))
            GuardArm(local, _leftArm, left: true);
        if (_hasRightArm && !_hands.Any(p => SaberPoseController.IsActiveArmProcessor(p, left: false)))
            GuardArm(local, _rightArm, left: false);
    }

    private void TuckLeg(Span<TransformTRS> local, SomersaultPoseMath.Leg leg)
    {
        float3 thigh = _world[leg.Knee].Translation - _world[leg.Hip].Translation;
        Turn(local, leg.Hip, floatQuat.Slerp(floatQuat.Identity,
            SaberPoseMath.RotationFromTo(thigh, SomersaultPoseMath.ThighDirection(_rig)), _tuck));
        float3 calf = _world[leg.Ankle].Translation - _world[leg.Knee].Translation;
        Turn(local, leg.Knee, floatQuat.Slerp(floatQuat.Identity,
            SaberPoseMath.RotationFromTo(calf, SomersaultPoseMath.CalfDirection(_rig)), _tuck));
    }

    private void GuardArm(Span<TransformTRS> local, SomersaultPoseMath.Leg arm, bool left)
    {
        float side = left ? 1 : -1;
        float3 upper = _world[arm.Knee].Translation - _world[arm.Hip].Translation;
        float3 upperTarget = _rig.Forward * .65f - _rig.Up * .55f + _rig.Across * (.4f * side);
        Turn(local, arm.Hip, floatQuat.Slerp(floatQuat.Identity,
            SaberPoseMath.RotationFromTo(upper, upperTarget), _tuck * .75f));
        float3 lower = _world[arm.Ankle].Translation - _world[arm.Knee].Translation;
        float3 lowerTarget = _rig.Up * .55f + _rig.Forward * .6f - _rig.Across * (.55f * side);
        Turn(local, arm.Knee, floatQuat.Slerp(floatQuat.Identity,
            SaberPoseMath.RotationFromTo(lower, lowerTarget), _tuck * .75f));
    }

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
            throw new InvalidOperationException("Invalid kitten pose hierarchy.");
    }

    private void Apply(bool root, Skeleton skeleton, Span<TransformTRS> pose)
    {
        if (!_active || !Eligible) return;
        try
        {
            if (pose.Length != _parents.Length || skeleton.NodeInfo.Length != _parents.Length)
                throw new InvalidOperationException("The kitten skeleton changed during a somersault.");
            for (int i = 0; i < _parents.Length; i++)
                if (_parents[i] != skeleton.NodeInfo[i].ParentIndex)
                    throw new InvalidOperationException("The kitten skeleton hierarchy changed.");
            if (!root) { ApplyTuck(pose); return; }
            float4x4 rotation = SomersaultPoseMath.Rotation(_direction, _radians, _pivot);
            foreach (int i in _roots) pose[i] = TransformTRS.FromMatrix(pose[i].CreateMatrix() * rotation);
            SetSaberVisualTransform(rotation);
        }
        catch (Exception error)
        {
            // The next sampled pose is native again. Release motion ownership at the next safe seam.
            SetSaberVisualTransform(float4x4.Identity);
            Fail($"The somersault pose stopped safely: {error.Message}");
        }
    }

    private sealed class LocalProcessor(SomersaultPoseController owner, bool root) : IAnimProcessor
    {
        public float Priority => 5f;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => true;
        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
            => owner.Apply(root, skeleton, localPose);
        // All local callbacks precede the native per-view world rebuild. Saber UpdateSkeleton
        // therefore publishes the final flipped hand for every viewport, without a late pose edit.
        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt) { }
    }
}
