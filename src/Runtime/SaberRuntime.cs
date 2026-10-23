using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;
using Purr2D2.Rendering;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime : IDisposable
{
    private enum Command { Equip, Ignite, SliceNearby, CancelSlice, SetTarget }
    private readonly record struct Request(KittenEva Owner, Command Value, Vehicle? Target = null);
    private readonly Queue<Request> _commands = new();
    private readonly BladeState _blade = new();
    private readonly SaberPoseController _pose = new();
    private readonly SaberRenderer _renderer = new();
    private readonly SaberAudio _audio = new();
    private readonly SaberContactProbe _contactProbe = new();
    private readonly PartSliceTargeting _targeting = new();
    private readonly ArbitrarySlicer _slicer = new();
    private readonly string _settingsPath;
    private SaberSettings _settings;
    private KittenEva? _owner;
    private object? _system;
    private UniverseTime _consumedTime;
    private bool _hasConsumedTime, _disposed, _ready;
    private bool _windowVisible = true;
    private double _playerTime;
    private double3 _previousTipBody;
    private double _previousTipTime;
    private bool _hasPreviousTip;
    private float _swingSpeed;
    private ulong _consumedPoseGeneration;
    private bool _freshPoseThisTick;
    private string? _fault;
    private float3 Color => new(_settings.ColorR, _settings.ColorG, _settings.ColorB);
    private float Length => _blade.Deployment * (_sliceBladeLength > 0 ? _sliceBladeLength : _settings.BladeLength);

    internal SaberRuntime(string directory)
    {
        _settingsPath = Path.Combine(directory, "settings.json");
        try { _settings = SaberSettings.Load(_settingsPath); }
        catch (Exception ex) { _settings = new(); Report("Could not read preferences; using defaults", ex); }
        _pose.Draw += DrawSaber;
        _targeting.SliceHandler = (part, plane) => _slicer.Commit(part, _playerTime);
        _slicer.CutSparks = _renderer.AddCutSparks;
    }

    internal void Initialize(Harmony harmony)
    {
        GameHooks.Runtime = this;
        GameHooks.Install(harmony);
        _renderer.Initialize(harmony);
        RuntimeFragmentAssets.Install(harmony);
        _audio.Initialize();
        _ready = true;
    }

    internal void BeforePhysics(double dtPlayer)
    {
        if (!_ready || _disposed) return;
        try
        {
            double playerDt = double.IsFinite(dtPlayer) ? Math.Clamp(dtPlayer, 0, .1) : 0;
            _playerTime += playerDt;
            var selected = !Program.EditorFlag ? Program.ControlledVehicle as KittenEva : null;
            if (selected?.IsDisposed == true) selected = null;
            if (!ReferenceEquals(_system, Universe.CurrentSystem) || !ReferenceEquals(_owner, selected))
            {
                ResetWorld(); _system = Universe.CurrentSystem; _owner = selected;
            }
            _slicer.PumpRetired();
            while (_commands.TryDequeue(out var item))
            {
                if ((_fault != null && item.Value != Command.CancelSlice) || !ReferenceEquals(item.Owner, _owner) || _owner == null) continue;
                if (_sliceSequence.Active && item.Value != Command.SliceNearby) CancelAssistedSlice("Slice cancelled by manual control.");
                switch (item.Value)
                {
                    case Command.SliceNearby: BeginAssistedSlice(); break;
                    case Command.CancelSlice: CancelAssistedSlice("Slice cancelled."); break;
                    case Command.Equip:
                        _blade.Equip(!_blade.Equipped);
                        if (_blade.Equipped) _pose.Bind(_owner);
                        else { _pose.Unbind(); _audio.Stop(); }
                        break;
                    case Command.Ignite: _blade.SetIgnited(!_blade.TargetOn); break;
                    case Command.SetTarget:
                        if (item.Target is { IsDisposed: true } || item.Target is KittenEva || item.Target?.IsEditedVehicle == true) break;
                        _owner.SetTarget(item.Target);
                        break;
                }
            }
            SimStep applied = Universe.GetLastSimStep();
            bool newStep = !_hasConsumedTime || applied.NextTime != _consumedTime;
            _hasConsumedTime = true; _consumedTime = applied.NextTime;
            double speed = Universe.GetSimulationSpeed();
            bool supportedSpeed = SaberLimits.SupportsSpeed(speed);
            double simulationDt = SaberLimits.AppliedDelta(newStep, speed, applied.DeltaTime);
            if (_sliceSequence.Active && newStep && supportedSpeed && (!double.IsFinite(applied.DeltaTime)
                || applied.DeltaTime < 0 || applied.DeltaTime > SaberLimits.MaximumSimulationStep))
                CancelAssistedSlice("Slice cancelled: simulation step too large. Switch to 1x and try again.");
            _pose.GripOffsetFromChestCm = new(_settings.GripX, _settings.GripY, _settings.GripZ);
            _pose.BladeDirectionModel = new(_settings.AimX, _settings.AimY, _settings.AimZ);
            _pose.FingerCurlRadians = _settings.FingerCurl;
            _pose.HideMmuWhenEquipped = _settings.HideMmu;
            _pose.Update(supportedSpeed ? playerDt : 0, _blade.Equipped);
            _freshPoseThisTick = _pose.MainPoseGeneration != _consumedPoseGeneration && _pose.HasFreshBodyFrame;
            _consumedPoseGeneration = _pose.MainPoseGeneration;
            if (!_pose.Eligible) _blade.SetIgnited(false);
            _blade.Advance(simulationDt);
            // Structural work happens only for the explicit swing. Contact is visual only.
            if (_sliceSequence.Active) UpdateAssistedSlice(playerDt, simulationDt);
            else if (_owner != null && _blade.Equipped && simulationDt > 0 && _freshPoseThisTick
                && _pose.TryGetBladeSegment(Length, out var contactBase, out var contactTip)
                && _contactProbe.TrySample(_owner, contactBase, contactTip, _playerTime, out var contact))
                _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
            _renderer.Update(_playerTime);
            RuntimeFragmentAssets.Update(_playerTime);
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBodyFrame(out var emitter))
                _renderer.SetEmitter(_owner, emitter, Length, Color);
            else
            {
                _renderer.SetEmitter(null, default, 0, Color);
                if (_owner != null && _blade.Equipped) _audio.Suspend(); else _audio.Stop();
            }
        }
        catch (Exception ex)
        {
            _fault = "Saber suspended: " + ex.Message;
            Report("Simulation handoff failed", ex);
            CancelAssistedSlice("Slice cancelled after a simulation error.");
            _blade.Reset(); _commands.Clear(); _freshPoseThisTick = false;
            _renderer.SetEmitter(null, default, 0, Color);
            _audio.Stop();
        }
    }

    internal void Frame(double dtPlayer)
    {
        if (!_ready || _disposed) return;
        try
        {
            _renderer.Prepare();
            var io = ImGui.GetIO();
            if (!io.WantTextInput && !io.WantCaptureKeyboard && ImGui.IsKeyPressed(ImGuiKey.F7, false))
            {
                if (io.KeyCtrl) Queue(Command.Equip);
                else if (io.KeyShift) Queue(Command.Ignite);
                else if (io.KeyAlt) Queue(Command.SliceNearby);
                else _windowVisible = !_windowVisible;
            }
            if (_sliceSequence.Active && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape, false)) Queue(Command.CancelSlice);
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBladeSegment(Length, out var emitter, out _))
                _audio.Update(_owner, emitter, _blade.Deployment, _blade.TargetOn,
                    !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) || !_pose.HasFreshBodyFrame,
                    _settings.Volume, _swingSpeed, _sliceSequence.Swinging ? 1 : 0);
            else if (_owner != null && _blade.Equipped) _audio.Suspend(); else _audio.Stop();
        }
        catch (Exception ex) { Queue(Command.CancelSlice); _fault = ex.Message; Report("Frame update failed", ex); }
    }

    private void DrawSaber(KittenEva owner, IViewport viewport, float4x4 emitterToEgo)
    {
        if (_disposed || !_blade.Equipped || !ReferenceEquals(owner, _owner)) return;
        _renderer.DrawSaber(owner, viewport, emitterToEgo, Length, Color);
        if (ReferenceEquals(viewport, Program.MainViewport) && _pose.TryGetBladeSegment(Length, out var emitter, out var tip))
        {
            double elapsed = _playerTime - _previousTipTime;
            _swingSpeed = _hasPreviousTip && elapsed > .001 && elapsed < .15
                ? (float)Math.Clamp((tip - _previousTipBody).Length() / elapsed, 0, 15) : 0;
            _sliceSequence.NotifyRendered();
            _previousTipBody = tip; _previousTipTime = _playerTime; _hasPreviousTip = true;
            _audio.Update(owner, emitter, _blade.Deployment, _blade.TargetOn,
                !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) || !_pose.Eligible,
                _settings.Volume, _swingSpeed, _sliceSequence.Swinging ? 1 : 0);
        }
    }

    private void Queue(Command command)
    {
        if (_commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten) _commands.Enqueue(new(kitten, command));
    }
    private void QueueTarget(Vehicle? target)
    {
        if (_commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten) _commands.Enqueue(new(kitten, Command.SetTarget, target));
    }
    private void EnsureBlade()
    {
        if (_owner == null || _blade.Equipped) return;
        _blade.Equip(true); _pose.Bind(_owner);
    }

    internal void ResetWorld()
    {
        CancelAssistedSlice(""); _commands.Clear(); _blade.Reset(); _pose.Unbind(); _audio.Reset(); _targeting.Reset();
        _renderer.ClearEffects(); _contactProbe.Reset(); RuntimeFragmentAssets.ClearHeat();
        _renderer.SetEmitter(null, default, 0, new(.12f, .75f, 1));
        _owner = null; _system = null; _hasConsumedTime = _hasPreviousTip = _freshPoseThisTick = false;
        _swingSpeed = 0; _consumedPoseGeneration = _pose.MainPoseGeneration;
    }
    internal void Shutdown()
    {
        if (_disposed) return;
        _disposed = true; _ready = false;
        Cleanup(ResetWorld, "World"); Cleanup(_slicer.Dispose, "Slice worker"); Cleanup(_audio.Shutdown, "Audio"); Cleanup(_renderer.Dispose, "Renderer"); Cleanup(_pose.Dispose, "Pose");
    }
    private static void Cleanup(Action action, string component)
    {
        try { action(); } catch (Exception ex) { Report(component + " cleanup failed", ex); }
    }
    public void Dispose() => Shutdown();
    internal static void Report(string context, Exception ex) => Console.WriteLine($"[purr2d2] {context}: {ex}");
}
