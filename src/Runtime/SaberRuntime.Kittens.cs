using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Capes;
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
    private int _nextKittenColor;
    private static readonly float3[] KittenColorPalette =
    [
        new(143 / 255f, 16 / 255f, 16 / 255f),
        new(13 / 255f, 81 / 255f, 1 / 255f),
        new(49 / 255f, 49 / 255f, 5 / 255f),
        new(98 / 255f, 9 / 255f, 96 / 255f),
    ];

    private readonly record struct KittenSaberRequest(KittenEva Kitten, bool Enabled);
    private enum SaberTogglePhase { None, Equipping, Retracting, Unequipping }

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
        internal float3 Color = new(.12f, .75f, 1);
        internal SaberType Type = SaberType.Light;
        internal SaberTogglePhase TogglePhase;
        internal bool CapeEnabled;
        internal CapeController? Cape;
        internal string? CapeFailure;
        internal bool CapeFailureLogged;
        internal bool CapeFailureLatched;

        internal KittenSaber(Action<KittenEva, IViewport, float4x4>? draw = null) => Pose = new(draw);

        internal void Dispose()
        {
            Cleanup(Audio.Shutdown, "Kitten audio");
            Cleanup(Audio.Dispose, "Kitten audio");
            Cleanup(Pose.Dispose, "Kitten pose");
            if (Cape is { } cape) Cleanup(cape.Dispose, "Kitten cape");
            Cape = null;
            Blade.Reset();
            TogglePhase = SaberTogglePhase.None;
        }
    }

    // The UI reads this settled snapshot and enqueues requests. It never scans the
    // live system or changes avatar/audio state while the solver is in flight.
    private IReadOnlyList<KittenEva> OverlayKittens => _overlayKittens;

    private float3 DefaultSaberColor => ClampSaberColor(new(_settings.ColorR, _settings.ColorG, _settings.ColorB));
    private float3 Color => _activeSaber?.Color ?? DefaultSaberColor;
    private float3 SaberColorFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? saber.Color : DefaultSaberColor;
    private SaberType SaberTypeFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? saber.Type : SaberType.Light;
    private KittenEva? AppearanceKitten => !Program.EditorFlag && Program.ControlledVehicle is KittenEva { IsDisposed: false } kitten
        && _currentKittens.Contains(kitten) ? kitten : null;

    private void SetKittenSaberColor(KittenEva kitten, float3 color)
    {
        if (!kitten.IsDisposed && _currentKittens.Contains(kitten) && _kittenSabers.TryGetValue(kitten, out var saber))
            saber.Color = ClampSaberColor(color);
    }

    private void SetKittenSaberType(KittenEva kitten, SaberType type)
    {
        if (!kitten.IsDisposed && _currentKittens.Contains(kitten) && _kittenSabers.TryGetValue(kitten, out var saber))
            saber.Type = type == SaberType.Dark ? SaberType.Dark : SaberType.Light;
    }

    private static float3 ClampSaberColor(float3 color) => new(
        float.IsFinite(color.X) ? Math.Clamp(color.X, 0, 1) : .12f,
        float.IsFinite(color.Y) ? Math.Clamp(color.Y, 0, 1) : .75f,
        float.IsFinite(color.Z) ? Math.Clamp(color.Z, 0, 1) : 1);

    private bool SaberEnabledFor(KittenEva kitten)
    {
        bool enabled = _kittenSabers.TryGetValue(kitten, out var saber)
            && (saber.TogglePhase == SaberTogglePhase.Equipping
                || saber.TogglePhase == SaberTogglePhase.None && saber.Blade.Equipped && saber.Blade.TargetOn);
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

    private void QueueControlledSaberToggle()
    {
        if (AppearanceKitten is { } kitten) QueueKittenSaber(kitten, !SaberEnabledFor(kitten));
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
                    // Seed color at first sight, without equipping or binding a pose.
                    GetKittenSaber(kitten);
                }

        _removedKittens.Clear();
        foreach (var pair in _kittenSabers)
            if (!_currentKittens.Contains(pair.Key)) _removedKittens.Add(pair.Key);
        foreach (var kitten in _removedKittens)
        {
            // Boarding destroys/deregisters the EVA object. Release its processor,
            // MMU override and owned channels even if it was not being controlled.
            var saber = _kittenSabers[kitten];
            if (ReferenceEquals(kitten, _kittenColorPickerOwner)) DismissKittenColorPicker();
            if (ReferenceEquals(saber, _activeSaber)) SetControlledKitten(null);
            DisposeKittenCape(kitten, saber, clearFailure: true);
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
            saber = new(DrawSaber) { Color = KittenColorPalette[_nextKittenColor] };
            _nextKittenColor = (_nextKittenColor + 1) % KittenColorPalette.Length;
            _kittenSabers.Add(kitten, saber);
        }
        return saber;
    }

    private void SetControlledKitten(KittenEva? kitten)
    {
        DismissKittenColorPicker();
        CancelAssistedSlice(SliceWorkActive || _selectingSlice ? "Slice cancelled: controlled kitten changed." : "");
        ResetOverlaySwing();
        _targeting.Reset(); _contactProbe.Reset();
        _owner = kitten;
        _activeSaber = kitten != null && _kittenSabers.TryGetValue(kitten, out var selectedSaber) ? selectedSaber : null;
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
            if (_fault != null || !_currentKittens.Contains(request.Kitten) || request.Kitten.IsDisposed
                || !_kittenSabers.TryGetValue(request.Kitten, out var saber)) continue;
            if (ReferenceEquals(saber, _activeSaber) && (SliceWorkActive || _selectingSlice))
                CancelAssistedSlice("Slice cancelled by manual control.");
            if (request.Enabled)
            {
                saber.Blade.Equip(true);
                saber.Pose.Bind(request.Kitten);
                saber.TogglePhase = SaberTogglePhase.Equipping;
            }
            else
            {
                // Keep the hilt, pose and channels alive through retraction and
                // arm lowering. A later enable reverses from the current pose/length.
                saber.Blade.SetIgnited(false);
                if (saber.TogglePhase != SaberTogglePhase.Unequipping)
                    saber.TogglePhase = saber.Blade.Equipped ? SaberTogglePhase.Retracting : SaberTogglePhase.None;
                if (ReferenceEquals(saber, _activeSaber)) ResetOverlaySwing();
            }
        }
    }

    private void ConfigureSaberPose(SaberPoseController pose)
    {
        pose.GripOffsetFromChestCm = new(_settings.GripX, _settings.GripY, _settings.GripZ);
        pose.BladeDirectionModel = new(_settings.AimX, _settings.AimY, _settings.AimZ);
        pose.FingerCurlRadians = _settings.FingerCurl;
        pose.HideMmuWhenEquipped = _settings.HideMmu;
    }

    // Shared by controlled and background kittens. Each completed stage is
    // presented for a frame before the next stage starts; pause advances none.
    private void AdvanceKittenSaber(KittenSaber saber, double playerDt)
    {
        var pose = saber.Pose;
        bool advancing = double.IsFinite(playerDt) && playerDt > 0;
        if (advancing)
        {
            if (saber.TogglePhase == SaberTogglePhase.Equipping && pose.Eligible && pose.Blend == 1)
            {
                saber.Blade.SetIgnited(true);
                saber.TogglePhase = SaberTogglePhase.None;
            }
            else if (saber.TogglePhase == SaberTogglePhase.Retracting && saber.Blade.Deployment == 0)
                saber.TogglePhase = SaberTogglePhase.Unequipping;
        }
        pose.Update(playerDt, saber.Blade.Equipped && saber.TogglePhase != SaberTogglePhase.Unequipping);
        saber.FreshPoseThisTick = pose.MainPoseGeneration != saber.ConsumedPoseGeneration && pose.HasFreshBodyFrame;
        saber.ConsumedPoseGeneration = pose.MainPoseGeneration;
        // Pose availability can change while moving or grabbing a ladder. It
        // suspends presentation, never the player's ignition/equipment intent.
        // Pending equip resumes when the hand pose is available again.
        saber.Blade.Advance(playerDt);
        if (advancing && saber.TogglePhase == SaberTogglePhase.Unequipping && pose.Blend == 0)
        {
            saber.Blade.Equip(false);
            if (pose.Kitten is { } kitten) _renderer.RemoveEmitter(kitten);
            pose.Unbind(); saber.Audio.Stop();
            saber.TogglePhase = SaberTogglePhase.None;
            saber.FreshPoseThisTick = saber.HasPreviousTip = false; saber.SwingSpeed = 0;
        }
    }

    private void UpdateOtherSabers(double playerDt)
    {
        foreach (var pair in _kittenSabers)
        {
            var saber = pair.Value;
            if (ReferenceEquals(saber, _activeSaber)) continue;
            ConfigureSaberPose(saber.Pose);
            AdvanceKittenSaber(saber, playerDt);
            var pose = saber.Pose;
            if (saber.Blade.Equipped && saber.FreshPoseThisTick && pose.TryGetBodyFrame(out var emitter))
                _renderer.SetEmitter(pair.Key, emitter, saber.Blade.Deployment * _settings.BladeLength, saber.Color);
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
                    unsupportedSpeed || !saber.Pose.HasFreshBodyFrame, _settings.Volume, saber.SwingSpeed,
                    type: saber.Type);
            else if (saber.Blade.Equipped) saber.Audio.Suspend();
            else saber.Audio.Stop();
        }
    }

    private void ResetKittenSabers()
    {
        DismissKittenColorPicker();
        foreach (var pair in _kittenSabers)
        {
            DisposeKittenCape(pair.Key, pair.Value, clearFailure: true);
            pair.Value.Dispose();
        }
        _capeControllers.Clear();
        _capeRuntimeFaulted = _capeRuntimeFaultLogged = false;
        _capeRoundRobinCursor = 0;
        _kittenSabers.Clear(); _kittenSaberRequests.Clear(); _overlayKittens.Clear();
        _currentKittens.Clear(); _removedKittens.Clear(); _activeSaber = null;
        _nextKittenColor = 0;
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _blade.Reset(); _pose.Unbind(); _audio.Reset();
    }
}
