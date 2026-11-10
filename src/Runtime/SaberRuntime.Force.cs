using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Rendering;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ForceController _force = new();
    private readonly ForceOverlayDrag _forceDrag = new();
    private readonly ForceRotationDrag _forceRotationDrag = new();
    private KittenEva? _forceRotationOwner;
    private CelestialSystem? _forceRotationSystem;
    private IParentBody? _forceRotationParent;
    private long _forceRotationGeneration;
    private double _forceRotationTarget;
    private bool _forceRotationPending;
    private readonly List<(double Rotation, bool AlternateAxis)> _forceRotationSegments = new();
    private readonly ForceOffsetSmoother _forceOffsetSmoother = new();
    private readonly ForceSpreadSmoother _forceSpreadSmoother = new();
    private readonly ForceCaptureLift _forceCaptureLift = new();
    private readonly ForceOverlaySliderDrag _forceSettingDrag = new();
    private readonly ForceThrowDrag _forceThrowDrag = new();
    private readonly ForceCaptureRadiusDrag _forceCaptureRadiusDrag = new();
    private readonly ForceBreakupSequence _forceBreakupSequence = new();
    private readonly ForceBreakupAudio _forceBreakupAudio = new();
    private readonly TimeProvider _forceBreakupClock = TimeProvider.System;
    private readonly Queue<ForceRequest> _forceRequests = new();
    private readonly Queue<double> _forceBreakupScrollPixels = new();
    private ForceOverlayLayout _forceLayout;
    private bool _forceLayoutAvailable, _selectingForce, _angryForce, _forceDisposed, _forceEscapeHeld;
    private KittenEva? _forcePickOwner, _forceMoveOwner, _forceSettingOwner, _forceThrowOwner, _forceCaptureRadiusOwner;
    private CelestialSystem? _forceCaptureRadiusSystem;
    private IParentBody? _forceCaptureRadiusParent;
    private KittenEva? _forceCaptureLiftOwner;
    private ForcePick? _forceHover;
    private ForceHoverPreview? _forceHoverPreview;
    private KittenEva? _forceHoverPreviewOwner;
    private CelestialSystem? _forceHoverPreviewSystem;
    private IParentBody? _forceHoverPreviewParent;
    private bool _forceHoverPreviewContextActive;
    private Vehicle? _forceHoverLabelVehicle;
    private Part? _forceHoverLabelPart;
    private ForcePick? _forceHoverLabelClutter;
    private string? _forceHoverLabelName;
    private string _forceHoverLabelText = string.Empty;
    private float2 _forceHoverLabelSize;
    private int _forceHoverLabelRadiusTenths = -1;
    private uint _forceHoverLabelFontId;
    private float _forceHoverLabelFontSize = float.NaN, _forceHoverLabelWrap = float.NaN;
    private bool _forceHoverLabelAngry, _forceHoverLabelConfirmed, _forceHoverLabelValid;
    private double3 _forceMoveOffset;
    private double _forceSpreadTarget = ForceMath.DefaultGroupSpread;
    private double _forceCaptureRadius = ForceMath.DefaultCaptureRadius;
    private int _forceCaptureRadiusLabelTenths = -1;
    private string _forceCaptureRadiusSliderLabel = "2 m";
    private long _forceCaptureGeneration;
    private long _forceCaptureRadiusGeneration;
    private bool _forceBreakupCancelPending, _forceBreakupAudioActive, _forceBreakupSplitCuePlayed;
    private bool _forceMovePending;
    private bool _forceClickPending;
    private float2 _forceClickPointer;
    private double _forceNextHoverTime;
    private ForceQuery? _forceQuery;
    private string _forceStatus = "";
    private ForceStatusSeverity _forceStatusSeverity;
    private enum ForceAction { Capture, Release, Throw }
    private readonly record struct ForceRequest(KittenEva Owner, ForceAction Action, ForcePick? Target = null,
        bool Angry = false, double AngryRadius = 10, int CaptureLimit = ForceCaptureLimits.Default,
        double ThrowSpeed = ForceMath.ThrowSpeed);
    private readonly record struct ForceQuery(KittenEva Owner, ForcePickRay Ray, bool Capture, bool Angry,
        double AngryRadius, double ClutterTolerance = .2, int CaptureLimit = ForceCaptureLimits.Default);
    private readonly record struct ForceHoverPresentation(double3 HitEgo, Vehicle? Vehicle, Part? Part,
        ForcePick? Clutter, bool Confirmed);

    internal bool ForceHolding => !_forceDisposed && _force.HasCapture;
    internal KittenEva? ForceOwner => !_forceDisposed ? _force.Owner : null;
    private bool ForcePicking => _selectingForce && ForceUiVisible;
    private bool ForceCanUse => _ready && !_disposed && !_forceDisposed && !Program.EditorFlag
        && _owner is { IsDisposed: false } && ReferenceEquals(_owner, Program.ControlledVehicle);

    private void InitializeForce(Harmony harmony)
    {
        _force.Install(harmony);
        SyncForceStatusFromController();
    }

    private void SetForceStatus(string status, ForceStatusSeverity severity)
    {
        _forceStatus = status;
        _forceStatusSeverity = severity;
    }

    private void SyncForceStatusFromController()
    {
        if (_force.StatusSeverity != ForceStatusSeverity.None)
            SetForceStatus(_force.Status, _force.StatusSeverity);
    }

    // Called only after the host joins and applies the previous simulation step.
    private void UpdateForceBeforePhysics(double dtPlayer)
    {
        if (_forceDisposed) return;
        try
        {
            // A queued release/throw owns this handoff. Cancel warmed breakup work
            // before any preparation or visible source commit can run.
            if (_forceRequests.Count != 0
                && _forceRequests.Any(request => request.Action is ForceAction.Release or ForceAction.Throw))
                RequestForceBreakupCancel();
            PrepareForceBreakupHandoff();

            // Input callbacks only publish a target. Advance the displayed offset here,
            // after workers have joined and before the Force controller follows it.
            bool ownsCapture = ForceCanUse && _force.HasCapture
                && ReferenceEquals(_forceMoveOwner, _owner) && ReferenceEquals(_force.Owner, _owner);
            if (ownsCapture)
            {
                if (_forceCaptureLift.IsActive && ReferenceEquals(_forceCaptureLiftOwner, _owner))
                {
                    double3 liftOffset = _forceCaptureLift.Advance(dtPlayer);
                    if (liftOffset != _force.GroupOffsetBody) _force.SetOffset(liftOffset);
                    _forceOffsetSmoother.Reset(liftOffset);
                    _forceMoveOffset = liftOffset;
                    _forceMovePending = false;
                    if (!_forceCaptureLift.IsActive) _forceCaptureLiftOwner = null;
                }
                else
                {
                    if (_forceMovePending)
                    {
                        _forceOffsetSmoother.SetTarget(_forceMoveOffset);
                        _forceMovePending = false;
                    }
                    double3 displayed = _forceOffsetSmoother.Advance(dtPlayer);
                    if (displayed != _force.GroupOffsetBody) _force.SetOffset(displayed);
                }
            }
            else if (!_force.HasCapture) ResetForceMovement();
            if (ForceCanUse && _force.HasCapture && ReferenceEquals(_force.Owner, _owner))
            {
                AdvanceForceSpread(dtPlayer);
            }

            // Pointer events publish intent only; rigid transforms are written here,
            // in the same settled handoff as movement, before dispatching new workers.
            if (_forceRotationPending)
            {
                if (ForceRotationGestureValid())
                    foreach (var segment in _forceRotationSegments)
                        _force.SetRotation(segment.Rotation, segment.AlternateAxis);
                else CancelForceRotationDrag();
                _forceRotationSegments.Clear();
                _forceRotationPending = false;
            }
            bool wasHolding = _force.HasCapture;
            _force.Update(_owner);
            if (_forceThrowDrag.Held && (!ForceHolding || !ForceCanUse
                || !ReferenceEquals(_forceThrowOwner, _owner) || !ReferenceEquals(_force.Owner, _owner)))
                CancelForceThrowDrag();
            if (_forceCaptureRadiusDrag.Held && !ForceCaptureRadiusGestureValid())
                CancelForceThrowDrag();
            if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
            else if (wasHolding) SetForceStatus(_force.Status, ForceStatusSeverity.None);
            if (wasHolding && !_force.HasCapture)
            {
                CancelForceThrowDrag();
                ResetForceMovement();
                ResetForceCaptureSession();
            }
            ResolveForceQuery();
            while (_forceRequests.TryDequeue(out var request))
            {
                if (!ForceCanUse || !ReferenceEquals(request.Owner, _owner) || request.Owner.IsDisposed)
                {
                    if (request.Action == ForceAction.Throw) CancelForceThrowDrag();
                    SetForceStatus("Furce request cancelled: the controlled kitten changed.", ForceStatusSeverity.None);
                    continue;
                }
                if (request.Action == ForceAction.Capture && request.Target is { } pick)
                {
                    if (_force.Capture(request.Owner, pick, request.Angry, request.AngryRadius, request.CaptureLimit))
                    {
                        ResetForceMovement();
                        ResetForceCaptureSession();
                        StartForceCaptureLift(request.Owner);
                    }
                    else if (!_force.HasCapture)
                    { ResetForceMovement(); ResetForceCaptureSession(); }
                    if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
                    else SetForceStatus(_force.Status, ForceStatusSeverity.None);
                }
                else if (ReferenceEquals(_force.Owner, request.Owner))
                {
                    CancelForceBreakupAtHandoff();
                    CancelForceThrowDrag();
                    _force.Release(request.Action == ForceAction.Throw, request.ThrowSpeed);
                    ResetForceCaptureSession();
                    if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
                    else SetForceStatus(_force.Status, ForceStatusSeverity.None);
                    ResetForceMovement();
                }
            }
            if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) _forceDrag.Release();
            ProcessForceBreakupHandoff();
        }
        catch (Exception error)
        {
            SetForceStatus("Furce suspended: " + error.Message, ForceStatusSeverity.Error);
            _forceRequests.Clear();
            ResetForceMovement();
            ResetForceGestures();
            ResetForceCaptureSession();
            try { _force.Reset(); }
            catch (Exception cleanup) { Report("Furce cleanup failed", cleanup); }
            ResetForceSpreadSmoother();
            try { ResetForceBreakupAudio(); }
            catch (Exception cleanup) { Report("Furce breakup audio cleanup failed", cleanup); }
            Report("Furce handoff failed", error);
        }
    }

    private void PrepareForceBreakupHandoff()
    {
        if (_forceBreakupSequence.Configure(_settings.ForceBreakupGroanPixels, _settings.ForceBreakupSplitPixels))
            _forceBreakupCancelPending = true;
        if (_forceBreakupCancelPending) CancelForceBreakupAtHandoff();
    }

    private void ProcessForceBreakupHandoff()
    {
        if (!ForceCanUse || !_force.HasCapture || _owner is not { } owner || !ReferenceEquals(_force.Owner, owner))
        {
            CancelForceBreakupAtHandoff();
            return;
        }

        while (_forceBreakupScrollPixels.TryDequeue(out double pixels))
        {
            var input = _forceBreakupSequence.ApplyScroll(pixels, ForceBreakupNow, _force.CanBreakup);
            if (input.StageOneCancelled)
            {
                _force.CancelBreakup();
                if (_forceBreakupAudioActive) ResetForceBreakupAudio();
                _forceBreakupSplitCuePlayed = false;
            }
            if (input.StageOneStarted)
            {
                if (!_force.BeginBreakup(owner, _settings.ForceBreakupPieceLimit))
                {
                    _forceBreakupSequence.Reset();
                    if (_forceBreakupAudioActive) ResetForceBreakupAudio();
                    _forceBreakupSplitCuePlayed = false;
                    SyncForceStatusFromController();
                    continue;
                }
                _forceBreakupSplitCuePlayed = false;
                var emitter = _force.TryGetPoseTargetBody(owner, out var target) ? target : double3.Zero;
                PrimeForceBreakupAudio(owner, emitter);
                _forceBreakupAudio.PlayGroan(owner, emitter, _settings.Volume);
                _forceBreakupAudioActive = _forceBreakupAudio.HasActiveChannels;
            }
        }

        if (_forceBreakupSequence.StageOneActive && _force.BreakupActive)
        {
            var progress = _force.AdvanceBreakup(owner, _forceBreakupSequence.SplitRequested,
                _forceBreakupSequence.MinimumDelayElapsed(ForceBreakupNow));
            if (progress.SourceCommitted && !_forceBreakupSplitCuePlayed)
            {
                var emitter = _force.TryGetPoseTargetBody(owner, out var target) ? target : double3.Zero;
                PrimeForceBreakupAudio(owner, emitter);
                _forceBreakupAudio.PlayBreak(owner, emitter, _settings.Volume);
                _forceBreakupAudioActive = _forceBreakupAudio.HasActiveChannels;
                _forceBreakupSplitCuePlayed = true;
            }
            if (progress.Failed)
            {
                _forceBreakupSequence.Reset();
                if (_forceBreakupAudioActive) ResetForceBreakupAudio();
                _forceBreakupSplitCuePlayed = false;
            }
            else if (progress.Completed)
            {
                _forceBreakupSequence.Reset();
                _forceBreakupSplitCuePlayed = false;
            }
        }
        else if (_force.BreakupActive)
        {
            // The gesture crossed back below stage one or its start was invalidated.
            _force.CancelBreakup();
            _forceBreakupSplitCuePlayed = false;
        }

        UpdateForceBreakupAudio(owner);
        if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
    }

    private void ResetForceCaptureSession()
    {
        _forceCaptureGeneration = unchecked(_forceCaptureGeneration + 1);
        _forceCaptureRadius = ForceMath.DefaultCaptureRadius;
        _forceCaptureRadiusOwner = null;
    }

    private void UpdateForceCaptureRadiusLabels(double radius)
    {
        if (!double.IsFinite(radius)) return;
        int tenths = (int)Math.Clamp(Math.Round(radius * 10, MidpointRounding.AwayFromZero), 20, 500);
        if (_forceCaptureRadiusLabelTenths == tenths) return;
        _forceCaptureRadiusLabelTenths = tenths;
        _forceCaptureRadiusSliderLabel = (tenths / 10d).ToString("0.#") + " m";
    }

    private void SetForceCaptureRadiusGestureOwner(KittenEva owner)
    {
        _forceCaptureRadiusOwner = owner;
        _forceCaptureRadiusSystem = owner.System;
        _forceCaptureRadiusParent = owner.Parent;
        _forceCaptureRadiusGeneration = _forceCaptureGeneration;
    }

    private bool ForceCaptureRadiusGestureValid(bool requirePointer = false, float2 pointer = default)
    {
        if (!_forceCaptureRadiusDrag.Held || !ForceUiVisible || Program.IsModalOpen()
            || Program.HasMouseInputLatch || !HudApplicationHasFocus || !ForceCanUse
            || !_forceLayoutAvailable || !_forceCaptureRadiusDrag.Matches(_forceLayout)
            || _owner is not { } owner || !ReferenceEquals(_forceCaptureRadiusOwner, owner)
            || !ReferenceEquals(owner.System, _forceCaptureRadiusSystem)
            || !ReferenceEquals(owner.Parent, _forceCaptureRadiusParent)
            || _forceCaptureRadiusGeneration != _forceCaptureGeneration
            || (requirePointer && (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y)))) return false;

        return !ForceHolding;
    }

    private void UpdateForceBreakupAudio(KittenEva owner)
    {
        if (!_forceBreakupAudioActive) return;
        if (!ForceHolding || !ReferenceEquals(_force.Owner, owner))
        {
            ResetForceBreakupAudio();
            return;
        }
        var emitter = _force.TryGetPoseTargetBody(owner, out var target) ? target : double3.Zero;
        _forceBreakupAudio.Update(owner, emitter, _settings.Volume,
            paused: !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()));
        _forceBreakupAudioActive = _forceBreakupAudio.HasActiveChannels;
    }

    // Apply pause and spatial state before Play starts a new one-shot. The audio
    // object uses the most recent pause state when creating its channel.
    private void PrimeForceBreakupAudio(KittenEva owner, double3 emitter)
    {
        _forceBreakupAudio.Update(owner, emitter, _settings.Volume,
            paused: !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()));
        _forceBreakupAudioActive = _forceBreakupAudio.HasActiveChannels;
    }

    private void ResetForceBreakupAudio()
    {
        _forceBreakupAudio.Reset();
        _forceBreakupAudioActive = false;
    }

    private double ForceBreakupNow => _forceBreakupClock.GetTimestamp()
        / (double)_forceBreakupClock.TimestampFrequency;

    private void RequestForceBreakupCancel()
    {
        _forceBreakupScrollPixels.Clear();
        _forceBreakupSequence.Reset();
        _forceBreakupSplitCuePlayed = false;
        _forceBreakupCancelPending = true;
    }

    private void CancelForceBreakupAtHandoff()
    {
        _forceBreakupScrollPixels.Clear();
        _forceBreakupSequence.Reset();
        if (_force.BreakupActive) _force.CancelBreakup();
        if (_forceBreakupAudioActive) ResetForceBreakupAudio();
        _forceBreakupSplitCuePlayed = false;
        _forceBreakupCancelPending = false;
    }

    // Clutter queries read live Bepu bodies. Only the settled phase may resolve
    // a frame's immutable ray snapshot; UI callbacks never touch that simulation.
    private void ResolveForceQuery()
    {
        var query = _forceQuery;
        _forceQuery = null;
        if (query is not { } pending || !ForcePicking || !ForceCanUse || KittenColorPickerContainsPointer(ImGui.GetIO().MousePos)
            || !HudApplicationHasFocus || Program.IsModalOpen() || !ReferenceEquals(pending.Owner, _owner)
            || !ReferenceEquals(_forcePickOwner, _owner)) return;
        try
        {
            ForceTargeting.TryPick(pending.Owner, pending.Ray, out _forceHover, out var reason, pending.ClutterTolerance);
            if (pending.Capture && _forceHover == null)
                SetForceStatus(string.IsNullOrWhiteSpace(reason) ? "Furce could not find a target." : reason,
                    ForceStatusSeverity.Warning);
            if (pending.Capture && _forceHover != null && _forceRequests.Count < 32)
            {
                _forceRequests.Enqueue(new(pending.Owner, ForceAction.Capture, _forceHover, pending.Angry, pending.AngryRadius, pending.CaptureLimit));
                SetForceStatus("Capturing " + _forceHover.DisplayName + "...", ForceStatusSeverity.None);
                CancelForcePicker();
            }
        }
        catch (Exception error)
        {
            SetForceStatus("Furce picker failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            Report("Furce picker failed", error);
        }
    }

    private void RefreshForceHoverPreview()
    {
        var io = ImGui.GetIO();
        if (!ForcePicking || !ForceCanUse || _owner is not { } owner
            || !ReferenceEquals(_forcePickOwner, owner) || ForceHolding
            || _forceCaptureRadiusDrag.Held || _forceThrowDrag.Held
            || Program.IsModalOpen() || Program.HasMouseInputLatch || !HudApplicationHasFocus
            || io.WantCaptureMouse || io.WantTextInput || !_forceLayoutAvailable
            || !CursorTarget.IsHitTestViewport(Program.MainViewport))
        {
            ClearForceHoverPreview();
            return;
        }

        var pointer = io.MousePos;
        if (KittenColorPickerContainsPointer(pointer) || SettingsContainsPointer(pointer)
            || (_overlayLayoutAvailable && HudCombinedHit(pointer) != SaberOverlayHit.None)
            || ForceOverlayHitAt(pointer) != ForceOverlayHit.None)
        {
            ClearForceHoverPreview();
            return;
        }

        var viewport = Program.MainViewport;
        var local = Cursor.ToViewportLocal(viewport, pointer);
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)
            || local.X < 0 || local.Y < 0 || local.X >= viewport.Width || local.Y >= viewport.Height)
        {
            ClearForceHoverPreview();
            return;
        }

        var camera = viewport.GetCamera();
        var picker = viewport.PartPicker;
        if (picker.Part is not { } part || !double.IsFinite(picker.Distance))
        {
            ClearForceHoverPreviewForNoHit(owner);
            return;
        }

        var ray = camera.ScreenToEgoRay(local);
        ray.Direction = double3.NormalizeOrZero(ray.Direction);
        if (!ForceTargeting.TryPreviewNativeHit(owner, part, picker.Distance, ray, camera, out var preview))
        {
            ClearForceHoverPreviewForNoHit(owner);
            return;
        }

        _forceHoverPreview = preview;
        SetForceHoverPreviewContext(owner);
    }

    private void SetForceHoverPreviewContext(KittenEva owner)
    {
        _forceHoverPreviewOwner = owner;
        _forceHoverPreviewSystem = owner.System;
        _forceHoverPreviewParent = owner.Parent;
        _forceHoverPreviewContextActive = true;
    }

    private void ClearForceHoverPreviewForNoHit(KittenEva owner)
    {
        _forceHoverPreview = null;
        SetForceHoverPreviewContext(owner);
    }

    private void ClearForceHoverPreview()
    {
        _forceHoverPreview = null;
        _forceHoverPreviewOwner = null;
        _forceHoverPreviewSystem = null;
        _forceHoverPreviewParent = null;
        _forceHoverPreviewContextActive = false;
        ClearForceHoverLabelCache();
    }

    private void ClearForceHoverLabelCache()
    {
        _forceHoverLabelVehicle = null;
        _forceHoverLabelPart = null;
        _forceHoverLabelClutter = null;
        _forceHoverLabelName = null;
        _forceHoverLabelText = string.Empty;
        _forceHoverLabelSize = float2.Zero;
        _forceHoverLabelRadiusTenths = -1;
        _forceHoverLabelFontId = 0;
        _forceHoverLabelFontSize = float.NaN;
        _forceHoverLabelWrap = float.NaN;
        _forceHoverLabelValid = false;
    }

    private bool TryGetForceHoverPresentation(Camera camera, out ForceHoverPresentation presentation)
    {
        presentation = default;
        if (!_forceHoverPreviewContextActive || !ForcePicking || !ForceCanUse
            || _owner is not { } owner || !ReferenceEquals(_forcePickOwner, owner)
            || !ReferenceEquals(_forceHoverPreviewOwner, owner)
            || !ReferenceEquals(_forceHoverPreviewSystem, owner.System)
            || !ReferenceEquals(_forceHoverPreviewParent, owner.Parent)) return false;

        if (_forceHoverPreview is { } preview
            && !preview.Vehicle.IsDisposed
            && ReferenceEquals(preview.Vehicle.System, owner.System)
            && ReferenceEquals(preview.Vehicle.Parent, owner.Parent)
            && ForceTargeting.IsLive(preview.Vehicle))
        {
            bool confirmed = _forceHover is { } settled
                && ReferenceEquals(settled.Vehicle, preview.Vehicle)
                && ReferenceEquals(settled.Part, preview.Part)
                && ForceTargeting.Valid(owner, settled);
            presentation = new(preview.HitEgo, preview.Vehicle, preview.Part, null, confirmed);
            return true;
        }

        if (_forceHover is { Vehicle: null, Part: null, Clutter: not null } clutter
            && ReferenceEquals(clutter.System, owner.System)
            && ReferenceEquals(clutter.Parent, owner.Parent)
            && ForceTargeting.TryHitEgo(clutter, camera, out var hitEgo))
        {
            presentation = new(hitEgo, null, null, clutter, true);
            return true;
        }
        return false;
    }

    private void GetForceHoverLabel(Vehicle? vehicle, Part? part, ForcePick? clutter, string name,
        bool confirmed, float wrap, out string label, out float2 size)
    {
        int radiusTenths = (int)Math.Clamp(Math.Round(_forceCaptureRadius * 10, MidpointRounding.AwayFromZero), 20, 500);
        uint fontId = ImGui.GetFont().FontId;
        float fontSize = ImGui.GetFontSize();
        if (_forceHoverLabelValid
            && ReferenceEquals(_forceHoverLabelVehicle, vehicle)
            && ReferenceEquals(_forceHoverLabelPart, part)
            && ReferenceEquals(_forceHoverLabelClutter, clutter)
            && string.Equals(_forceHoverLabelName, name, StringComparison.Ordinal)
            && _forceHoverLabelAngry == _angryForce && _forceHoverLabelRadiusTenths == radiusTenths
            && _forceHoverLabelConfirmed == confirmed && _forceHoverLabelFontId == fontId
            && _forceHoverLabelFontSize == fontSize && _forceHoverLabelWrap == wrap)
        {
            label = _forceHoverLabelText;
            size = _forceHoverLabelSize;
            return;
        }

        string radius = (radiusTenths / 10d).ToString("0.#");
        _forceHoverLabelText = name + (_angryForce
            ? $" + objects within {radius} m" : " — click to hold");
        _forceHoverLabelSize = ImGui.CalcTextSize(_forceHoverLabelText, false, wrap);
        _forceHoverLabelVehicle = vehicle;
        _forceHoverLabelPart = part;
        _forceHoverLabelClutter = clutter;
        _forceHoverLabelName = name;
        _forceHoverLabelAngry = _angryForce;
        _forceHoverLabelRadiusTenths = radiusTenths;
        _forceHoverLabelConfirmed = confirmed;
        _forceHoverLabelFontId = fontId;
        _forceHoverLabelFontSize = fontSize;
        _forceHoverLabelWrap = wrap;
        _forceHoverLabelValid = true;
        label = _forceHoverLabelText;
        size = _forceHoverLabelSize;
    }

    // Frame visibility can change even when KSA skips the overlay drawing hook.
    private void UpdateForceVisibility()
    {
        if (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner))
            SetForceStatus("Furce selection cancelled: the controlled kitten changed.", ForceStatusSeverity.None);
        if (_arrangeHud || !ForceUiVisible || Program.IsModalOpen() || !ForceCanUse
            || (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner)))
            ResetForceGestures();
        if (_forceDrag.Held && !ReferenceEquals(_forceMoveOwner, _owner)) _forceDrag.Release();
        if (_forceRotationDrag.Held && !ForceRotationGestureValid()) CancelForceRotationDrag();
        if (_forceSettingDrag.Held && !ReferenceEquals(_forceSettingOwner, _owner)) ReleaseForceSettingDrag();
        if (_forceThrowDrag.Held && (!ForceHolding || !ForceCanUse
            || !ReferenceEquals(_forceThrowOwner, _owner) || !ReferenceEquals(_force.Owner, _owner)))
            CancelForceThrowDrag();
        if (_forceCaptureRadiusDrag.Held && !ForceCaptureRadiusGestureValid())
            CancelForceThrowDrag();
    }

    // Snapshot the fresh camera ray; resolve it after workers join next frame.
    private void UpdateForceFrame()
    {
        try { UpdateForceFrameCore(); }
        catch (Exception error)
        {
            SetForceStatus("Furce picker failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            Report("Furce picker failed", error);
        }
    }

    private void UpdateForceFrameCore()
    {
        UpdateForceVisibility();
        RefreshForceHoverPreview();
        if (!ForcePicking || !ForceCanUse) return;
        var io = ImGui.GetIO();
        if (!HudApplicationHasFocus) { ResetForceGestures(); return; }
        if (_forceQuery is { Capture: true }) return;
        var pointer = _forceClickPending ? _forceClickPointer : io.MousePos;
        if ((!_forceClickPending && (io.WantCaptureMouse || !CursorTarget.IsHitTestViewport(Program.MainViewport)))
            || io.WantTextInput || Program.IsModalOpen() || KittenColorPickerContainsPointer(pointer)
            || (_overlayLayoutAvailable && HudCombinedHit(pointer) != SaberOverlayHit.None)
            || (_forceLayoutAvailable && ForceOverlayHitAt(pointer) != ForceOverlayHit.None))
        { _forceHover = null; _forceClickPending = false; _forceQuery = null; return; }
        if (!_forceClickPending && _playerTime < _forceNextHoverTime) return;
        _forceNextHoverTime = _playerTime + .05;
        var viewport = Program.MainViewport;
        var local = Cursor.ToViewportLocal(viewport, pointer);
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)
            || local.X < 0 || local.Y < 0 || local.X >= viewport.Width || local.Y >= viewport.Height)
        { _forceClickPending = false; _forceHover = null; _forceQuery = null; return; }
        var ray = viewport.GetCamera().ScreenToEgoRay(local);
        ray.Direction = double3.NormalizeOrZero(ray.Direction);
        if (ForceTargeting.TrySnapshotRay(_owner!, ray, out var snapshot))
        {
            _forceQuery = new(_owner!, snapshot, _forceClickPending, _angryForce, _forceCaptureRadius,
                _settings.ClutterPickTolerance, _settings.ForceCaptureLimit);
        }
        else { _forceQuery = null; _forceHover = null; }
        _forceClickPending = false;
    }

    private void BeginForcePicker(bool angry, double radius = ForceMath.DefaultCaptureRadius)
    {
        if (!ForceCanUse || _force.HasCapture) return;
        // A direct mode switch can arrive before the pointer's paired release.
        // Retire the press gesture without carrying it into the picker.
        if (_forceCaptureRadiusDrag.Held) CancelForceThrowDrag();
        ClearForceHoverPreview();
        if (_selectingSlice) Queue(Command.CancelSlice);
        _forceCaptureRadius = angry && ValidForceCaptureRadius(radius)
            ? radius : ForceMath.DefaultCaptureRadius;
        _forcePickOwner = _owner; _angryForce = angry; _selectingForce = true;
        _forceHover = null; _forceClickPending = false; _forceQuery = null; _forceNextHoverTime = 0;
        SetForceStatus(angry ? $"Angry Furce: select a target and objects within {_forceCaptureRadius:0.#} m of the hit. Right-click or Esc cancels."
            : "Furce: point at a vessel, debris, rock or tree trunk and click. Right-click or Esc cancels.", ForceStatusSeverity.None);
        SyncForceStatusFromController();
    }

    private static bool ValidForceCaptureRadius(double radius) => double.IsFinite(radius)
        && radius >= ForceCaptureRadiusDrag.MinimumRadius && radius <= ForceCaptureRadiusDrag.MaximumRadius;

    private void CancelForcePicker()
    {
        _selectingForce = false; _forcePickOwner = null; _forceHover = null; _forceClickPending = false; _forceQuery = null;
        ClearForceHoverPreview();
    }

    private void QueueForceAction(ForceAction action, double throwSpeed = ForceMath.ThrowSpeed)
    {
        if (!ForceCanUse || _forceRequests.Count >= 32) return;
        _forceRequests.Enqueue(new(_owner!, action, ThrowSpeed: SanitizeThrowSpeed(throwSpeed)));
        if (action is ForceAction.Release or ForceAction.Throw) RequestForceBreakupCancel();
        CancelForcePicker();
        _forceDrag.Release();
        // Retain a final drag offset until the settled handoff processes the release.
    }

    private void ToggleForcePicker(bool angry)
    {
        if (ForcePicking && _angryForce == angry)
        {
            CancelForcePicker();
            SetForceStatus("Furce selection cancelled.", ForceStatusSeverity.None);
        }
        else BeginForcePicker(angry);
    }

    private bool HandleForceOverlayPress(ForceOverlayHit hit, float2 pointer)
    {
        if (hit == ForceOverlayHit.None) return false;
        if (hit == ForceOverlayHit.AngryRadius) return true;
        if (hit == ForceOverlayHit.CaptureLimit)
        {
            CancelForcePicker();
            _forceDrag.Release();
            var center = hit == ForceOverlayHit.CaptureLimit
                ? _forceLayout.CaptureLimitCenter : _forceLayout.AngryRadiusCenter;
            double current = _settings.ForceCaptureLimit;
            double maximum = ForceCaptureLimits.Maximum;
            if (_forceSettingDrag.Begin(hit, pointer, center, _forceLayout.ButtonRadius,
                _forceLayout.SliderTravel, current, 1, maximum, integer: true))
                _forceSettingOwner = _owner;
            return true;
        }
        if (!ForceCanUse) { SetForceStatus("Control an EVA kitten to use the Furce.", ForceStatusSeverity.None); return true; }
        switch (hit)
        {
            case ForceOverlayHit.Force:
                if (_force.HasCapture) QueueForceAction(ForceAction.Release);
                else ToggleForcePicker(false);
                break;
            case ForceOverlayHit.AngryForce:
                if (_force.HasCapture)
                    BeginForceThrowSlider(pointer);
                else if (ForcePicking && _angryForce)
                {
                    CancelForcePicker();
                    SetForceStatus("Furce selection cancelled.", ForceStatusSeverity.None);
                }
                else BeginForceCaptureRadiusSelection(pointer);
                break;
            case ForceOverlayHit.Rotate:
                double currentRotation = ForceRotationDisplay;
                if (!ForceHolding || !ReferenceEquals(_force.Owner, _owner)
                    || !_forceRotationDrag.Begin(pointer, _forceLayout, currentRotation)) break;
                _forceDrag.Release();
                ReleaseForceSettingDrag();
                CancelForceThrowDrag();
                _forceRotationOwner = _owner;
                _forceRotationSystem = _owner!.System;
                _forceRotationParent = _owner.Parent;
                _forceRotationGeneration = _forceCaptureGeneration;
                _forceRotationTarget = currentRotation;
                _forceRotationPending = currentRotation != _force.ItemRotation;
                break;
            case ForceOverlayHit.Move:
                if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) break;
                var camera = Program.MainViewport.GetCamera();
                var bodyToEgo = double4x4.CreateFromQuaternion(_owner!.Body2Ego);
                if (!double4x4.Invert(bodyToEgo, out var egoToBody)) break;
                // Re-grab at the visible position. A raw pointer target can be ahead
                // of the group while the spring is still moving toward it.
                double3 current = _force.GroupOffsetBody;
                if (_forceDrag.Begin(pointer, _forceLayout.MoveRadius, current,
                    double3.TransformNormal(camera.GetRightEcl(), egoToBody),
                    double3.TransformNormal(camera.GetUpEcl(), egoToBody),
                    double3.TransformNormal(camera.GetForwardEcl(), egoToBody)))
                {
                    CancelForceCaptureLiftForManualMovement();
                    _forceMoveOwner = _owner;
                    _forceMoveOffset = current;
                    _forceMovePending = true;
                }
                break;
        }
        return true;
    }

    private void MoveForcePointer(float2 pointer)
    {
        if (_forceRotationDrag.Held)
        {
            if (!ForceRotationGestureValid()) CancelForceRotationDrag();
            else if (_forceRotationDrag.Move(pointer))
            {
                if (_forceRotationTarget != _forceRotationDrag.Rotation)
                    _forceRotationSegments.Add((_forceRotationDrag.Rotation, ForceShiftHeld(HudModifierWindow)));
                _forceRotationTarget = _forceRotationDrag.Rotation;
                _forceRotationPending = true;
            }
            return;
        }
        if (_forceCaptureRadiusDrag.Held)
        {
            if (_forceCaptureRadiusDrag.Move(pointer) && _forceCaptureRadiusDrag.Dragged)
            {
                _forceCaptureRadius = _forceCaptureRadiusDrag.Radius;
                UpdateForceCaptureRadiusLabels(_forceCaptureRadius);
            }
            return;
        }
        if (_forceThrowDrag.Held)
        {
            _forceThrowDrag.Move(pointer);
            return;
        }
        if (_forceSettingDrag.Move(pointer, out double setting))
        {
            if (_forceSettingDrag.Control == ForceOverlayHit.CaptureLimit)
                _settings.ForceCaptureLimit = (int)setting;
            return;
        }
        if (!_forceDrag.Held) return;
        _forceDrag.Move(pointer);
        _forceMoveOffset = _forceDrag.Offset;
        _forceMovePending = true;
    }

    private void BeginForceThrowSlider(float2 pointer)
    {
        CancelForcePicker();
        _forceDrag.Release();
        if (!ForceCanUse || !_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)
            || !_forceLayoutAvailable || !_forceThrowDrag.Begin(pointer, _forceLayout.AngryCenter,
                _forceLayout.ButtonRadius, _forceLayout.ThrowSliderTravel)) return;
        _forceThrowOwner = _owner;
    }

    private void BeginForceCaptureRadiusSelection(float2 pointer)
    {
        CancelForcePicker();
        CancelForceThrowDrag();
        _forceDrag.Release();
        _forceCaptureRadius = ForceMath.DefaultCaptureRadius;
        if (!ForceCanUse || _force.HasCapture || !_forceLayoutAvailable
            || !_forceCaptureRadiusDrag.BeginSelection(pointer, _forceLayout.AngryCenter,
                _forceLayout.ButtonRadius, _forceLayout.SliderTravel)) return;
        SetForceCaptureRadiusGestureOwner(_owner!);
        UpdateForceCaptureRadiusLabels(_forceCaptureRadius);
    }

    /// <summary>Called from the overlay cursor hook while an Angry radius gesture owns the pointer.</summary>
    internal bool FilterForceRadiusCursor(float2 pointer)
    {
        if (!_forceCaptureRadiusDrag.Held) return false;
        if (!ForceCaptureRadiusGestureValid(requirePointer: true, pointer))
        {
            CancelForceThrowDrag();
            return true;
        }
        MoveForcePointer(pointer);
        return true;
    }

    internal ForceOverlayHit ForceOverlayHitAt(float2 pointer)
    {
        if (!_forceLayoutAvailable) return ForceOverlayHit.None;
        if (ForceCanUse && ForceHolding && ReferenceEquals(_force.Owner, _owner)
            && _forceLayout.HitRotation(pointer, ForceRotationDisplay)) return ForceOverlayHit.Rotate;
        var hit = _forceLayout.Hit(pointer);
        return hit == ForceOverlayHit.AngryRadius ? ForceOverlayHit.None : hit;
    }

    private double ForceRotationDisplay => _forceRotationPending ? _forceRotationTarget : _force.ItemRotation;

    private bool ForceRotationContextValid() => ForceCanUse && ForceHolding
        && ReferenceEquals(_force.Owner, _owner) && ReferenceEquals(_forceRotationOwner, _owner)
        && ReferenceEquals(_owner!.System, _forceRotationSystem)
        && ReferenceEquals(_owner.Parent, _forceRotationParent)
        && _forceRotationGeneration == _forceCaptureGeneration;

    private bool ForceRotationGestureValid() => ForceRotationContextValid() && ForceUiVisible
        && !Program.IsModalOpen() && !Program.HasMouseInputLatch && HudApplicationHasFocus
        && _forceLayoutAvailable && _forceRotationDrag.Matches(_forceLayout);

    private void CompleteForceRotationDrag(float2 pointer)
    {
        if (!_forceRotationDrag.Held) return;
        MoveForcePointer(pointer);
        _forceRotationDrag.Release();
    }

    private void CancelForceRotationDrag()
    {
        _forceRotationDrag.Release();
        _forceRotationPending = false;
        _forceRotationSegments.Clear();
        _forceRotationTarget = _force.ItemRotation;
        _forceRotationOwner = null;
        _forceRotationSystem = null;
        _forceRotationParent = null;
    }

    // Called only by the captured left-button release. Snapshot the radius or
    // speed before resetting the press-time gesture.
    private bool CompleteForceThrowSlider(float2 pointer)
    {
        if (_forceCaptureRadiusDrag.Held)
        {
            bool validRadius = ForceCaptureRadiusGestureValid(requirePointer: true, pointer);
            if (validRadius) MoveForcePointer(pointer);
            bool finished = _forceCaptureRadiusDrag.Finish(out _, out double radius);
            ClearForceCaptureRadiusGestureOwner();
            if (!finished) return false;
            if (!validRadius) return true;
            BeginForcePicker(true, radius);
            return true;
        }
        if (!_forceThrowDrag.Held) return false;
        bool valid = ForceUiVisible && !Program.IsModalOpen() && !Program.HasMouseInputLatch
            && HudApplicationHasFocus && ForceCanUse && ForceHolding
            && _forceLayoutAvailable && _forceThrowDrag.Matches(_forceLayout)
            && ReferenceEquals(_forceThrowOwner, _owner) && ReferenceEquals(_force.Owner, _owner);
        if (valid) _forceThrowDrag.Move(pointer);
        double speed = ForceMath.ThrowSpeed;
        bool completed = valid && _forceThrowDrag.Finish(out speed);
        _forceThrowDrag.Cancel();
        _forceThrowOwner = null;
        if (completed) QueueForceAction(ForceAction.Throw, speed);
        return true;
    }

    private void CancelForceThrowDrag()
    {
        _forceThrowDrag.Cancel();
        _forceThrowOwner = null;
        _forceCaptureRadiusDrag.Cancel();
        ClearForceCaptureRadiusGestureOwner();
    }

    private void ClearForceCaptureRadiusGestureOwner()
    {
        _forceCaptureRadiusOwner = null;
        _forceCaptureRadiusSystem = null;
        _forceCaptureRadiusParent = null;
        _forceCaptureRadiusGeneration = 0;
    }

    private static double SanitizeThrowSpeed(double speed) => double.IsFinite(speed)
        ? Math.Clamp(speed, ForceMath.ThrowSpeed, ForceMath.MaximumThrowSpeed) : ForceMath.ThrowSpeed;

    private void AdvanceForceSpread(double dtPlayer)
    {
        double maximumSpread = ForceSpreadLimits.ValidateMaximum(_settings.ForceMaximumSpread);
        _forceSpreadTarget = ForceMath.ClampGroupSpread(_forceSpreadTarget, maximumSpread);
        double current = _force.GroupSpread;
        if (current != _forceSpreadSmoother.Spread)
        {
            // Other controller work can seed a displayed factor directly. Rebase position
            // while retaining the latest published target below.
            _forceSpreadSmoother.Reset(ForceMath.ClampGroupSpread(current));
        }
        _forceSpreadSmoother.SetTarget(_forceSpreadTarget);
        if (!double.IsFinite(dtPlayer) || dtPlayer <= 0) return;
        double displayed = _forceSpreadSmoother.Advance(Math.Min(dtPlayer, .1));
        if (displayed != current) _force.SetSpread(displayed);
    }

    // GLFW input only updates the pending body-frame target. The settled handoff
    // smooths and applies it to every captured vessel and native clutter body together.
    private bool ScrollForcePointer(double ticks, bool shift = false)
    {
        if (!ForceCanUse || !_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) return false;
        if (!double.IsFinite(ticks) || ticks == 0) return false;
        if (shift)
        {
            _forceBreakupScrollPixels.Enqueue(ForceBreakupSequence.PixelsFromNativeScroll(ticks));
            _forceSpreadTarget = ForceMath.ScaleGroupSpread(_forceSpreadTarget, ticks, _settings.ForceMaximumSpread);
            return true;
        }
        var bodyToEgo = double4x4.CreateFromQuaternion(_owner!.Body2Ego);
        if (!double4x4.Invert(bodyToEgo, out var egoToBody)) return false;
        double3 current = _forceMovePending && ReferenceEquals(_forceMoveOwner, _owner)
            ? _forceMoveOffset : _forceOffsetSmoother.Target;
        var forward = double3.TransformNormal(Program.MainViewport.GetCamera().GetForwardEcl(), egoToBody);
        if (!_forceDrag.Scroll(ticks, current, forward)) return false;
        CancelForceCaptureLiftForManualMovement();
        _forceMoveOwner = _owner;
        _forceMoveOffset = _forceDrag.Offset;
        _forceMovePending = true;
        return true;
    }

    // Query GLFW directly in input callbacks so the modifier reflects native input
    // at event time, even if ImGui's translated KeyShift value has not caught up yet.
    internal static bool ForceShiftHeld(GlfwWindow window) => window.GetKey(GlfwKey.LeftShift) == GlfwKeyAction.Press
        || window.GetKey(GlfwKey.RightShift) == GlfwKeyAction.Press;

    private bool HandleForcePickerPress(GlfwMouseButton button, float2 pointer)
    {
        if (!ForcePicking) return false;
        if (button == GlfwMouseButton.Right) { CancelForcePicker(); return true; }
        if (button != GlfwMouseButton.Left || !ForceCanUse) return true;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || io.WantTextInput || Program.IsModalOpen() || KittenColorPickerContainsPointer(pointer)
            || !CursorTarget.IsHitTestViewport(Program.MainViewport)) return true;
        _forceClickPointer = pointer;
        _forceClickPending = true;
        return true;
    }

    internal bool FilterForceKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action)
    {
        if (!HudWindowAcceptsInput(window) || key != GlfwKey.Escape) return false;
        if (Program.IsModalOpen())
        {
            if (_forceThrowDrag.Held || _forceCaptureRadiusDrag.Held) CancelForceThrowDrag();
            CancelForceRotationDrag();
            return false;
        }
        if (_forceEscapeHeld)
        {
            if (action == GlfwKeyAction.Release) _forceEscapeHeld = false;
            return true;
        }
        var io = ImGui.GetIO();
        bool forceGestureHeld = _forceThrowDrag.Held || _forceCaptureRadiusDrag.Held || _forceRotationDrag.Held;
        if (!forceGestureHeld && (!ForcePicking && !_forceSettingDrag.Held || KittenColorPickerContainsPointer(io.MousePos)
            || io.WantTextInput || (io.WantCaptureKeyboard && !HudKeyboardOwnsInput(window)))) return false;
        if (action != GlfwKeyAction.Release)
        {
            _forceEscapeHeld = true;
            CancelForceRotationDrag();
            CancelForcePicker();
            ReleaseForceSettingDrag();
            CancelForceThrowDrag();
        }
        return true;
    }

    private void ReleaseForceSettingDrag()
    {
        _forceSettingDrag.Release();
        _forceSettingOwner = null;
    }

    private void ResetForceGestures()
    {
        CancelForcePicker(); _forceDrag.Release(); CancelForceRotationDrag(); ReleaseForceSettingDrag(); CancelForceThrowDrag(); _forceLayoutAvailable = false;
        if (_forceBreakupSequence.TensionPixels > 0 || _forceBreakupSequence.StageOneActive
            || _force.BreakupActive || _forceBreakupScrollPixels.Count != 0)
            RequestForceBreakupCancel();
    }

    private void ResetForceMovement()
    {
        _forceCaptureLift.Reset();
        _forceCaptureLiftOwner = null;
        _forceDrag.Reset();
        CancelForceRotationDrag();
        _forceRotationDrag.Reset();
        _forceOffsetSmoother.Reset();
        ResetForceSpreadSmoother();
        _forceBreakupScrollPixels.Clear();
        _forceBreakupSequence.Reset();
        if (_force.BreakupActive) _force.CancelBreakup();
        if (_forceBreakupAudioActive) ResetForceBreakupAudio();
        _forceBreakupCancelPending = false;
        _forceBreakupSplitCuePlayed = false;
        _forceMoveOffset = double3.Zero;
        _forceMovePending = false;
        _forceMoveOwner = null;
    }

    private void ResetForceSpreadSmoother()
    {
        _forceSpreadTarget = ForceMath.DefaultGroupSpread;
        _forceSpreadSmoother.Reset(_force.GroupSpread);
        _forceSpreadSmoother.SetTarget(_forceSpreadTarget);
    }

    private void StartForceCaptureLift(KittenEva owner)
    {
        _forceCaptureLift.Start();
        _forceCaptureLiftOwner = owner;
        _forceMoveOwner = owner;
        _forceMoveOffset = _force.GroupOffsetBody;
        _forceMovePending = false;
        _forceOffsetSmoother.Reset(_force.GroupOffsetBody);
    }

    private void CancelForceCaptureLiftForManualMovement()
    {
        if (!_forceCaptureLift.IsActive && _forceCaptureLiftOwner == null) return;
        _forceCaptureLift.Reset();
        _forceCaptureLiftOwner = null;
        double3 displayed = _force.GroupOffsetBody;
        _forceOffsetSmoother.Reset(displayed);
        _forceMoveOffset = displayed;
        _forceMovePending = false;
    }

    private void ResetForce()
    {
        ResetForceGestures(); _forceSettingDrag.Reset(); _forceRequests.Clear();
        _forceEscapeHeld = false; SetForceStatus("", ForceStatusSeverity.None);
        if (!_forceDisposed)
        {
            _force.Reset();
            SyncForceStatusFromController();
        }
        // The controller joins native readers before its Clear() cancels detached
        // breakup roots. Reset local gesture/audio state only after that safe cleanup.
        ResetForceMovement();
        ResetForceCaptureSession();
        ResetForceBreakupAudio();
    }

    private void DisposeForce()
    {
        if (_forceDisposed) return;
        ResetForce();
        _forceDisposed = true;
        try { _force.Dispose(); }
        finally { _forceBreakupAudio.Dispose(); }
    }

    private void DrawForceCaptureBubble(ImDrawListPtr draw, Camera camera, bool hasHoverPresentation,
        ForceHoverPresentation hoverPresentation)
    {
        if (!ForceUiVisible || Program.IsModalOpen() || !ForceCanUse
            || _owner is not { IsDisposed: false } owner || ForceHolding) return;

        double3 center;
        if (_forceCaptureRadiusDrag.Held)
            center = camera.GetPositionEgo(owner);
        else if (ForcePicking && _angryForce && hasHoverPresentation)
            center = hoverPresentation.HitEgo;
        else return;

        var viewport = ImGui.GetMainViewport();
        ForceCaptureBubble.Draw(draw, camera, center, _forceCaptureRadius,
            viewport.Pos, viewport.Size, SaberTypeFor(owner) == SaberType.Dark, _settings.ForceBubbleLineLengthPercent);
    }

    private void DrawForceHoverMarker(ImDrawListPtr draw, Camera camera, ForceHoverPresentation presentation)
    {
        if (double3.Dot(presentation.HitEgo, camera.GetForwardEcl()) <= .001) return;
        var ui = ImGui.GetMainViewport();
        var buffer = camera.FramebufferSize;
        if (buffer.X <= 0 || buffer.Y <= 0) return;
        var pixels = camera.EgoToScreen(presentation.HitEgo);
        var position = ui.Pos + new float2(pixels.X * ui.Size.X / buffer.X, pixels.Y * ui.Size.Y / buffer.Y);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return;

        var markerColor = presentation.Confirmed
            ? new ImColor8(100, 255, 180, 255)
            : new ImColor8(195, 200, 210, 230);
        draw.AddCircle(position, 19, new ImColor8(0, 0, 0, 220), 48, 5);
        draw.AddCircle(position, 19, markerColor, 48, presentation.Confirmed ? 2 : 1.5f);
        draw.AddCircleFilled(position, 3, markerColor, 16);

        string name = presentation.Vehicle?.Id ?? presentation.Clutter?.DisplayName ?? "Unavailable object";
        float wrap = Math.Max(100, Math.Min(320, ui.Size.X - 24));
        GetForceHoverLabel(presentation.Vehicle, presentation.Part, presentation.Clutter, name,
            presentation.Confirmed, wrap, out string label, out var labelSize);
        var labelPosition = new float2(Math.Clamp(position.X + 25, ui.Pos.X + 8,
            ui.Pos.X + Math.Max(8, ui.Size.X - wrap - 8)), Math.Clamp(position.Y,
            ui.Pos.Y + 8, ui.Pos.Y + Math.Max(8, ui.Size.Y - labelSize.Y - 8)));
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition + new float2(1),
            new ImColor8(0, 0, 0, 255), label, wrap);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition, markerColor, label, wrap);
    }

    private void DrawForceWorldOverlay(ImDrawListPtr draw)
    {
        if (!ForceUiVisible) return;
        Camera? frameCamera = Program.GetMainCamera();
        ForceHoverPresentation hoverPresentation = default;
        bool hasHoverPresentation = frameCamera is { } cameraForPreview
            && TryGetForceHoverPresentation(cameraForPreview, out hoverPresentation);
        if (frameCamera is { } camera)
            DrawForceCaptureBubble(draw, camera, hasHoverPresentation, hoverPresentation);
        if (hasHoverPresentation && frameCamera is { } markerCamera)
            DrawForceHoverMarker(draw, markerCamera, hoverPresentation);
    }

    private void DrawForceOverlay(ImDrawListPtr draw)
    {
        try { DrawForceOverlayCore(draw); }
        catch (Exception error)
        {
            SetForceStatus("Furce overlay failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            ReleaseForceSettingDrag();
            CancelForceThrowDrag();
            Report("Furce overlay failed", error);
        }
    }

    private void DrawForceOverlayCore(ImDrawListPtr draw)
    {
        RefreshForceHoverPreview();
        if (!_forceLayoutAvailable) return;
        var layout = _forceLayout;
        // A resize, DPI change or custom offset invalidates press-time geometry.
        // Collapse visually; the pointer capture still suppresses input until release.
        if (_forceSettingDrag.Held && !_forceSettingDrag.Matches(layout)) ReleaseForceSettingDrag();
        if (_forceThrowDrag.Held && !_forceThrowDrag.Matches(layout)) CancelForceThrowDrag();
        if (_forceCaptureRadiusDrag.Held && !_forceCaptureRadiusDrag.Matches(layout)) CancelForceThrowDrag();
        if (_forceRotationDrag.Held && !ForceRotationGestureValid()) CancelForceRotationDrag();
        var white = new ImColor8(255, 255, 255, 255);
        var muted = new ImColor8(195, 200, 210, 255);
        var color = ForceCanUse ? white : muted;
        var buttonColor = ForceCanUse ? MovementButtonText : MovementButtonDisabledText;
        bool forcePicking = !ForceHolding && ForcePicking && !_angryForce;
        bool angryPicking = !ForceHolding && ForcePicking && _angryForce;
        bool angrySliderHeld = _forceThrowDrag.Held || _forceCaptureRadiusDrag.Held;
        var settingsFill = new ImColor8(0, 0, 0, 128);
        var faintWhiteRim = new ImColor8(255, 255, 255, 64);
        draw.AddCircleFilled(layout.ForceCenter, layout.ButtonRadius, MovementButtonFill, 48);
        if (!angrySliderHeld)
            draw.AddCircleFilled(layout.AngryCenter, layout.ButtonRadius,
                angryPicking ? new ImColor8(224, 24, 24, 210) : new ImColor8(224, 24, 24, 128), 48);
        draw.AddCircle(layout.ForceCenter, layout.ButtonRadius, faintWhiteRim, 48,
            forcePicking ? Math.Max(2, 3 * layout.Scale) : Math.Max(.5f, layout.Scale));
        CenterOverlayText(draw, layout.ForceCenter, buttonColor, ForceHolding ? "RELEASE" : forcePicking ? "CANCEL" : "FURCE", layout.ButtonRadius * 1.75f);
        if (!angrySliderHeld)
        {
            if (ForceHolding)
                CenterOverlayText(draw, layout.AngryCenter, color, "THROW", layout.ButtonRadius * 1.75f);
            else if (angryPicking)
                CenterOverlayText(draw, layout.AngryCenter, color, "CANCEL", layout.ButtonRadius * 1.75f);
            else
            {
                float labelFont = Math.Min(ImGui.GetFontSize(), 14 * layout.Scale);
                CenterOverlayText(draw, layout.AngryCenter - new float2(0, labelFont * .5f), color,
                    "ANGRY", layout.ButtonRadius * 1.75f, labelFont);
                CenterOverlayText(draw, layout.AngryCenter + new float2(0, labelFont * .5f), color,
                    "FURCE", layout.ButtonRadius * 1.75f, labelFont);
            }
        }
        draw.AddCircleFilled(layout.MoveCenter, layout.MoveRadius, new ImColor8(0, 0, 0, 128), 64);
        draw.AddCircle(layout.MoveCenter, layout.MoveRadius, faintWhiteRim, 64, Math.Max(.5f, layout.Scale));
        float font = Math.Min(ImGui.GetFontSize(), 16 * layout.Scale);
        var moveText = ForceHolding
            ? new ImColor8(255, 255, 255, 204)
            : new ImColor8(195, 200, 210, 204);
        CenterOverlayText(draw, layout.MoveCenter - new float2(0, font * .55f), moveText,
            "THE FURCE", layout.MoveRadius * 1.6f, font);
        CenterOverlayText(draw, layout.MoveCenter + new float2(0, font * .65f), moveText,
            "drag + [shift] wheel", layout.MoveRadius * 1.6f, font);
        if (_forceDrag.Held)
            draw.AddCircle(layout.MoveCenter + _forceDrag.PointerOffset * (layout.MoveRadius * .65f),
                Math.Max(2, 5 * layout.Scale), white, 16, 1);
        DrawForceSettingControl(ForceOverlayHit.CaptureLimit, layout.CaptureLimitCenter,
            _settings.ForceCaptureLimit, "COUNT", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}", "1", ForceCaptureLimits.Maximum.ToString());
        if (ForceCanUse && ForceHolding && ReferenceEquals(_force.Owner, _owner))
        {
            if (_forceRotationDrag.Held)
            {
                // Reveal the constrained track while dragging; idle controls stay small.
                var trackColor = new ImColor8(255, 255, 255, 32);
                var previous = layout.RotationHandleCenter(-Math.PI * 2);
                for (int i = 1; i <= 48; i++)
                {
                    var next = layout.RotationHandleCenter(-Math.PI * 2 + Math.PI * 4 * i / 48);
                    draw.AddLine(previous, next, trackColor, Math.Max(.5f, layout.Scale));
                    previous = next;
                }
            }
            draw.AddCircleFilled(layout.RotationHandleCenter(ForceRotationDisplay),
                layout.RotationHandleRadius, faintWhiteRim, 24);
        }
        if (_forceStatusSeverity is ForceStatusSeverity.Warning or ForceStatusSeverity.Error
            && !string.IsNullOrWhiteSpace(_forceStatus))
            DrawCenteredOverlayStatus(draw, _forceStatus, layout.StatusMin, layout.StatusMax,
                font, layout.Scale, bottomAligned: true);
        var overlayPointer = ImGui.GetIO().MousePos;
        if (!_forceSettingDrag.Held && !_forceThrowDrag.Held && !_forceCaptureRadiusDrag.Held && !_forceRotationDrag.Held
            && !KittenColorPickerContainsPointer(overlayPointer) && !SettingsContainsPointer(overlayPointer))
        {
            var overlayHit = ForceOverlayHitAt(overlayPointer);
            if (overlayHit == ForceOverlayHit.AngryForce && ForceHolding)
                ShowSaberTooltip("Hold and drag up to choose a throw speed; release to throw.");
            else if (overlayHit == ForceOverlayHit.Rotate)
                ShowSaberTooltip("rotate captured items (hold Shift for alternate axis)");
            else if (overlayHit == ForceOverlayHit.CaptureLimit)
                ShowSaberTooltip("max # objects Furce can capture");
        }
        if (_forceThrowDrag.Held)
            DrawForceThrowSlider(draw, layout, settingsFill, faintWhiteRim, white, muted);
        else if (_forceCaptureRadiusDrag.Held)
            DrawForceCaptureRadiusSlider(draw, layout, settingsFill, faintWhiteRim, white, muted);

        void DrawForceThrowSlider(ImDrawListPtr list, ForceOverlayLayout currentLayout,
            ImColor8 fill, ImColor8 rim, ImColor8 foreground, ImColor8 endpointColor)
        {
            list.AddRectFilled(_forceThrowDrag.PillMin, _forceThrowDrag.PillMax,
                fill, currentLayout.ButtonRadius);
            list.AddRect(_forceThrowDrag.PillMin, _forceThrowDrag.PillMax, rim,
                currentLayout.ButtonRadius, ImDrawFlags.None, Math.Max(.5f, currentLayout.Scale));
            float endpointFont = Math.Min(ImGui.GetFontSize(), 9 * currentLayout.Scale);
            CenterOverlayText(list, new(currentLayout.AngryCenter.X, _forceThrowDrag.TrackTop), endpointColor,
                $"{ForceThrowDrag.MaximumSpeed:0}", currentLayout.ButtonRadius * 1.65f, endpointFont);
            CenterOverlayText(list, new(currentLayout.AngryCenter.X, _forceThrowDrag.TrackBottom), endpointColor,
                $"{ForceThrowDrag.MinimumSpeed:0}", currentLayout.ButtonRadius * 1.65f, endpointFont);
            float pillWidth = _forceThrowDrag.PillMax.X - _forceThrowDrag.PillMin.X;
            float thumbRadius = Math.Max(2, pillWidth * .4f);
            list.AddCircleFilled(_forceThrowDrag.Thumb, thumbRadius, new ImColor8(45, 105, 210, 230), 24);
            list.AddCircle(_forceThrowDrag.Thumb, thumbRadius, foreground, 24, Math.Max(1, currentLayout.Scale));
            string value = $"{_forceThrowDrag.Speed:0} m/s";
            float fontSize = ImGui.GetFontSize();
            var labelPosition = new float2(_forceThrowDrag.PillMax.X + currentLayout.SliderValueLabelGap,
                _forceThrowDrag.Thumb.Y - fontSize * .5f);
            list.AddText(ImGui.GetFont(), fontSize, labelPosition + new float2(currentLayout.Scale),
                new ImColor8(0, 0, 0, 255), value, 0);
            list.AddText(ImGui.GetFont(), fontSize, labelPosition, foreground, value, 0);
        }

        void DrawForceCaptureRadiusSlider(ImDrawListPtr list, ForceOverlayLayout currentLayout,
            ImColor8 fill, ImColor8 rim, ImColor8 foreground, ImColor8 endpointColor)
        {
            list.AddRectFilled(_forceCaptureRadiusDrag.PillMin, _forceCaptureRadiusDrag.PillMax,
                fill, currentLayout.ButtonRadius);
            list.AddRect(_forceCaptureRadiusDrag.PillMin, _forceCaptureRadiusDrag.PillMax, rim,
                currentLayout.ButtonRadius, ImDrawFlags.None, Math.Max(.5f, currentLayout.Scale));
            float endpointFont = Math.Min(ImGui.GetFontSize(), 9 * currentLayout.Scale);
            CenterOverlayText(list, new(currentLayout.AngryCenter.X, _forceCaptureRadiusDrag.TrackTop), endpointColor,
                "50m", currentLayout.ButtonRadius * 1.65f, endpointFont);
            CenterOverlayText(list, new(currentLayout.AngryCenter.X, _forceCaptureRadiusDrag.TrackBottom), endpointColor,
                "2m", currentLayout.ButtonRadius * 1.65f, endpointFont);
            float pillWidth = _forceCaptureRadiusDrag.PillMax.X - _forceCaptureRadiusDrag.PillMin.X;
            float thumbRadius = Math.Max(2, pillWidth * .4f);
            list.AddCircleFilled(_forceCaptureRadiusDrag.Thumb, thumbRadius, new ImColor8(45, 105, 210, 230), 24);
            list.AddCircle(_forceCaptureRadiusDrag.Thumb, thumbRadius, foreground, 24, Math.Max(1, currentLayout.Scale));
            UpdateForceCaptureRadiusLabels(_forceCaptureRadiusDrag.Radius);
            string value = _forceCaptureRadiusSliderLabel;
            float fontSize = ImGui.GetFontSize();
            var labelPosition = new float2(_forceCaptureRadiusDrag.PillMax.X + currentLayout.SliderValueLabelGap,
                _forceCaptureRadiusDrag.Thumb.Y - fontSize * .5f);
            list.AddText(ImGui.GetFont(), fontSize, labelPosition + new float2(currentLayout.Scale),
                new ImColor8(0, 0, 0, 255), value, 0);
            list.AddText(ImGui.GetFont(), fontSize, labelPosition, foreground, value, 0);
        }

        void DrawForceSettingControl(ForceOverlayHit control, float2 center, double current, string caption,
            Func<double, string> format, string minimum, string maximum)
        {
            float labelFont = Math.Min(ImGui.GetFontSize(), 11 * layout.Scale);
            if (_forceSettingDrag.Held && _forceSettingDrag.Control == control)
            {
                draw.AddRectFilled(_forceSettingDrag.PillMin, _forceSettingDrag.PillMax,
                    settingsFill, layout.ButtonRadius);
                draw.AddRect(_forceSettingDrag.PillMin, _forceSettingDrag.PillMax, faintWhiteRim,
                    layout.ButtonRadius, ImDrawFlags.None, Math.Max(.5f, layout.Scale));
                CenterOverlayText(draw, new(center.X, _forceSettingDrag.TrackTop), muted,
                    minimum, layout.ButtonRadius * 1.65f, labelFont * .8f);
                CenterOverlayText(draw, new(center.X, _forceSettingDrag.TrackBottom), muted,
                    maximum, layout.ButtonRadius * 1.65f, labelFont * .8f);
                var thumb = _forceSettingDrag.Thumb(current);
                float pillWidth = _forceSettingDrag.PillMax.X - _forceSettingDrag.PillMin.X;
                float thumbRadius = Math.Max(2, pillWidth * .4f);
                draw.AddCircleFilled(thumb, thumbRadius, new ImColor8(45, 105, 210, 230), 24);
                draw.AddCircle(thumb, thumbRadius, white, 24, Math.Max(1, layout.Scale));
                string value = format(current);
                float fontSize = ImGui.GetFontSize();
                var labelPosition = new float2(_forceSettingDrag.PillMax.X + layout.SliderValueLabelGap,
                    thumb.Y - fontSize * .5f);
                draw.AddText(ImGui.GetFont(), fontSize, labelPosition + new float2(layout.Scale),
                    new ImColor8(0, 0, 0, 255), value, 0);
                draw.AddText(ImGui.GetFont(), fontSize, labelPosition, white, value, 0);
            }
            else
            {
                draw.AddCircleFilled(center, layout.ButtonRadius, settingsFill, 48);
                draw.AddCircle(center, layout.ButtonRadius, faintWhiteRim, 48, Math.Max(.5f, layout.Scale));
                CenterOverlayText(draw, center - new float2(0, labelFont * .5f), color,
                    caption, layout.ButtonRadius * 1.75f, labelFont);
                CenterOverlayText(draw, center + new float2(0, labelFont * .55f), color,
                    format(Math.Round(current, MidpointRounding.AwayFromZero)), layout.ButtonRadius * 1.75f, labelFont);
            }
        }
    }
}
