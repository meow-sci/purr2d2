using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly Dictionary<KittenEva, bool> _dropRequests = new(ReferenceEqualityComparer.Instance);
    private bool SaberHeldForActions => (_activeSaber ?? _idleSaber).Drop.Held;

    private void QueueSaberDrop(KittenEva owner)
    {
        if (!_enabled || !_ready || _disposed || _fault != null || !_currentKittens.Contains(owner)
            || !_kittenSabers.TryGetValue(owner, out var saber) || !saber.Blade.Equipped
            || saber.TogglePhase is SaberTogglePhase.Retracting or SaberTogglePhase.Unequipping) return;
        if (saber.Drop.Phase == SaberDropSequence.Stage.Returning
            || saber.Drop.Held && (!saber.Pose.HasFreshBodyFrame || !owner.HasPhysicsBubble)) return;
        // A second click while reaching cancels the gesture; requests never mutate native state in UI.
        _dropRequests[owner] = saber.Drop.Held;
    }

    private void ApplySaberDropRequests()
    {
        foreach (var request in _dropRequests)
        {
            if (!_currentKittens.Contains(request.Key) || request.Key.IsDisposed
                || !_kittenSabers.TryGetValue(request.Key, out var saber) || !saber.Blade.Equipped
                || saber.TogglePhase is SaberTogglePhase.Retracting or SaberTogglePhase.Unequipping) continue;
            if (request.Value && (!saber.Drop.Held || !saber.Pose.HasFreshBodyFrame || !request.Key.HasPhysicsBubble)
                || !request.Value && saber.Drop.Phase is SaberDropSequence.Stage.Held or SaberDropSequence.Stage.Returning) continue;
            RemoveClashOwner(request.Key);
            DisableSaberAutoSwing(saber);
            if (ReferenceEquals(request.Key, _owner))
            {
                CancelAssistedSlice(""); ResetOverlaySwing();
                _manualSwingSwoosh.Reset(); _contactProbe.Reset();
            }
            if (request.Value)
            {
                saber.Drop.BeginDrop();
            }
            else if (saber.Drop.Phase == SaberDropSequence.Stage.Reaching) ResetSaberDrop(request.Key, saber);
            else if (saber.Drop.Phase == SaberDropSequence.Stage.Dropped)
            {
                if (saber.DroppedBody is not { } dropped || !dropped.TryFrame(out var start))
                { ResetSaberDrop(request.Key, saber); continue; }
                saber.ReturnStart = start;
                saber.DroppedBody?.Dispose(); saber.DroppedBody = null;
                saber.Drop.BeginReturn();
            }
            saber.FreshPoseThisTick = saber.HasPreviousTip = false; saber.SwingSpeed = 0;
        }
        _dropRequests.Clear();
    }

    private void AdvanceSaberDrop(KittenSaber saber, double dt)
    {
        var pose = saber.Pose;
        pose.PalmDown = saber.Drop.Phase == SaberDropSequence.Stage.Reaching;
        // An offscreen hand cannot provide a return target. Wait for its next
        // native pose instead of exhausting the animation and snapping to it later.
        if (saber.Drop.Phase != SaberDropSequence.Stage.Returning || pose.HasFreshBodyFrame)
            saber.Drop.Advance(dt);
        if (pose.Kitten is not { IsDisposed: false } owner) return;
        if (saber.Drop.ReadyToRelease && pose.PalmDownBlend >= .999f && pose.TryGetBodyFrame(out var emitter))
        {
            if (DroppedSaberBody.TryCreate(owner, emitter, out var body, out var reason))
            {
                saber.DroppedBody = body;
                saber.Drop.Released(); pose.PalmDown = false;
                saber.HasPreviousTip = false; saber.SwingSpeed = 0;
            }
            else
            {
                ResetSaberDrop(owner, saber);
                _sliceStatus = reason; _sliceMessageUntil = _playerTime + 5;
            }
        }
        if (saber.Drop.Phase == SaberDropSequence.Stage.Dropped
            && saber.DroppedBody is not { Vehicle.IsDisposed: false }) ResetSaberDrop(owner, saber);
        if (saber.Drop.ReadyToAttach && pose.HasFreshBodyFrame)
            ResetSaberDrop(owner, saber);
    }

    private bool TryDetachedSaberFrame(KittenEva owner, KittenSaber saber, out double4x4 emitterToEcl)
    {
        emitterToEcl = default;
        if (saber.Drop.Phase == SaberDropSequence.Stage.Dropped)
            return saber.DroppedBody?.TryFrame(out emitterToEcl) == true;
        if (saber.Drop.Phase != SaberDropSequence.Stage.Returning) return false;
        emitterToEcl = saber.ReturnStart;
        if (!saber.Pose.TryGetBodyFrame(out var hand)) return SaberDropMath.ValidFrame(emitterToEcl);
        var target = hand * double4x4.CreateFromQuaternion(owner.Body2Cce) * double4x4.CreateTranslation(owner.GetPositionEcl());
        float t = saber.Drop.ReturnBlend;
        // Rotate along the shortest rigid path while retaining the observed affine stretch/shear.
        emitterToEcl = SaberDropMath.Interpolate(saber.ReturnStart, target, t);
        return SaberDropMath.ValidFrame(emitterToEcl);
    }

    private void PublishDetachedSaber(KittenEva owner, KittenSaber saber)
    {
        if (!saber.Blade.Equipped || !TryDetachedSaberFrame(owner, saber, out var frame)) return;
        _renderer.SetDetachedSaber(owner, frame, VisibleBladeLength(saber), saber.Color, saber.Type);
        var bodyToEcl = double4x4.CreateFromQuaternion(owner.Body2Cce) * double4x4.CreateTranslation(owner.GetPositionEcl());
        if (double4x4.Invert(bodyToEcl, out var eclToBody))
            _renderer.SetEmitter(owner, frame * eclToBody, VisibleBladeLength(saber), saber.Color);
    }

    private bool UpdateDetachedSaberAudio(KittenEva owner, KittenSaber saber)
    {
        if (!saber.Drop.Detached) return false;
        if (TryDetachedSaberFrame(owner, saber, out var frame))
        {
            // Audio's existing owner frame conversion keeps the exact detached world position.
            var bodyPoint = (frame.Translation - owner.GetPositionEcl()).Transform(doubleQuat.Inverse(owner.Body2Cce));
            saber.Audio.Update(owner, bodyPoint, saber.Blade.Deployment, saber.Blade.TargetOn,
                !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()), _settings.Volume,
                type: saber.Type, spatialOwner: saber.DroppedBody?.Vehicle);
        }
        else saber.Audio.Suspend();
        return true;
    }

    private void ResetSaberDrop(KittenEva owner, KittenSaber saber)
    {
        saber.DroppedBody?.Dispose(); saber.DroppedBody = null;
        saber.Drop.Reset(); saber.Pose.PalmDown = false;
        saber.ReturnStart = default;
        saber.FreshPoseThisTick = saber.HasPreviousTip = false; saber.SwingSpeed = 0;
        _renderer.RemoveDetachedSaber(owner);
    }

    internal void ResetAllDroppedSabers()
    {
        foreach (var pair in _kittenSabers) ResetSaberDrop(pair.Key, pair.Value);
        _dropRequests.Clear();
    }
}
