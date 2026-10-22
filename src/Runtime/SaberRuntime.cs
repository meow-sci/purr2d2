using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Autopilot;
using Purr2D2.Audio;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;
using Purr2D2.Rendering;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime : IDisposable
{
    private enum Command { Equip, Ignite, Swing, CaptureRing, AlignRing, StartCutOrbit, AbortAutopilot, MatchVelocity, NudgeRingBack, NudgeRingForward, SetTarget, PickRing }
    private readonly record struct Request(KittenEva Owner, Command Value, Vehicle? Target = null, double3? Point = null);
    private readonly Queue<Request> _commands = new();
    private readonly BladeState _blade = new();
    private readonly SaberPoseController _pose = new();
    private readonly SaberRenderer _renderer = new();
    private readonly SaberAudio _audio = new();
    private readonly SaberCutter _cutter = new();
    private readonly ArbitrarySlicer _slicer = new();
    private readonly EvaCuttingAutopilot _autopilot = new();
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
    private bool _autopilotBladeWasEquipped, _autopilotBladeWasOn, _autopilotOwnsBlade;
    private string? _fault;
    private float3 Color => new(_settings.ColorR, _settings.ColorG, _settings.ColorB);
    private float Length => _blade.Deployment * _settings.BladeLength;

    internal SaberRuntime(string directory)
    {
        _settingsPath = Path.Combine(directory, "settings.json");
        try { _settings = SaberSettings.Load(_settingsPath); }
        catch (Exception ex) { _settings = new(); Report("Could not read preferences; using defaults", ex); }
        _pose.Draw += DrawSaber;
        _cutter.Scar += _renderer.AddScar;
        _cutter.SliceHandler = (part, plane) => _slicer.Slice(part, plane, _playerTime);
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
            _playerTime += double.IsFinite(dtPlayer) ? Math.Clamp(dtPlayer, 0, .1) : 0;
            var selected = !Program.EditorFlag ? Program.ControlledVehicle as KittenEva : null;
            if (selected?.IsDisposed == true) selected = null;
            if (!ReferenceEquals(_system, Universe.CurrentSystem) || !ReferenceEquals(_owner, selected))
            {
                ResetWorld();
                _system = Universe.CurrentSystem;
                _owner = selected;
            }
            while (_commands.TryDequeue(out var item))
            {
                if ((_fault != null && item.Value != Command.AbortAutopilot) || !ReferenceEquals(item.Owner, _owner) || _owner == null) continue;
                switch (item.Value)
                {
                    case Command.Equip:
                        if (_autopilot.Active) StopAutopilot("Autopilot released for manual saber control.");
                        _blade.Equip(!_blade.Equipped);
                        if (_blade.Equipped) _pose.Bind(_owner);
                        else { _pose.Unbind(); _audio.Stop(); _cutter.Reset(); }
                        break;
                    case Command.Ignite:
                        _blade.SetIgnited(!_blade.TargetOn);
                        break;
                    case Command.Swing:
                        if (_blade.Equipped && Universe.GetSimulationSpeed() > 0 && Universe.GetSimulationSpeed() <= 1)
                            _pose.Swing();
                        break;
                    case Command.SetTarget:
                        if (item.Target is { IsDisposed: true } || item.Target is KittenEva || item.Target?.IsEditedVehicle == true) break;
                        StopAutopilot("Target changed; manual MMU control restored.");
                        _autopilot.InvalidateCapture();
                        _owner.SetTarget(item.Target);
                        break;
                    case Command.PickRing:
                        if (item.Point.HasValue && ReferenceEquals(_owner.Target, item.Target))
                            _autopilot.Capture(_owner, _settings, item.Point.Value);
                        break;
                    case Command.CaptureRing:
                        _autopilot.Capture(_owner, _settings);
                        break;
                    case Command.NudgeRingBack:
                    case Command.NudgeRingForward:
                        _autopilot.Nudge(_owner, _settings, item.Value == Command.NudgeRingBack ? -.25 : .25);
                        break;
                    case Command.MatchVelocity:
                        _autopilot.BeginMatchVelocity(_owner);
                        break;
                    case Command.AlignRing:
                    {
                        bool wasEquipped = _blade.Equipped, wasOn = _blade.TargetOn;
                        if (_autopilot.BeginAlign(_owner, _settings))
                        {
                            _autopilotBladeWasEquipped = wasEquipped;
                            _autopilotBladeWasOn = wasOn;
                            _autopilotOwnsBlade = true;
                            EnsureAutopilotBlade();
                        }
                        break;
                    }
                    case Command.StartCutOrbit:
                        if (_autopilot.BeginCut(StructuralCutCount)) EnsureAutopilotBlade();
                        break;
                    case Command.AbortAutopilot:
                        StopAutopilot("Autopilot aborted; manual MMU control restored.");
                        break;
                }
            }
            SimStep applied = Universe.GetLastSimStep();
            bool newStep = !_hasConsumedTime || applied.NextTime != _consumedTime;
            _hasConsumedTime = true;
            _consumedTime = applied.NextTime;
            bool normalSpeed = Universe.GetSimulationSpeed() > 0 && Universe.GetSimulationSpeed() <= 1;
            double simulationDt = newStep && normalSpeed && double.IsFinite(applied.DeltaTime) && applied.DeltaTime <= .15
                ? Math.Clamp(applied.DeltaTime, 0, .1) : 0;
            if (_autopilot.Active)
            {
                if (!normalSpeed)
                {
                    _autopilot.Suspend();
                }
                else
                {
                    double3 bladeDirectionBody = SaberPoseMath.ModelDirectionToBody(
                        new float3(_settings.AimX, _settings.AimY, _settings.AimZ));
                    if (_pose.TryGetBladeSegment(1, out double3 bladeBase, out double3 bladeTip))
                        bladeDirectionBody = double3.NormalizeOrZero(bladeTip - bladeBase);
                    AutopilotResult result = _autopilot.Update(_settings, simulationDt,
                        _blade.Deployment, StructuralCutCount, _pose.HasFreshBodyFrame, bladeDirectionBody);
                    if (_autopilot.WantsEquipped) EnsureAutopilotBlade();
                    if (_autopilotOwnsBlade) _blade.SetIgnited(_autopilot.WantsIgnited);
                    if (result is AutopilotResult.Completed or AutopilotResult.Aborted)
                        RestoreAutopilotBlade();
                }
            }
            _pose.GripOffsetFromChestCm = new float3(_settings.GripX, _settings.GripY, _settings.GripZ);
            _pose.BladeDirectionModel = new float3(_settings.AimX, _settings.AimY, _settings.AimZ);
            _pose.FingerCurlRadians = _settings.FingerCurl;
            _pose.HideMmuWhenEquipped = _settings.HideMmu;
            _pose.Update(normalSpeed ? Math.Clamp(dtPlayer, 0, .1) : 0, _blade.Equipped);
            // KSA's FrameNumber does not advance when rendering exits early. A distinct main
            // pose publication, consumed once here, also handles minimized/rebuilding windows.
            _freshPoseThisTick = _pose.MainPoseGeneration != _consumedPoseGeneration && _pose.HasFreshBodyFrame;
            _consumedPoseGeneration = _pose.MainPoseGeneration;
            if (!_pose.Eligible) _blade.SetIgnited(false);
            _blade.Advance(simulationDt);
            _renderer.Update(_playerTime);
            RuntimeFragmentAssets.Update(_playerTime);
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBodyFrame(out var emitter))
            {
                _renderer.SetEmitter(_owner, emitter, Length, Color);
                // Geometry is a coherent snapshot from the previous render; never sweep a hidden/stale paw.
                if (_fault == null && _settings.CuttingEnabled && simulationDt > 0 && Length > .02f &&
                    _pose.HasFreshBodyFrame &&
                    _pose.TryGetBladeSegment(Length, out var start, out var end))
                {
                    ConfigureCutter();
                    if (_autopilot.Active && !_autopilot.AllowsCutting) _cutter.Enabled = false;
                    _cutter.Update(_owner, start, end, simulationDt, _playerTime);
                }
                else _cutter.Suspend();
            }
            else
            {
                _renderer.SetEmitter(null, default, 0, Color);
                _cutter.Suspend();
                if (_owner != null && _blade.Equipped) _audio.Suspend();
                else _audio.Stop();
            }
        }
        catch (Exception ex)
        {
            Cleanup(() => StopAutopilot("Autopilot released after a simulation error."), "Autopilot release");
            _fault = "Saber suspended: " + ex.Message;
            Report("Simulation handoff failed", ex);
            _blade.Reset();
            _commands.Clear();
            _freshPoseThisTick = false;
            _renderer.SetEmitter(null, default, 0, Color);
            _cutter.Reset();
            _audio.Stop();
        }
    }

    // Called once inside the ImGui frame even when stock HUD visibility is disabled.
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
                else if (io.KeyAlt) Queue(Command.Swing);
                else _windowVisible = !_windowVisible;
            }
            if (_autopilot.Active && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                Queue(Command.AbortAutopilot);
            // Pauses/warp and culled kittens must not leave the last audible channel running forever.
            if (_owner != null && _blade.Equipped && _freshPoseThisTick && _pose.TryGetBladeSegment(Length, out var emitter, out _))
                _audio.Update(_owner, emitter, _blade.Deployment, _blade.TargetOn,
                    Universe.IsPaused() || Universe.GetSimulationSpeed() > 1 || !_pose.HasFreshBodyFrame,
                    _settings.Volume, _swingSpeed, _cutter.Contacts > 0 ? 1 : 0);
            else if (_owner != null && _blade.Equipped) _audio.Suspend();
            else _audio.Stop();
        }
        catch (Exception ex)
        {
            Queue(Command.AbortAutopilot);
            _fault = ex.Message;
            Report("Frame update failed", ex);
        }
    }

    private void DrawSaber(KittenEva owner, IViewport viewport, float4x4 emitterToEgo)
    {
        if (_disposed || !_blade.Equipped || !ReferenceEquals(owner, _owner)) return;
        _renderer.DrawSaber(owner, viewport, emitterToEgo, Length, Color);
        if (ReferenceEquals(viewport, Program.MainViewport) && _pose.TryGetBladeSegment(Length, out var emitter, out var tip))
        {
            double elapsed = _playerTime - _previousTipTime;
            if (_hasPreviousTip && elapsed > .001 && elapsed < .15)
                _swingSpeed = (float)Math.Clamp((tip - _previousTipBody).Length() / elapsed, 0, 15);
            else _swingSpeed = 0;
            _previousTipBody = tip; _previousTipTime = _playerTime; _hasPreviousTip = true;
            _audio.Update(owner, emitter, _blade.Deployment, _blade.TargetOn,
                Universe.IsPaused() || Universe.GetSimulationSpeed() > 1 || !_pose.Eligible,
                _settings.Volume, _swingSpeed, _cutter.Contacts > 0 ? 1 : 0);
        }
    }

    private void Queue(Command command)
    {
        if (_commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten)
            _commands.Enqueue(new(kitten, command));
    }

    private void QueueTarget(Vehicle? target)
    {
        if (_commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten)
            _commands.Enqueue(new(kitten, Command.SetTarget, target));
    }

    private void QueueRingPoint(Vehicle target, double3 point)
    {
        if (_commands.Count < 16 && Program.ControlledVehicle is KittenEva kitten)
            _commands.Enqueue(new(kitten, Command.PickRing, target, point));
    }

    private int StructuralCutCount => _cutter.SeveredConnections + _cutter.FragmentedParts + _cutter.SlicedParts;

    private void EnsureAutopilotBlade()
    {
        if (_owner == null) return;
        if (!_blade.Equipped)
        {
            _blade.Equip(true);
            _pose.Bind(_owner);
        }
    }

    private void StopAutopilot(string reason)
    {
        _autopilot.Abort(reason);
        RestoreAutopilotBlade();
    }

    private void RestoreAutopilotBlade()
    {
        if (!_autopilotOwnsBlade) return;
        _blade.SetIgnited(_autopilotBladeWasOn);
        if (!_autopilotBladeWasEquipped)
        {
            _blade.Equip(false);
            _pose.Unbind();
            _audio.Stop();
            _cutter.Reset();
        }
        _autopilotOwnsBlade = false;
    }

    internal void ResetWorld()
    {
        _autopilot.Reset();
        _autopilotOwnsBlade = false;
        _commands.Clear();
        _ringSelectionArmed = false;
        _selectedRingPointTarget = null;
        _ringSelectionStatus = "";
        _blade.Reset();
        _pose.Unbind();
        _audio.Reset();
        _cutter.Reset();
        _renderer.ClearScars();
        RuntimeFragmentAssets.ClearHeat();
        _renderer.SetEmitter(null, default, 0, new float3(.12f, .75f, 1));
        _owner = null;
        _system = null;
        _hasConsumedTime = false;
        _hasPreviousTip = false;
        _swingSpeed = 0;
        _freshPoseThisTick = false;
        _consumedPoseGeneration = _pose.MainPoseGeneration;
    }

    internal void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        Cleanup(ResetWorld, "World");
        Cleanup(_audio.Shutdown, "Audio");
        Cleanup(_renderer.Dispose, "Renderer");
        Cleanup(_pose.Dispose, "Pose");
    }

    private static void Cleanup(Action action, string component)
    {
        try { action(); }
        catch (Exception ex) { Report(component + " cleanup failed", ex); }
    }

    public void Dispose() => Shutdown();

    internal static void Report(string context, Exception ex) => Console.WriteLine($"[purr2d2] {context}: {ex}");
}
