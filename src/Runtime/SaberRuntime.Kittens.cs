using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly KittenSaber _idleSaber = new();
    private readonly Dictionary<KittenEva, KittenSaber> _kittenSabers = new(ReferenceEqualityComparer.Instance);
    private readonly List<KittenEva> _overlayKittens = new();
    private readonly HashSet<KittenEva> _currentKittens = new(ReferenceEqualityComparer.Instance);
    private readonly List<KittenEva> _removedKittens = new();
    private readonly Queue<KittenSaberRequest> _kittenSaberRequests = new();
    private KittenSaber? _activeSaber;

    private readonly record struct KittenSaberRequest(KittenEva Kitten, bool Enabled);

    private sealed class KittenSaber
    {
        internal readonly BladeState Blade = new();
        internal readonly SaberPoseController Pose;
        internal readonly SaberAudio Audio = new();
        internal ulong ConsumedPoseGeneration;
        internal bool FreshPoseThisTick, HasPreviousTip;
        internal double3 PreviousTipBody;
        internal double PreviousTipTime;
        internal float SwingSpeed;

        internal KittenSaber(Action<KittenEva, IViewport, float4x4>? draw = null) => Pose = new(draw);

        internal void Dispose()
        {
            Cleanup(Audio.Shutdown, "Kitten audio");
            Cleanup(Audio.Dispose, "Kitten audio");
            Cleanup(Pose.Dispose, "Kitten pose");
            Blade.Reset();
        }
    }

    // The UI reads this settled snapshot and enqueues requests. It never scans the
    // live system or changes avatar/audio state while the solver is in flight.
    private IReadOnlyList<KittenEva> OverlayKittens => _overlayKittens;

    private bool SaberEnabledFor(KittenEva kitten)
    {
        bool enabled = _kittenSabers.TryGetValue(kitten, out var saber) && saber.Blade.Equipped && saber.Blade.TargetOn;
        // Reflect the latest click while waiting for the next settled handoff.
        foreach (var request in _kittenSaberRequests)
            if (ReferenceEquals(request.Kitten, kitten)) enabled = request.Enabled;
        return enabled;
    }

    private void QueueKittenSaber(KittenEva kitten, bool enabled)
    {
        if (_ready && !_disposed && _fault == null && !Program.EditorFlag
            && !kitten.IsDisposed && _currentKittens.Contains(kitten) && _kittenSaberRequests.Count < 128)
            _kittenSaberRequests.Enqueue(new(kitten, enabled));
    }

    private static string KittenLabel(KittenEva kitten) => kitten.Id;

    private void RefreshOverlayKittens()
    {
        _overlayKittens.Clear();
        _currentKittens.Clear();
        if (!Program.EditorFlag && Universe.CurrentSystem is { } system)
            foreach (var item in system.All.AsSpan())
                if (item is KittenEva { IsDisposed: false } kitten)
                {
                    _overlayKittens.Add(kitten);
                    _currentKittens.Add(kitten);
                }

        _removedKittens.Clear();
        foreach (var pair in _kittenSabers)
            if (!_currentKittens.Contains(pair.Key)) _removedKittens.Add(pair.Key);
        foreach (var kitten in _removedKittens)
        {
            // Boarding destroys/deregisters the EVA object. Release its processor,
            // MMU override and owned channels even if it was not being controlled.
            var saber = _kittenSabers[kitten];
            if (ReferenceEquals(saber, _activeSaber)) SetControlledKitten(null);
            saber.Dispose();
            _kittenSabers.Remove(kitten);
            _renderer.RemoveEmitter(kitten);
        }
        _removedKittens.Clear();
    }

    private KittenSaber GetKittenSaber(KittenEva kitten)
    {
        if (!_kittenSabers.TryGetValue(kitten, out var saber))
        {
            saber = new(DrawSaber);
            _kittenSabers.Add(kitten, saber);
        }
        return saber;
    }

    private void SetControlledKitten(KittenEva? kitten)
    {
        CancelAssistedSlice(_sliceSequence.Active || _selectingSlice ? "Slice cancelled: controlled kitten changed." : "");
        ResetOverlaySwing();
        _targeting.Reset(); _contactProbe.Reset();
        _owner = kitten;
        _activeSaber = kitten != null ? GetKittenSaber(kitten) : null;
        var saber = _activeSaber ?? _idleSaber;
        _blade = saber.Blade; _pose = saber.Pose; _audio = saber.Audio;
        // A new controlled owner needs a newly rendered main-view pose before it
        // can slice; an earlier view of that same kitten must not authorize a cut.
        _consumedPoseGeneration = _pose.MainPoseGeneration;
        saber.ConsumedPoseGeneration = _consumedPoseGeneration;
        saber.FreshPoseThisTick = false;
        _freshPoseThisTick = _hasPreviousTip = false; _swingSpeed = 0;
        saber.HasPreviousTip = false; saber.SwingSpeed = 0;
    }

    private void ApplyKittenSaberRequests()
    {
        while (_kittenSaberRequests.TryDequeue(out var request))
        {
            if (_fault != null || !_currentKittens.Contains(request.Kitten) || request.Kitten.IsDisposed) continue;
            var saber = GetKittenSaber(request.Kitten);
            if (ReferenceEquals(saber, _activeSaber) && (_sliceSequence.Active || _selectingSlice))
                CancelAssistedSlice("Slice cancelled by manual control.");
            saber.Blade.Equip(request.Enabled);
            saber.Blade.SetIgnited(request.Enabled);
            if (request.Enabled) saber.Pose.Bind(request.Kitten);
            else
            {
                saber.Pose.Unbind(); saber.Audio.Stop(); _renderer.RemoveEmitter(request.Kitten);
                saber.FreshPoseThisTick = saber.HasPreviousTip = false; saber.SwingSpeed = 0;
            }
        }
    }

    private void UpdateOtherSabers(double playerDt)
    {
        foreach (var pair in _kittenSabers)
        {
            var saber = pair.Value;
            if (ReferenceEquals(saber, _activeSaber)) continue;
            var pose = saber.Pose;
            pose.GripOffsetFromChestCm = new(_settings.GripX, _settings.GripY, _settings.GripZ);
            pose.BladeDirectionModel = new(_settings.AimX, _settings.AimY, _settings.AimZ);
            pose.FingerCurlRadians = _settings.FingerCurl;
            pose.HideMmuWhenEquipped = _settings.HideMmu;
            pose.Update(playerDt, saber.Blade.Equipped);
            saber.FreshPoseThisTick = pose.MainPoseGeneration != saber.ConsumedPoseGeneration && pose.HasFreshBodyFrame;
            saber.ConsumedPoseGeneration = pose.MainPoseGeneration;
            if (!pose.Eligible) saber.Blade.SetIgnited(false);
            saber.Blade.Advance(playerDt);
            if (saber.Blade.Equipped && saber.FreshPoseThisTick && pose.TryGetBodyFrame(out var emitter))
                _renderer.SetEmitter(pair.Key, emitter, saber.Blade.Deployment * _settings.BladeLength, Color);
            else if (saber.Blade.Equipped) saber.Audio.Suspend();
            else saber.Audio.Stop();
        }
    }

    private void UpdateOtherSaberAudio()
    {
        bool unsupportedSpeed = !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed());
        foreach (var pair in _kittenSabers)
        {
            var saber = pair.Value;
            if (ReferenceEquals(saber, _activeSaber)) continue;
            float length = saber.Blade.Deployment * _settings.BladeLength;
            if (saber.Blade.Equipped && saber.FreshPoseThisTick && saber.Pose.TryGetBladeSegment(length, out var emitter, out _))
                saber.Audio.Update(pair.Key, emitter, saber.Blade.Deployment, saber.Blade.TargetOn,
                    unsupportedSpeed || !saber.Pose.HasFreshBodyFrame, _settings.Volume, saber.SwingSpeed);
            else if (saber.Blade.Equipped) saber.Audio.Suspend();
            else saber.Audio.Stop();
        }
    }

    private void ResetKittenSabers()
    {
        foreach (var saber in _kittenSabers.Values) saber.Dispose();
        _kittenSabers.Clear(); _kittenSaberRequests.Clear(); _overlayKittens.Clear();
        _currentKittens.Clear(); _removedKittens.Clear(); _activeSaber = null;
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _blade.Reset(); _pose.Unbind(); _audio.Reset();
    }
}
