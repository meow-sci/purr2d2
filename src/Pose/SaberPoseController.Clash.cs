using Brutal.Numerics;
using System.Diagnostics.CodeAnalysis;
using KSA;
using RenderCore;
using RenderCore.Animation;

namespace Purr2D2.Pose;

public sealed partial class SaberPoseController
{
    private const double ClashHoldSeconds = .4;
    private const double ClashAngrySeconds = .5;

    internal bool ClashHeld => _clashTail?.IsHolding == true;
    internal float ClashHeldBladeLength => _clashTail?.IsHolding == true ? _clashTail.HeldBladeLength : 0f;
    internal bool HasFreshClashSnapshot => _clashTail?.HasFreshSnapshot == true;

    internal bool CanBeginClash(double now) => _clashTail?.CanBegin(now) == true;

    internal bool TryBeginClash(double now, float visibleBladeLength = 0) =>
        _clashTail?.TryBegin(now, visibleBladeLength) == true;

    internal void UpdateClash(double now) => _clashTail?.UpdateClash(now);

    internal void ClearClash() => _clashTail?.ClearClash();

    private bool TryGetHeldEmitterToModel(float4x4 observedModelToEgo, out float4x4 metresToModel)
    {
        metresToModel = float4x4.Identity;
        if (_clashTail?.IsHolding != true || _avatar is null || Kitten is not { } kitten) return false;
        if (!KittenVisualScale.TryModelToBody(observedModelToEgo, kitten.Body2Cce,
            Program.RenderedViewport.GetCamera().GetPositionEgo(kitten), out var modelToBody)
            || !double4x4.Invert(modelToBody, out var bodyToModel)) return false;
        double4x4 held = _clashTail.HeldEmitterToBody * bodyToModel;
        if (!Finite(held.Translation) || !Finite(held.Z.XYZ)) return false;
        metresToModel = float4x4.Pack(held);
        return true;
    }

    internal static bool IsClashTailFor(IAnimProcessor processor, KittenEva kitten) =>
        processor is ClashTailProcessor tail && ReferenceEquals(tail._controller.Kitten, kitten);

    /// <summary>Move the owned tail after all local-pose processors, without touching other processors.</summary>
    internal static bool MoveClashTailLast(List<IAnimProcessor> processors, KittenEva kitten)
    {
        for (int i = 0; i < processors.Count; i++)
        {
            if (!IsClashTailFor(processors[i], kitten)) continue;
            if (i == processors.Count - 1) return false;
            IAnimProcessor tail = processors[i];
            processors.RemoveAt(i);
            processors.Add(tail);
            return true;
        }
        return false;
    }

    private sealed class ClashTailProcessor : CatPostAnim
    {
        internal readonly SaberPoseController _controller;
        private readonly TransformTRS[] _shownPose;
        private readonly TransformTRS[] _heldPose;
        private readonly TransformTRS[]? _angryPose;
        private readonly int _expressionRoot;
        private bool _hasShownPose, _hasMainSnapshot, _clashActive, _holding;
        private ulong _shownFrame = ulong.MaxValue, _shownGeneration;
        private double4x4 _shownEmitterToBody, _heldEmitterToBody;
        private float _heldBladeLength;
        private double _now, _holdUntil, _angryUntil;

        [SetsRequiredMembers]
        internal ClashTailProcessor(SaberPoseController controller, CharacterAvatar avatar, Skeleton skeleton)
        {
            _controller = controller;
            CharacterAvatar = avatar;
            Priority = 5f;
            _shownPose = new TransformTRS[skeleton.BoneCount];
            _heldPose = new TransformTRS[skeleton.BoneCount];
            _expressionRoot = avatar.Expressions.ExpressionRootIndex;
            if (_expressionRoot < 0 || _expressionRoot >= skeleton.BoneCount
                || avatar.Expressions.Angry is not { Count: > 0 } angry || angry[0] is not { } expression) return;
            try
            {
                var sample = new TransformTRS[skeleton.BoneCount];
                expression.AnimationData.SampleStepped(1f, sample.AsSpan());
                _angryPose = sample;
            }
            catch
            {
                // Some custom avatars omit or mismatch the optional expression clip.
                _angryPose = null;
            }
        }

        internal bool IsHolding => _clashActive && _holding && _now < _holdUntil;
        internal float HeldBladeLength => _heldBladeLength;
        internal double4x4 HeldEmitterToBody => _heldEmitterToBody;
        internal bool HasFreshSnapshot => _hasMainSnapshot && _hasShownPose
            && _shownGeneration == _controller.MainPoseGeneration
            && _controller.HasFreshBodyFrame
            && SaberPoseMath.AgeFrames(true, _shownFrame, Program.FrameNumber) <= 1;

        internal bool CanBegin(double now) => double.IsFinite(now) && !IsHolding && _controller.Eligible
            && HasFreshSnapshot && Finite(_shownEmitterToBody.Translation)
            && Finite(_shownEmitterToBody.Z.XYZ);

        internal bool TryBegin(double now, float visibleBladeLength)
        {
            if (!CanBegin(now) || _holding || !float.IsFinite(visibleBladeLength)
                || visibleBladeLength < 0) return false;
            _shownPose.AsSpan().CopyTo(_heldPose);
            _heldEmitterToBody = _shownEmitterToBody;
            _heldBladeLength = visibleBladeLength;
            _now = now;
            _holdUntil = now + ClashHoldSeconds;
            _angryUntil = now + ClashAngrySeconds;
            _clashActive = _holding = true;
            return true;
        }

        internal void UpdateClash(double now)
        {
            if (!double.IsFinite(now) || now < _now) { ClearClash(); return; }
            _now = now;
            if (!_clashActive) return;
            if (now >= _holdUntil) _holding = false;
            if (now >= _angryUntil) ClearClash();
        }

        internal void ClearClash()
        {
            _clashActive = _holding = false;
            _holdUntil = _angryUntil = 0;
            _heldEmitterToBody = default;
            _heldBladeLength = 0;
        }

        public override void UpdateLocalPose(float4x4 transform, Skeleton skeleton,
            Span<TransformTRS> localPose, float dt)
        {
            if (localPose.Length != _shownPose.Length || skeleton.BoneCount != _shownPose.Length)
            {
                _hasShownPose = _hasMainSnapshot = false;
                return;
            }
            if (IsHolding) _heldPose.AsSpan().CopyTo(localPose);
            if (_clashActive && _now < _angryUntil && _angryPose is { } angry
                && _expressionRoot >= 0 && _expressionRoot < skeleton.BoneCount)
                MixPoseLocal(skeleton, localPose, angry.AsSpan(), _expressionRoot, 1f);
            localPose.CopyTo(_shownPose);
            _hasShownPose = true;
        }

        public override void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
        {
            IViewport viewport = Program.RenderedViewport;
            if (!ReferenceEquals(viewport, Program.MainViewport) || viewport.Type != ViewportType.Main
                || !_hasShownPose || !_controller._hasBodyFrame
                || _controller.FrameNumber != Program.FrameNumber) return;
            _shownEmitterToBody = _controller._emitterToBody;
            _shownGeneration = _controller.MainPoseGeneration;
            _shownFrame = Program.FrameNumber;
            _hasMainSnapshot = true;
        }

        private static bool Finite(double3 value) =>
            double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    }
}
