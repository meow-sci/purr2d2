using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Capes;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime : IDisposable
{
    private enum Command { SelectSlice, CutPicked, CancelSlice }
    private readonly record struct Request(KittenEva Owner, Command Value, SliceTarget? PartTarget = null, Ray? PickRay = null);
    private readonly Queue<Request> _commands = new();
    // These aliases always belong to the controlled kitten. Other partials use them
    // for explicit slicing; the records keep every equipped kitten alive on switches.
    private BladeState _blade;
    private SaberPoseController _pose;
    private readonly SaberRenderer _renderer = new();
    private SaberAudio _audio;
    private readonly SaberContactProbe _contactProbe = new();
    private readonly PartSliceTargeting _targeting = new();
    private readonly ArbitrarySlicer _slicer = new();
    private readonly string _settingsPath;
    private SaberSettings _settings;
    private readonly SaberSettingsAutosaver _settingsAutosaver;
    private KittenEva? _owner;
    private object? _system;
    private UniverseTime _consumedTime;
    private bool _hasConsumedTime, _disposed, _ready;
    private bool _windowVisible;
    private double _playerTime;
    private double3 _previousTipBody;
    private double _previousTipTime;
    private bool _hasPreviousTip;
    private float _swingSpeed;
    private ulong _consumedPoseGeneration;
    private bool _freshPoseThisTick;
    private string? _fault;
    private float Length => _blade.Deployment * _settings.BladeLength;

    internal SaberRuntime(string directory)
    {
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _settingsPath = Path.Combine(directory, "settings.json");
        try { _settings = SaberSettings.Load(_settingsPath); }
        catch (Exception ex) { _settings = new(); Report("Could not read preferences; using defaults", ex); }
        ApplySliceResourceSettings();
        _settingsAutosaver = new(_settingsPath, _settings);
        _targeting.SliceHandler = (part, plane) => _slicer.Commit(part, _playerTime);
        _slicer.CutSparks = _renderer.AddCutSparks;
    }

    internal void Initialize(Harmony harmony)
    {
        GameHooks.Runtime = this;
        GameHooks.Install(harmony);
        CapeController.Install(harmony);
        _somersaultMotion.Install(harmony);
        InitializeForce(harmony);
        _renderer.Initialize(harmony);
        RuntimeFragmentAssets.Install(harmony);
        QuietSliceQuiescence.Install(harmony);
        _audio.Initialize();
        _ready = true;
    }

    internal void BeforePhysics(double dtPlayer)
    {
        if (!_ready || _disposed) return;
        try
        {
            QuietSliceQuiescence.BeginSettledUpdate();
            QuietSliceContacts.Pump();
            if (!ApplyActivation()) { _slicer.PumpRetired(); return; }
            double playerDt = double.IsFinite(dtPlayer) ? Math.Clamp(dtPlayer, 0, .1) : 0;
            _playerTime += playerDt;
            var selected = !Program.EditorFlag ? Program.ControlledVehicle as KittenEva : null;
            if (selected?.IsDisposed == true) selected = null;
            if (!ReferenceEquals(_system, Universe.CurrentSystem))
            {
                ResetWorld(); _system = Universe.CurrentSystem;
            }
            RefreshOverlayKittens();
            if (selected != null && !_currentKittens.Contains(selected)) selected = null;
            if (!ReferenceEquals(_owner, selected)) SetControlledKitten(selected);
            ApplyManualContactReset();
            ApplyKittenSaberRequests();
            UpdateSomersault();
            UpdateForceBeforePhysics(playerDt);
            UpdateForcePose(playerDt);
            _slicer.PumpRetired();
            while (_commands.TryDequeue(out var item))
            {
                if ((_fault != null && item.Value != Command.CancelSlice) || !ReferenceEquals(item.Owner, _owner) || _owner == null) continue;
                switch (item.Value)
                {
                    case Command.SelectSlice: SelectPartToSlice(); break;
                    case Command.CutPicked: BeginAssistedSlice(item.PartTarget, item.PickRay); break;
                    case Command.CancelSlice: CancelAssistedSlice("Slice cancelled."); break;
                }
            }
            // The audited PrepareFrame handoff precedes all new draw recording.
            // Retired uploads were pumped above; growth separately drains GPU use
            // before replacing shared buffers and before the next slice upload.
            RuntimeFragmentAssets.PumpGrowthAtFrameBoundary();
            SimStep applied = Universe.GetLastSimStep();
            bool newStep = !_hasConsumedTime || applied.NextTime != _consumedTime;
            _hasConsumedTime = true; _consumedTime = applied.NextTime;
            double speed = Universe.GetSimulationSpeed();
            bool supportedSpeed = SaberLimits.SupportsSpeed(speed);
            double simulationDt = SaberLimits.AppliedDelta(newStep, speed, applied.DeltaTime);
            ConfigureSaberPose(_pose);
            ApplyOverlaySwing(playerDt);
            // Equip/ignition and swings are visual: use player time, even between solver steps.
            var activeSaber = _activeSaber ?? _idleSaber;
            AdvanceKittenSaber(activeSaber, supportedSpeed ? playerDt : 0);
            _freshPoseThisTick = activeSaber.FreshPoseThisTick;
            _consumedPoseGeneration = activeSaber.ConsumedPoseGeneration;
            // Assisted and intentional manual contact share the settled slice worker.
            if (_sliceSequence.Active) UpdateAssistedSlice(playerDt);
            UpdateManualContact(simulationDt > 0);
            if (!_overlayDrag.Held && !_manualSliceActive && (!_sliceSequence.Active || _sliceSequence.EndPresented)
                && _owner != null && _blade.Equipped && simulationDt > 0 && _freshPoseThisTick
                && _pose.TryGetBladeSegment(Length, out var contactBase, out var contactTip)
                && _contactProbe.TrySample(_owner, contactBase, contactTip, _playerTime, out var contact))
                _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
            _renderer.Update(_playerTime);
            RuntimeFragmentAssets.Update(_playerTime);
            _renderer.ClearEmitters();
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBodyFrame(out var emitter))
                _renderer.SetEmitter(_owner, emitter, Length, Color);
            else
            {
                if (_owner != null && _blade.Equipped) _audio.Suspend(); else _audio.Stop();
            }
            UpdateOtherSabers(supportedSpeed ? playerDt : 0);
            if (HasCapeWork) UpdateCapes(playerDt);
            UpdateSliceResources();
        }
        catch (Exception ex)
        {
            _fault = "Saber suspended: " + ex.Message;
            Report("Simulation handoff failed", ex);
            CancelAssistedSlice("Slice cancelled after a simulation error.");
            ResetWorld();
        }
    }

    internal void Frame(double dtPlayer)
    {
        if (!_enabled || !_ready || _disposed) return;
        try
        {
            if (!OverlayVisible) DismissKittenColorPicker();
            UpdateForceVisibility();
            _renderer.ConfigureVfx(_settings);
            _renderer.Prepare();
            var io = ImGui.GetIO();
            if ((SliceWorkActive || _selectingSlice) && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape, false)) Queue(Command.CancelSlice);
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBladeSegment(Length, out var emitter, out _))
                _audio.Update(_owner, emitter, _blade.Deployment, _blade.TargetOn,
                    !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) || !_pose.HasFreshBodyFrame,
                    _settings.Volume, swingSpeed: _swingSpeed, contact: _sliceSequence.Swinging ? 1 : 0,
                    type: SaberTypeFor(_owner));
            else if (_owner != null && _blade.Equipped) _audio.Suspend(); else _audio.Stop();
            UpdateOtherSaberAudio();
        }
        catch (Exception ex) { Queue(Command.CancelSlice); _fault = ex.Message; Report("Frame update failed", ex); }
    }

    private void DrawSaber(KittenEva owner, IViewport viewport, float4x4 emitterToEgo)
    {
        if (!_enabled || _disposed || !_kittenSabers.TryGetValue(owner, out var saber) || !saber.Blade.Equipped) return;
        float length = saber.Blade.Deployment * _settings.BladeLength;
        _renderer.DrawSaber(owner, viewport, emitterToEgo, length, saber.Color, saber.Type);
        if (ReferenceEquals(viewport, Program.MainViewport) && saber.Pose.TryGetBladeSegment(length, out var emitter, out var tip))
        {
            double elapsed = _playerTime - saber.PreviousTipTime;
            saber.SwingSpeed = saber.HasPreviousTip && elapsed > .001 && elapsed < .15
                ? (float)Math.Clamp((tip - saber.PreviousTipBody).Length() / elapsed, 0, 15) : 0;
            saber.PreviousTipBody = tip; saber.PreviousTipTime = _playerTime; saber.HasPreviousTip = true;
            bool controlled = ReferenceEquals(saber, _activeSaber);
            if (controlled)
            {
                _swingSpeed = saber.SwingSpeed;
                _sliceSequence.NotifyRendered();
                PublishManualPose(emitter, tip);
                _previousTipBody = tip; _previousTipTime = _playerTime; _hasPreviousTip = true;
            }
            saber.Audio.Update(owner, emitter, saber.Blade.Deployment, saber.Blade.TargetOn,
                !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) || !saber.Pose.Eligible,
                _settings.Volume, swingSpeed: saber.SwingSpeed, contact: controlled && _sliceSequence.Swinging ? 1 : 0,
                type: saber.Type);
        }
    }

    private void Queue(Command command)
    {
        if (_enabled && _commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten) _commands.Enqueue(new(kitten, command));
    }
    private void EnsureBlade()
    {
        if (_owner == null) return;
        if (_activeSaber is { } saber) saber.TogglePhase = SaberTogglePhase.None;
        if (_blade.Equipped) return;
        _blade.Equip(true); _pose.Bind(_owner);
    }

    internal void ResetWorld()
    {
        ResetSliceResourceRequests();
        ResetForce();
        _forcePose.Unbind();
        ResetSomersault();
        CancelAssistedSlice(""); ResetOverlaySwing(); _commands.Clear(); ResetKittenSabers(); _targeting.Reset();
        _renderer.ClearEffects(); _contactProbe.Reset(); RuntimeFragmentAssets.ClearHeat();
        _renderer.ClearEmitters();
        _owner = null; _system = null; _hasConsumedTime = _hasPreviousTip = _freshPoseThisTick = false;
        _swingSpeed = 0; _consumedPoseGeneration = _pose.MainPoseGeneration;
    }
    internal void Shutdown()
    {
        if (_disposed) return;
        if (!_settingsAutosaver.Flush(_settings))
            Console.WriteLine("[purr2d2] " + _settingsAutosaver.LastError);
        _disposed = true; _ready = false;
        Cleanup(DisposeForce, "Force capture"); Cleanup(_forcePose.Dispose, "Force pose");
        Cleanup(_somersaultMotion.Dispose, "Somersault motion"); Cleanup(_somersaultPose.Dispose, "Somersault pose");
        Cleanup(ResetWorld, "World"); Cleanup(QuietSliceContacts.RetireToNative, "Quiet slice contacts"); Cleanup(QuietSliceQuiescence.Clear, "Quiet slice modules"); Cleanup(_slicer.Dispose, "Slice worker"); Cleanup(_audio.Shutdown, "Audio"); Cleanup(_renderer.Dispose, "Renderer"); Cleanup(_pose.Dispose, "Pose");
    }
    private static void Cleanup(Action action, string component)
    {
        try { action(); } catch (Exception ex) { Report(component + " cleanup failed", ex); }
    }
    public void Dispose() => Shutdown();
    internal static void Report(string context, Exception ex) => Console.WriteLine($"[purr2d2] {context}: {ex}");
}
