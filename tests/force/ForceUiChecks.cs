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
    private sealed class TestSettings
    {
        internal float AngryForceRadius = 10, ClutterPickTolerance = .2f;
        internal int ForceCaptureLimit = ForceCaptureLimits.Default;
    }
    private readonly TestSettings _settings = new();
    private double _playerTime = 0;
    private KittenEva? _owner;
    private int _sliceCancellations;
    private enum Command { CancelSlice }
    private void Queue(Command command) => _sliceCancellations++;
    private bool SettingsContainsPointer(float2 pointer) => false;
    private bool KittenColorPickerContainsPointer(float2 pointer) => KittenColorPickerVisible && pointer.X < 200;
    private static void Report(string operation, Exception error) { }
    private static readonly List<(float2 Center, string Text)> DrawnLabels = [];
    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text,
        float maxWidth = float.MaxValue, float fontSize = 0) => DrawnLabels.Add((center, text));
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
        ForcePickerButtons();
        var runtime = new SaberRuntime { _selectingSlice = true };
        runtime._settings.AngryForceRadius = 14;
        runtime._settings.ClutterPickTolerance = .375f;
        runtime._settings.ForceCaptureLimit = 256;
        runtime.BeginForcePicker(true);
        Check(runtime._forceStatus.Contains("14 m"), "the Angry Force instruction displays the configured radius");
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
        runtime._settings.AngryForceRadius = 25;
        runtime._settings.ClutterPickTolerance = 0;
        runtime._settings.ForceCaptureLimit = 1;
        Program.MainViewport.Camera.Generation = 8;
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime.ForceHolding && runtime._force.CaptureCalls == 1 && ForceTargeting.Picks == 1
            && ForceTargeting.LastRay.Origin == new double3(80, 60, 7) && runtime._force.CapturedRadius == 14,
            "the original click ray is queried and captured once at the settled handoff");
        Check(ForceTargeting.LastTolerance == .375,
            "the immutable click query keeps its tolerance if settings change before resolution");
        Check(runtime._force.CapturedLimit == 256,
            "the immutable click keeps its configured capture limit through the settled request if settings change");

        Check(!runtime._forceDrag.Held && runtime.ScrollForcePointer(1) && runtime.ScrollForcePointer(2),
            "wheel depth works without pressing the move circle and accepts consecutive events");
        Check(runtime._forceMovePending && runtime._forceMoveOffset == new double3(0, 0, 1.5)
            && runtime._force.GroupOffsetBody == double3.Zero,
            "hover wheel events accumulate in the pending offset without mutating captured physics");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.GroupOffsetBody == new double3(0, 0, 1.5) && !runtime._forceMovePending,
            "settled handoff applies the accumulated hover depth once");

        runtime._force.Events.Clear();
        runtime._forceMoveOwner = runtime._owner;
        Check(runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ), "held movement starts");
        runtime.MoveForcePointer(new(80, 0));
        Check(runtime.ScrollForcePointer(2) && runtime._forceMoveOffset == new double3(8, 0, 1),
            "wheel depth during a held drag preserves the camera-plane offset");
        runtime.QueueForceAction(ForceAction.Throw);
        Check(runtime.ForceHolding, "throw clicks queue work and retain ownership until the safe handoff");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == new double3(8, 0, 1)
            && runtime._force.Events.SequenceEqual(new[] { "Offset", "Update", "Throw" }),
            "the final drag offset is applied before updating and throwing the held group");

        runtime = new(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceMoveOwner = runtime._owner;
        runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ);
        runtime.MoveForcePointer(new(80, 0));
        var finalDragOffset = runtime._forceMoveOffset;
        runtime._forceDrag.Release();
        Check(runtime.ScrollForcePointer(-2) && runtime._forceMoveOffset == finalDragOffset - double3.UnitZ,
            "hover wheel after releasing a drag preserves the pending camera-plane movement");
        runtime.QueueForceAction(ForceAction.Release); runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == finalDragOffset - double3.UnitZ && !runtime.ScrollForcePointer(1),
            "release commits the final hover depth and later wheel input cannot move an unowned group");

        runtime = new(); runtime.BeginForcePicker(false);
        ImGui.Io.WantCaptureMouse = true;
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        Check(!runtime._forceClickPending && ForceTargeting.Picks == 0, "native ImGui mouse ownership blocks Force picking");
        ImGui.Io.WantCaptureMouse = false; runtime.KittenColorPickerVisible = true;
        ImGui.Io.MousePos = new(100, 90);
        runtime.UpdateForceFrame();
        Check(ForceTargeting.Picks == 0 && runtime._forceQuery is null, "the picker window has priority over Force hover work inside its bounds");
        ImGui.Io.MousePos = new(300, 200);
        runtime.UpdateForceFrame();
        Check(runtime._forceQuery != null, "an open picker allows Force hover outside its window bounds");
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
        Console.WriteLine("Force UI runtime checks passed (picker button states, deferred clicks, native UI priority, settled commands, stale owners/targets, final move/throw, cleanup).");
    }

    private static void ForcePickerButtons()
    {
        var runtime = new SaberRuntime();
        runtime._forceLayout = ForceOverlayLayout.Create(SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2),
            float2.Zero, new(1920, 1080));
        runtime._forceLayoutAvailable = true;
        var forceCenter = runtime._forceLayout.ForceCenter;
        var angryCenter = runtime._forceLayout.AngryCenter;
        void Draw()
        {
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear();
            runtime.DrawForceOverlayCore(default);
        }
        bool Label(float2 center, string text) => DrawnLabels.Contains((center, text));
        ImDrawListPtr.Circle Fill(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && circle.Filled);
        ImDrawListPtr.Circle Ring(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && !circle.Filled);
        Draw();
        var idleForce = Fill(forceCenter);
        var idleAngry = Fill(angryCenter);
        var idleForceRing = Ring(forceCenter);
        Check(Label(forceCenter, "FORCE") && !DrawnLabels.Any(label => label.Text == "CANCEL"),
            "idle Force controls show their action labels");

        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        Draw();
        Check(runtime.ForcePicking && !runtime._angryForce && Label(forceCenter, "CANCEL")
            && !Label(angryCenter, "CANCEL") && Fill(forceCenter).Color != idleForce.Color
            && Ring(forceCenter).Thickness > idleForceRing.Thickness,
            "the active Force picker displays Cancel with a brighter fill and stronger ring on its own button");
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(300, 200)); runtime.UpdateForceFrame();
        Check(runtime._forceQuery is { Capture: true }, "a pending capture exists before button cancellation");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        runtime.UpdateForceBeforePhysics(.016); Draw();
        Check(!runtime.ForcePicking && runtime._forcePickOwner == null && runtime._forceQuery == null
            && !runtime._forceClickPending && runtime._forceHover == null && ForceTargeting.Picks == 0
            && runtime._forceRequests.Count == 0 && Label(forceCenter, "FORCE") && Fill(forceCenter) == idleForce,
            "pressing the active Force button cancels selection, clears pending capture and restores its label and color");

        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(300, 200)); runtime.UpdateForceFrame();
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter); Draw();
        Check(runtime.ForcePicking && runtime._angryForce && runtime._forceQuery == null && !runtime._forceClickPending
            && Label(forceCenter, "FORCE") && Label(angryCenter, "CANCEL")
            && Fill(forceCenter) == idleForce && Fill(angryCenter).Color != idleAngry.Color
            && !ImDrawListPtr.Circles.Any(circle => circle.Center == angryCenter && !circle.Filled),
            "the other mode switches to borderless Angry Force and moves Cancel without retaining an old click");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter); Draw();
        Check(runtime.ForcePicking && !runtime._angryForce && Label(forceCenter, "CANCEL")
            && !Label(angryCenter, "CANCEL") && Fill(angryCenter) == idleAngry,
            "switching from Angry Force back to Force updates both button states");
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter);
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter); Draw();
        Check(!runtime.ForcePicking && !DrawnLabels.Any(label => label.Text == "CANCEL") && Fill(angryCenter) == idleAngry,
            "pressing the active Angry Force button also cancels its picker and restores its normal appearance");

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime.BeginForcePicker(false); Draw();
        Check(Label(forceCenter, "RELEASE") && Label(angryCenter, "THROW")
            && !DrawnLabels.Any(label => label.Text == "CANCEL"),
            "a held Force group preserves Release and Throw instead of picker cancellation labels");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        Check(runtime._forceRequests.TryPeek(out var release) && release.Action == ForceAction.Release,
            "the Force button still queues release while holding a group");
        runtime._forceRequests.Clear();
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter);
        Check(runtime._forceRequests.TryPeek(out var thrown) && thrown.Action == ForceAction.Throw,
            "the Angry Force button still queues throw while holding a group");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
