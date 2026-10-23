using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;

namespace Purr2D2.Runtime;

// Compile the real Force runtime partial. Only host UI/physics boundaries are fake.
internal sealed partial class SaberRuntime
{
    private bool _ready = true, _disposed = false, _selectingSlice = false;
    private bool OverlayVisible = true, KittenColorPickerVisible = false, _overlayLayoutAvailable = false;
    private SaberOverlayLayout _overlayLayout = default;
    private double _playerTime = 0;
    private KittenEva? _owner;
    private int _sliceCancellations;
    private enum Command { CancelSlice }
    private void Queue(Command command) => _sliceCancellations++;
    private static void Report(string operation, Exception error) { }
    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text,
        float maxWidth = float.MaxValue, float fontSize = 0) { }
    private SaberRuntime()
    {
        _forceLayout = default;
        _owner = new KittenEva(); Program.ControlledVehicle = _owner;
        Program.EditorFlag = Program.Modal = false;
        Program.MainViewport = new(); CursorTarget.Hit = true;
        ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
        ImGui.Io.MousePos = new(300, 200);
        ForceTargeting.Picks = 0; ForceTargeting.Next = new(new Vehicle());
        ForceTargeting.FailPick = false;
    }

    public static void Main()
    {
        ForceOverlayChecks.Run();
        var runtime = new SaberRuntime { _selectingSlice = true };
        runtime.BeginForcePicker(true);
        Check(runtime._sliceCancellations == 1 && runtime._selectingSlice,
            "Force selection queues slice-picker cancellation without mutating the settled saber state");
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        Check(ForceTargeting.Picks == 0 && runtime._forceRequests.Count == 0 && runtime._forceClickPending,
            "a raw click records only its pixel and defers picking until the frame camera is current");
        Program.MainViewport.Camera.Generation = 7;
        runtime.UpdateForceFrame();
        Check(ForceTargeting.Picks == 0 && runtime._forceQuery is { Capture: true }
            && runtime._forceRequests.Count == 0 && !runtime.ForceHolding,
            "the fresh frame camera snapshots the click without reading live physics bodies");
        Program.MainViewport.Camera.Generation = 8;
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime.ForceHolding && runtime._force.CaptureCalls == 1 && ForceTargeting.Picks == 1
            && ForceTargeting.LastRay.Origin == new double3(80, 60, 7),
            "the original click ray is queried and captured once at the settled handoff");

        runtime._force.Events.Clear();
        runtime._forceMoveOwner = runtime._owner;
        Check(runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ), "held movement starts");
        runtime.MoveForcePointer(new(80, 0));
        runtime.QueueForceAction(ForceAction.Throw);
        Check(runtime.ForceHolding, "throw clicks queue work and retain ownership until the safe handoff");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == new double3(8, 0, 0)
            && runtime._force.Events.SequenceEqual(new[] { "Offset", "Update", "Throw" }),
            "the final drag offset is applied before updating and throwing the held group");

        runtime = new(); runtime.BeginForcePicker(false);
        ImGui.Io.WantCaptureMouse = true;
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        Check(!runtime._forceClickPending && ForceTargeting.Picks == 0, "native ImGui mouse ownership blocks Force picking");
        ImGui.Io.WantCaptureMouse = false; runtime.KittenColorPickerVisible = true;
        runtime.UpdateForceFrame();
        Check(ForceTargeting.Picks == 0, "the saber popup has priority over Force hover work");
        runtime.KittenColorPickerVisible = false;
        Program.Modal = true;
        Check(!runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press) && runtime.ForcePicking,
            "a native modal retains Escape instead of cancelling the Force picker");
        Program.Modal = false; ImGui.Io.WantCaptureKeyboard = true;
        Check(!runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press) && runtime.ForcePicking,
            "a keyboard-capturing native UI retains Escape priority");
        ImGui.Io.WantCaptureKeyboard = false;
        Check(runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !runtime.ForcePicking && runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels the picker and consumes its matching release");

        runtime = new(); runtime.BeginForcePicker(false);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        runtime._owner = new(); Program.ControlledVehicle = runtime._owner;
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 0 && runtime._force.CaptureCalls == 0,
            "switching owner before the camera frame discards the pending Force click");

        runtime = new(); runtime.BeginForcePicker(false);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90)); runtime.UpdateForceFrame();
        runtime._owner = new(); Program.ControlledVehicle = runtime._owner; runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.CaptureCalls == 0 && ForceTargeting.Picks == 0,
            "switching owner after a ray snapshot rejects the query before reading physics");

        runtime = new(); runtime.BeginForcePicker(false);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90)); runtime.UpdateForceFrame();
        ImGui.Io.AppFocusLost = true; runtime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 0 && runtime._force.CaptureCalls == 0,
            "losing focus after the camera snapshot cancels capture before reading physics");

        runtime = new(); runtime.BeginForcePicker(false);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90)); runtime.UpdateForceFrame();
        ForceTargeting.Next!.Live = false; runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForceHolding && runtime._forceStatus == "Stale target", "a stale target is revalidated by the settled controller");

        runtime = new(); runtime.BeginForcePicker(false); runtime.UpdateForceFrame();
        Check(ForceTargeting.Picks == 0, "hover rendering never reads live physics bodies");
        runtime.UpdateForceBeforePhysics(.016);
        runtime.UpdateForceFrame(); Check(ForceTargeting.Picks == 1, "stationary hover is throttled to twenty picks per second");
        runtime._playerTime = .051; runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 2, "hover refreshes after the preview interval at the settled handoff");
        runtime.OverlayVisible = false; runtime.UpdateForceVisibility();
        Check(!runtime._selectingForce && !runtime._forceDrag.Held, "HUD hiding cancels selection and gesture state");

        runtime = new(); runtime._force.FailUpdate = true; runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._forceStatus.StartsWith("Force suspended:") && !runtime.ForceHolding,
            "Force handoff failures clean up locally without escaping into global saber reset");
        runtime.DisposeForce(); runtime.ResetForce();
        Check(runtime._force.Disposed, "shutdown and a subsequent world reset are safe and idempotent");
        runtime = new(); runtime.BeginForcePicker(false); ForceTargeting.FailPick = true;
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForcePicking && !runtime._forceClickPending && runtime._force.CaptureCalls == 0
            && runtime._forceStatus.StartsWith("Force picker failed:"),
            "settled picker exceptions cancel locally without skipping the surrounding saber overlay or mutating physics");
        Console.WriteLine("Force UI runtime checks passed (deferred clicks, native UI priority, settled commands, stale owners/targets, final move/throw, cleanup).");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
