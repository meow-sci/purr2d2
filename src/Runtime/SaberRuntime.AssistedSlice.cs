using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly AssistedSliceSequence _sliceSequence = new();
    private SliceTarget? _sliceTarget, _sliceHover;
    private PartSliceTargeting.Selection? _sliceSelection;
    private bool _selectingSlice, _sliceStarted;
    private string? _sliceError;
    private float3 _sliceAim = float3.UnitZ;
    private float _sliceBladeLength;
    private double _sliceNextSpark;
    private string _sliceStatus = "";
    // Reach is measured from the kitten, with room for the paw/hilt offset.
    private double SliceReach => Math.Max(3, _settings.BladeLength + 1);

    private void SelectPartToSlice()
    {
        if (_sliceSequence.Active || _owner == null) return;
        if (_slicer.Busy) { _sliceStatus = "Finishing the previous slice; try again shortly."; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = true;
        _sliceStatus = "Point at a part and click to slice. Right-click or Esc to cancel.";
    }

    private void BeginAssistedSlice(SliceTarget? target, Ray? ray)
    {
        if (_sliceSequence.Active || _owner == null || _slicer.Busy) return;
        if (target != null && !_targeting.CheckSliceTarget(_owner, target, default, SliceReach, out var reason))
        { _selectingSlice = true; _sliceStatus = reason; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = false; _sliceHover = null;
        _sliceTarget = target;
        if (target == null && ray is { } pick)
            _sliceSelection = _targeting.BeginSelection(_owner, pick.Origin, pick.Direction, SliceReach);
        if (target == null && _sliceSelection == null) return;
        _sliceStarted = false; _sliceError = null;
        _sliceBladeLength = _settings.BladeLength;
        _sliceSequence.Start(); _sliceNextSpark = .2;
        _sliceStatus = "Slicing the selected part...";
    }

    private void CancelAssistedSlice(string reason)
    {
        _selectingSlice = false; _sliceHover = null;
        _sliceSequence.Cancel();
        _sliceSelection?.Dispose(); _sliceSelection = null;
        _slicer.Cancel();
        _sliceTarget = null; _sliceStarted = false; _sliceError = null;
        _sliceBladeLength = 0;
        _pose.ClearSliceSwing();
        _sliceStatus = reason;
    }

    // Main-thread handoff. Animation never waits for a plane, export or upload.
    private void UpdateAssistedSlice(double dtPlayer)
    {
        if (!_sliceSequence.Active || _owner == null) return;
        if (_fault != null || !_blade.Equipped || !_blade.TargetOn)
        { CancelAssistedSlice("Slice cancelled: saber was stowed."); return; }
        if (_sliceTarget == null)
        {
            _sliceSelection!.Advance();
            if (!_sliceSelection.Done) { _sliceStatus = "Selecting the part under the pointer..."; return; }
            if (_sliceSelection.Target is not { } target)
            {
                string reason = _sliceSelection.Reason;
                CancelAssistedSlice(reason); _selectingSlice = true; return;
            }
            _sliceTarget = target;
            _sliceSelection.Dispose(); _sliceSelection = null;
        }
        if (!_sliceStarted)
        {
            _sliceStarted = true;
            try { _slicer.Begin(_sliceTarget.Part); }
            catch (Exception error) { _sliceError = error.Message; }
        }
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckSliceTarget(_owner, _sliceTarget, default, double.PositiveInfinity, out var refusal))
            _sliceError ??= refusal;
        double speed = Universe.GetSimulationSpeed();
        if (!SaberLimits.SupportsSpeed(speed)) return;
        if (!_sliceSequence.EndPresented && (!_freshPoseThisTick || !_pose.TryGetBladeSegment(1, out _, out _))) return;

        if (!_sliceTarget.Vehicle.IsDisposed && Program.GetMainCamera() is { } camera)
        {
            var hitEgo = _sliceTarget.HitAssembly.Transform(_sliceTarget.Vehicle.GetMatrixAsmb2Ego(camera));
            var bodyToEgo = double4x4.CreateFromQuaternion(_owner.Body2Ego)
                * double4x4.CreateTranslation(camera.GetPositionEgo(_owner));
            if (double4x4.Invert(bodyToEgo, out var egoToBody)
                && double4x4.Invert(SaberPoseMath.ModelToBody(1, float3.Zero), out var bodyToModel))
            {
                var direction = hitEgo.Transform(egoToBody);
                double length = direction.Length();
                if (double.IsFinite(length) && length > .02)
                {
                    var modelDirection = double3.TransformNormal(direction, bodyToModel).NormalizeOrZero();
                    _sliceAim = new((float)modelDirection.X, (float)modelDirection.Y, (float)modelDirection.Z);
                    _sliceBladeLength = (float)Math.Min(SaberLimits.MaximumBladeLength, Math.Max(_settings.BladeLength, length + .5));
                }
            }
        }
        var cue = _sliceSequence.Advance(dtPlayer, _blade.Deployment >= .99f, _freshPoseThisTick || _sliceSequence.EndPresented,
            _slicer.Ready || _sliceError != null);
        if (!_sliceSequence.EndPresented) _pose.SetSliceSwing(_sliceAim, float3.UnitY, _sliceSequence.Progress);
        else _pose.ClearSliceSwing();
        if (_sliceSequence.Progress >= _sliceNextSpark && _sliceNextSpark <= .7
            && !_sliceTarget.Vehicle.IsDisposed && double4x4.Invert(_sliceTarget.PartTransform, out var assemblyToPart))
        {
            _renderer.AddCutSparks(_sliceTarget.Part, _sliceTarget.HitAssembly.Transform(assemblyToPart), double3.UnitY, _playerTime);
            _sliceNextSpark += .25;
        }
        _sliceStatus = !_sliceSequence.EndPresented ? "Swinging across " + _sliceTarget.Part.DisplayName + "..."
            : _slicer.Phase + " You can keep flying while the cut finishes.";
        if (cue == SliceCue.Commit)
        {
            if (_sliceError != null) { CancelAssistedSlice("Cannot slice: " + _sliceError); return; }
            // Reach was checked at the click. Moving after the stroke does not undo the cut.
            var result = _targeting.CommitAssistedSlice(_owner, _sliceTarget, default, double.PositiveInfinity, default);
            CancelAssistedSlice((result.Success ? "Slice complete: " : "Slice failed: ") + result.Message);
        }
    }

    private void HandleSlicePointer()
    {
        _sliceHover = null;
        if (!_selectingSlice || _owner == null || !Program.DrawUI || Program.EditorFlag) return;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || io.WantTextInput || !CursorTarget.IsHitTestViewport(Program.MainViewport)) return;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { Queue(Command.CancelSlice); return; }
        var ray = Cursor.GetEgoRay(Program.MainViewport);
        ray.Direction = ray.Direction.NormalizeOrZero(); // Match the native picker's distance units.
        var picker = Program.MainViewport.PartPicker;
        string reason = "Point at a vessel part and click to slice.";
        // Reuse the game's current-frame hover hit. No fleet-wide scan on each mouse move.
        if (picker.Part is { } hovered && double.IsFinite(picker.Distance))
            _targeting.TryPointTarget(_owner, hovered, ray.Origin + ray.Direction * picker.Distance, SliceReach, out _sliceHover, out reason);
        _sliceStatus = _sliceHover is { } hit ? "Click to slice " + hit.Part.DisplayName + "." : reason;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (picker.Part != null && _sliceHover == null) return;
        if (_commands.Count < 16)
        {
            _commands.Enqueue(new(_owner, Command.CutPicked, PartTarget: _sliceHover, PickRay: ray));
            _selectingSlice = false;
        }
    }

    private void DrawAssistedSliceUi(bool hasKitten)
    {
        var owner = Program.ControlledVehicle as KittenEva;
        ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null);
        try
        {
            if (ImGui.BeginCombo("Vessel target", owner?.Target?.Id ?? "None"))
            {
                try
                {
                    if (ImGui.Selectable("None", owner?.Target == null)) QueueTarget(null);
                    if (Universe.CurrentSystem is { } system)
                        foreach (var item in system.All.AsSpan())
                            if (item is Vehicle vessel && vessel is not KittenEva && !vessel.IsDisposed && !vessel.IsEditedVehicle
                                && ImGui.Selectable(vessel.Id + "##SliceTarget" + vessel.Hash, ReferenceEquals(owner?.Target, vessel))) QueueTarget(vessel);
                }
                finally { ImGui.EndCombo(); }
            }
            if (_sliceSequence.Active || _selectingSlice)
            {
                if (ImGui.Button("Cancel slice")) Queue(Command.CancelSlice);
                if (_sliceSequence.Active && !_sliceSequence.EndPresented)
                    ImGui.ProgressBar((float)_sliceSequence.Progress, new float2(-1, 0), "Saber swing");
            }
            else if (ImGui.Button("Select part to slice", new float2(-1, 32))) Queue(Command.SelectSlice);
        }
        finally { ImGui.EndDisabled(); }
        ImGui.TextWrapped("Select part to slice, then point and click on the vessel. The saber swings immediately; the pieces separate when ready.");
        ImGui.TextWrapped($"Reach: {SliceReach:F1} m. Swing makes a normal saber swipe; contact makes sparks. Tank contents are emptied; occupied vessels are protected.");
        if (_sliceStatus.Length > 0) ImGui.TextWrapped(_sliceStatus);
    }
}
