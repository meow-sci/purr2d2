using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Capes;
using Purr2D2.Core;
using Purr2D2.Pose;
using System.Reflection;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly KittenSaber _idleSaber = new();
    private readonly Dictionary<KittenEva, KittenSaber> _kittenSabers = new(ReferenceEqualityComparer.Instance);
    private readonly List<KittenEva> _overlayKittens = new();
    private readonly HashSet<KittenEva> _currentKittens = new(ReferenceEqualityComparer.Instance);
    private readonly List<KittenEva> _removedKittens = new();
    private readonly List<KittenSaberRequest> _kittenSaberRequests = new();
    private readonly Dictionary<KittenEva, bool> _kittenIntentWrites = new(ReferenceEqualityComparer.Instance);
    private const int PendingRequestsPerKittenLimit = 2;
    partial void BeginKittenProfileRefresh();
    partial void RestoreKittenChoices(KittenEva kitten, KittenSaber saber);
    partial void RememberKittenChoices(KittenEva kitten, KittenSaber saber, bool revalidate = false, bool? enabled = null);
    partial void ForgetKittenBinding(KittenEva kitten);
    partial void ReleaseProfileBindings();
    private KittenSaber? _activeSaber;
    private int _nextKittenColor;
    private static readonly float3[] KittenColorPalette =
    [
        new(143 / 255f, 16 / 255f, 16 / 255f),
        new(13 / 255f, 81 / 255f, 1 / 255f),
        new(49 / 255f, 49 / 255f, 5 / 255f),
        new(98 / 255f, 9 / 255f, 96 / 255f),
    ];

    private readonly record struct KittenSaberRequest(KittenEva Kitten, bool Enabled,
        float? ManualDeployment = null, bool RememberIntent = true);
    private enum SaberTogglePhase { None, Equipping, Retracting, Unequipping }
    private static readonly FieldInfo? MmuAvatarField = typeof(KittenRenderable).GetField(
        "_characterAvatar", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class KittenSaber
    {
        internal readonly BladeState Blade = new();
        internal readonly SaberAutoSwing AutoSwing = new();
        internal readonly SaberPoseController Pose;
        internal readonly SaberAudio Audio = new();
        internal ulong ConsumedPoseGeneration;
        internal bool FreshPoseThisTick, HasPreviousTip;
        internal double3 PreviousTipBody;
        internal double PreviousTipTime;
        internal float SwingSpeed;
        internal float3 Color = new(.12f, .75f, 1);
        internal float? BladeLengthOverride;
        internal float? DeferredManualDeployment;
        internal string AutoSwingDurationText = "0.5 s";
        internal SaberType Type = SaberType.Light;
        internal SaberTogglePhase TogglePhase;
        internal bool CapeEnabled;
        internal bool CapeNeedsSetup;
        internal CapeController? Cape;
        internal string? CapeFailure;
        internal bool CapeFailureLogged;
        internal bool CapeFailureLatched;
        internal CharacterAvatar? MmuAvatar;
        internal bool MmuAvatarResolved, MmuAvatarLookupFailed;
        internal bool MmuLeaseActive, MmuWasEnabled;

        internal KittenSaber(Action<KittenEva, IViewport, float4x4>? draw = null) => Pose = new(draw);

        internal void Dispose()
        {
            Cleanup(Audio.Shutdown, "Kitten audio");
            Cleanup(Audio.Dispose, "Kitten audio");
            Cleanup(Pose.Dispose, "Kitten pose");
            ReleaseOverlayMmu(this);
            if (Cape is { } cape) Cleanup(cape.Dispose, "Kitten cape");
            Cape = null; CapeNeedsSetup = false;
            Blade.Reset();
            AutoSwing.Reset();
            AutoSwingDurationText = "0.5 s";
            DeferredManualDeployment = null;
            TogglePhase = SaberTogglePhase.None;
        }
    }

    // The UI reads this settled snapshot and enqueues requests. It never scans the
    // live system or changes avatar/audio state while the solver is in flight.
    private IReadOnlyList<KittenEva> OverlayKittens => _overlayKittens;

    private float3 DefaultSaberColor => ClampSaberColor(new(_settings.ColorR, _settings.ColorG, _settings.ColorB));
    private float3 Color => _activeSaber?.Color ?? DefaultSaberColor;
    private float SaberLengthFor(KittenSaber saber) => saber.BladeLengthOverride ?? _settings.BladeLength;
    private float VisibleBladeLength(KittenSaber saber) => saber.Pose.ClashHeld
        ? saber.Pose.ClashHeldBladeLength : saber.Blade.Deployment * SaberLengthFor(saber);
    private float3 SaberColorFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? saber.Color : DefaultSaberColor;
    private SaberType SaberTypeFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? saber.Type : SaberType.Light;
    private float SaberLengthFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? SaberLengthFor(saber) : _settings.BladeLength;
    private float? SaberLengthOverrideFor(KittenEva kitten) => _kittenSabers.TryGetValue(kitten, out var saber) ? saber.BladeLengthOverride : null;
    private KittenEva? AppearanceKitten => !Program.EditorFlag && Program.ControlledVehicle is KittenEva { IsDisposed: false } kitten
        && _currentKittens.Contains(kitten) ? kitten : null;

    private void SetKittenSaberColor(KittenEva kitten, float3 color)
    {
        if (!kitten.IsDisposed && _currentKittens.Contains(kitten) && _kittenSabers.TryGetValue(kitten, out var saber))
        {
            saber.Color = ClampSaberColor(color);
            RememberKittenChoices(kitten, saber, revalidate: true);
        }
    }

    private void SetKittenSaberType(KittenEva kitten, SaberType type)
    {
        if (!kitten.IsDisposed && _currentKittens.Contains(kitten) && _kittenSabers.TryGetValue(kitten, out var saber))
        {
            saber.Type = type == SaberType.Dark ? SaberType.Dark : SaberType.Light;
            RememberKittenChoices(kitten, saber, revalidate: true);
        }
    }

    private void SetKittenSaberLength(KittenEva kitten, float length)
    {
        if (!float.IsFinite(length) || length < .25f || length > SaberLimits.MaximumBladeLength
            || kitten.IsDisposed || !_currentKittens.Contains(kitten) || !_kittenSabers.TryGetValue(kitten, out var saber)) return;
        saber.BladeLengthOverride = length;
        RememberKittenChoices(kitten, saber, revalidate: true);
    }

    private void UseDefaultKittenSaberLength(KittenEva kitten)
    {
        if (kitten.IsDisposed || !_currentKittens.Contains(kitten) || !_kittenSabers.TryGetValue(kitten, out var saber)) return;
        saber.BladeLengthOverride = null;
        RememberKittenChoices(kitten, saber, revalidate: true);
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
        if (saber?.DeferredManualDeployment is { } deferred) enabled = deferred > 0;
        // Reflect the latest click while waiting for the next settled handoff.
        foreach (var request in _kittenSaberRequests)
            if (ReferenceEquals(request.Kitten, kitten))
                enabled = request.ManualDeployment is { } deployment ? deployment > 0 : request.Enabled;
        return enabled;
    }

    private bool? PendingSaberEnabledIntent(KittenEva kitten)
    {
        for (int i = _kittenSaberRequests.Count - 1; i >= 0; i--)
        {
            var request = _kittenSaberRequests[i];
            if (!ReferenceEquals(request.Kitten, kitten)) continue;
            return request.ManualDeployment is { } deployment ? deployment > 0 : request.Enabled;
        }
        if (_kittenSabers.TryGetValue(kitten, out var saber) && saber.DeferredManualDeployment is { } deferred)
            return deferred > 0;
        return null;
    }

    private void QueueKittenSaber(KittenEva kitten, bool enabled)
    {
        if (!_ready || _disposed || _fault != null || Program.EditorFlag || kitten.IsDisposed || !_currentKittens.Contains(kitten)) return;
        // A discrete equipment action supersedes any currently captured overlay
        // gesture for this same kitten, before a newer press can be started.
        if (ReferenceEquals(kitten, _saberDeploymentDragOwner)) CancelSaberDeploymentDrag();
        if (ReferenceEquals(kitten, _autoSwingDragOwner)) CancelSaberAutoSwingDrag();
        PublishKittenSaberRequest(new(kitten, enabled));
    }

    private void QueueKittenBladeDeployment(KittenEva kitten, float deployment, bool rememberIntent = false)
    {
        if (!_ready || _disposed || _fault != null || Program.EditorFlag || kitten.IsDisposed
            || !_currentKittens.Contains(kitten) || !float.IsFinite(deployment)) return;
        PublishKittenSaberRequest(new(kitten, deployment > 0, Math.Clamp(deployment, 0, 1), rememberIntent));
    }

    private void PublishKittenSaberRequest(KittenSaberRequest request)
    {
        // Requests from another kitten commute. Coalesce consecutive manual cursor
        // updates for this kitten while leaving every toggle/manual boundary ordered.
        for (int i = _kittenSaberRequests.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_kittenSaberRequests[i].Kitten, request.Kitten)) continue;
            var previous = _kittenSaberRequests[i];
            if (previous.ManualDeployment is not null && request.ManualDeployment is not null)
            {
                _kittenSaberRequests[i] = request with { RememberIntent = request.RememberIntent || previous.RememberIntent };
                return;
            }
            break;
        }
        _kittenSaberRequests.Add(request);
        if (_kittenSaberRequests.Count > Math.Max(1, _currentKittens.Count) * PendingRequestsPerKittenLimit)
            CompactKittenSaberRequests();
    }

    private void CompactKittenSaberRequests()
    {
        if (_kittenSaberRequests.Count < 2) return;
        var latest = new Dictionary<KittenEva, (int Last, int LastManual)>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _kittenSaberRequests.Count; i++)
        {
            var request = _kittenSaberRequests[i];
            int lastManual = request.ManualDeployment is null ? -1 : i;
            if (latest.TryGetValue(request.Kitten, out var previous))
            {
                if (lastManual < 0) lastManual = previous.LastManual;
            }
            latest[request.Kitten] = (i, lastManual);
        }
        var keep = new bool[_kittenSaberRequests.Count];
        foreach (var item in latest.Values)
        {
            keep[item.Last] = true;
            if (_kittenSaberRequests[item.Last].ManualDeployment is null && item.LastManual >= 0)
                keep[item.LastManual] = true;
        }
        int write = 0;
        for (int read = 0; read < _kittenSaberRequests.Count; read++)
            if (keep[read]) _kittenSaberRequests[write++] = _kittenSaberRequests[read];
        if (write < _kittenSaberRequests.Count) _kittenSaberRequests.RemoveRange(write, _kittenSaberRequests.Count - write);
    }

    private void QueueControlledSaberToggle()
    {
        if (AppearanceKitten is { } kitten) QueueKittenSaber(kitten, !SaberEnabledFor(kitten));
    }

    private static string KittenLabel(KittenEva kitten) => kitten.Id;

    private void RefreshOverlayKittens()
    {
        BeginKittenProfileRefresh();
        _overlayKittens.Clear();
        _currentKittens.Clear();
        if (!Program.EditorFlag && Universe.CurrentSystem is { } system)
            foreach (var item in system.All.AsSpan())
                if (item is KittenEva { IsDisposed: false } kitten)
                {
                    _overlayKittens.Add(kitten);
                    _currentKittens.Add(kitten);
                    // Seed color at first sight, without equipping or binding a pose.
                    RefreshOverlayMmu(kitten, GetKittenSaber(kitten));
                }

        _removedKittens.Clear();
        foreach (var pair in _kittenSabers)
            if (!_currentKittens.Contains(pair.Key)) _removedKittens.Add(pair.Key);
        foreach (var kitten in _removedKittens)
        {
            // Boarding destroys/deregisters the EVA object. Release its processor,
            // MMU override and owned channels even if it was not being controlled.
            var saber = _kittenSabers[kitten];
            if (ReferenceEquals(kitten, _saberDeploymentDragOwner)) CancelSaberDeploymentDrag();
            if (ReferenceEquals(kitten, _autoSwingDragOwner)) CancelSaberAutoSwingDrag();
            RemoveClashOwner(kitten);
            ForgetKittenBinding(kitten);
            RemoveCapeOwner(kitten);
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
            RestoreKittenChoices(kitten, saber);
        }
        return saber;
    }

    private void SetControlledKitten(KittenEva? kitten)
    {
        CancelSaberDeploymentDrag();
        CancelSaberAutoSwingDrag();
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
        _kittenIntentWrites.Clear();
        for (int i = 0; i < _kittenSaberRequests.Count; i++)
        {
            var request = _kittenSaberRequests[i];
            if (!_currentKittens.Contains(request.Kitten) || request.Kitten.IsDisposed
                || !_kittenSabers.ContainsKey(request.Kitten)) continue;
            bool intent = request.ManualDeployment is { } deployment ? deployment > 0 : request.Enabled;
            if (_kittenIntentWrites.ContainsKey(request.Kitten)) _kittenIntentWrites[request.Kitten] = intent;
            else if (request.RememberIntent) _kittenIntentWrites.Add(request.Kitten, intent);
        }
        for (int requestIndex = 0; requestIndex < _kittenSaberRequests.Count; requestIndex++)
        {
            var request = _kittenSaberRequests[requestIndex];
            if (_fault != null || !_currentKittens.Contains(request.Kitten) || request.Kitten.IsDisposed
                || !_kittenSabers.TryGetValue(request.Kitten, out var saber)) continue;
            if (ReferenceEquals(saber, _activeSaber) && (SliceWorkActive || _selectingSlice))
                CancelAssistedSlice(request.ManualDeployment is null
                    ? "Slice cancelled by manual control." : "Slice cancelled by blade deployment control.");
            if (request.ManualDeployment is { } deployment)
            {
                if (_kittenIntentWrites.ContainsKey(request.Kitten))
                    _kittenIntentWrites[request.Kitten] = deployment > 0;
                if (saber.Pose.ClashHeld)
                {
                    // Keep the visible blade frozen for the clash window. Only the latest
                    // requested level is retained; an ordinary toggle clears this slot.
                    bool deferredChanged = saber.DeferredManualDeployment != deployment;
                    saber.DeferredManualDeployment = deployment;
                    if (_kittenIntentWrites.ContainsKey(request.Kitten))
                        _kittenIntentWrites[request.Kitten] = deployment > 0;
                    else if (deferredChanged)
                        _kittenIntentWrites.Add(request.Kitten, deployment > 0);
                    continue;
                }
                saber.DeferredManualDeployment = null;
                if (ApplyManualDeploymentNow(request.Kitten, saber, deployment))
                    _kittenIntentWrites[request.Kitten] = deployment > 0;
                continue;
            }
            // A normal toggle supersedes an uncommitted slider target.
            saber.DeferredManualDeployment = null;
            if (_kittenIntentWrites.ContainsKey(request.Kitten))
                _kittenIntentWrites[request.Kitten] = request.Enabled;
            if (request.Enabled)
            {
                saber.Blade.Equip(true);
                if (!ReferenceEquals(saber.Pose.Kitten, request.Kitten)) saber.Pose.Bind(request.Kitten);
                saber.TogglePhase = SaberTogglePhase.Equipping;
            }
            else
            {
                // Keep the hilt, pose and channels alive through retraction and
                // arm lowering. A later enable reverses from the current pose/length.
                if (ReferenceEquals(request.Kitten, _autoSwingDragOwner)) CancelSaberAutoSwingDrag();
                saber.Pose.ClearClash();
                DisableSaberAutoSwing(saber);
                saber.Blade.SetIgnited(false);
                if (saber.TogglePhase != SaberTogglePhase.Unequipping)
                    saber.TogglePhase = saber.Blade.Equipped ? SaberTogglePhase.Retracting : SaberTogglePhase.None;
                if (ReferenceEquals(saber, _activeSaber)) ResetOverlaySwing();
            }
        }
        foreach (var intent in _kittenIntentWrites)
            if (_currentKittens.Contains(intent.Key) && _kittenSabers.TryGetValue(intent.Key, out var saber))
                RememberKittenChoices(intent.Key, saber, revalidate: true, enabled: intent.Value);
        _kittenIntentWrites.Clear();
        _kittenSaberRequests.Clear();
    }

    private bool ApplyManualDeploymentNow(KittenEva kitten, KittenSaber saber, float deployment)
    {
        if (!saber.Blade.Equipped) saber.Blade.Equip(true);
        if (!ReferenceEquals(saber.Pose.Kitten, kitten)) saber.Pose.Bind(kitten);
        saber.TogglePhase = SaberTogglePhase.None;
        bool changed = !saber.Blade.ManualDeployment || saber.Blade.Deployment != deployment;
        if (!saber.Blade.HoldDeployment(deployment)) return false;
        return changed;
    }

    private void ConfigureSaberPose(SaberPoseController pose)
    {
        pose.GripOffsetFromChestCm = new(_settings.GripX, _settings.GripY, _settings.GripZ);
        pose.BladeDirectionModel = new(_settings.AimX, _settings.AimY, _settings.AimZ);
        pose.FingerCurlRadians = _settings.FingerCurl;
        // The runtime-level lease hides all EVA MMUs while enabled. Keep pose
        // ownership off so stowing one saber cannot restore the shared attachment.
        pose.HideMmuWhenEquipped = false;
    }

    private void RefreshOverlayMmu(KittenEva kitten, KittenSaber saber)
    {
        // The overlay toggle, not HUD visibility, focus or viewport state, owns
        // this cosmetic lease. Faulted/disposed runtimes must not reacquire it.
        if (!_enabled || _disposed || _fault != null)
        {
            ReleaseOverlayMmu(saber);
            return;
        }

        if (!saber.MmuAvatarResolved)
        {
            saber.MmuAvatarResolved = true;
            try
            {
                saber.MmuAvatar = MmuAvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;
                saber.MmuAvatarLookupFailed = saber.MmuAvatar is null;
            }
            catch
            {
                // A changed private field or inaccessible avatar is isolated to
                // this record and is not retried on every settled frame.
                saber.MmuAvatarLookupFailed = true;
            }
        }

        if (saber.MmuAvatarLookupFailed || saber.MmuAvatar is not { } avatar) return;
        try
        {
            if (!saber.MmuLeaseActive)
            {
                saber.MmuWasEnabled = avatar.Attachments.Mmu.Enabled;
                saber.MmuLeaseActive = true;
            }
            // Reassert the cosmetic state if native or another mod re-enables it.
            if (avatar.Attachments.Mmu.Enabled) avatar.Attachments.Mmu.Enabled = false;
        }
        catch (Exception ex)
        {
            saber.MmuAvatarLookupFailed = true;
            ReleaseOverlayMmu(saber);
            Report("Could not hide an EVA MMU", ex);
        }
    }

    private static void ReleaseOverlayMmu(KittenSaber saber)
    {
        if (!saber.MmuLeaseActive) return;
        try
        {
            if (saber.MmuAvatar is { } avatar)
                avatar.Attachments.Mmu.Enabled = saber.MmuWasEnabled;
        }
        catch (Exception ex)
        {
            Report("Could not restore an EVA MMU", ex);
        }
        finally
        {
            saber.MmuLeaseActive = false;
        }
    }

    // Shared by controlled and background kittens. Each completed stage is
    // presented for a frame before the next stage starts; pause advances none.
    private void AdvanceKittenSaber(KittenSaber saber, double playerDt)
    {
        var pose = saber.Pose;
        bool advancing = double.IsFinite(playerDt) && playerDt > 0;
        double visualDt = pose.ClashHeld ? 0 : playerDt;
        if (!pose.ClashHeld && saber.DeferredManualDeployment is { } deferred
            && pose.Kitten is { IsDisposed: false } deferredKitten)
        {
            saber.DeferredManualDeployment = null;
            if (ApplyManualDeploymentNow(deferredKitten, saber, deferred))
                RememberKittenChoices(deferredKitten, saber, revalidate: true, enabled: deferred > 0);
        }
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
        pose.Update(visualDt, saber.Blade.Equipped && saber.TogglePhase != SaberTogglePhase.Unequipping);
        saber.FreshPoseThisTick = pose.MainPoseGeneration != saber.ConsumedPoseGeneration && pose.HasFreshBodyFrame;
        saber.ConsumedPoseGeneration = pose.MainPoseGeneration;
        // Pose availability can change while moving or grabbing a ladder. It
        // suspends presentation, never the player's ignition/equipment intent.
        // Pending equip resumes when the hand pose is available again.
        saber.Blade.Advance(visualDt);
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
            AdvanceSaberAutoSwing(saber, playerDt);
            AdvanceKittenSaber(saber, playerDt);
            var pose = saber.Pose;
            if (saber.Blade.Equipped && saber.FreshPoseThisTick && pose.TryGetBodyFrame(out var emitter))
                _renderer.SetEmitter(pair.Key, emitter, VisibleBladeLength(saber), saber.Color);
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
            float length = VisibleBladeLength(saber);
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
        CancelSaberDeploymentDrag();
        CancelSaberAutoSwingDrag();
        ReleaseProfileBindings();
        DismissKittenColorPicker();
        foreach (var pair in _kittenSabers)
        {
            DisposeKittenCape(pair.Key, pair.Value, clearFailure: true);
            pair.Value.AutoSwing.Reset();
            pair.Value.Dispose();
        }
        _capeControllers.Clear();
        _capeOwners.Clear();
        _capePendingCreationCount = 0;
        _capeRuntimeFaulted = _capeRuntimeFaultLogged = false;
        _capeFaultCleanupPending = _capePendingCleanup = false;
        _capeRoundRobinCursor = 0;
        _kittenSabers.Clear(); _kittenSaberRequests.Clear(); _overlayKittens.Clear();
        _kittenIntentWrites.Clear();
        _currentKittens.Clear(); _removedKittens.Clear(); _activeSaber = null;
        _nextKittenColor = 0;
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _blade.Reset(); _pose.Unbind(); _audio.Reset();
    }
}
