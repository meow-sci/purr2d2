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
    private static readonly ImColor8 MovementButtonFill = new(0, 0, 255, 64);
    private static readonly ImColor8 MovementButtonText = new(210, 230, 255, 255);
    private static readonly ImColor8 MovementButtonDisabledText = new(135, 145, 165, 255);
    private bool _ready = true, _disposed = false, _selectingSlice = false;
    private bool OverlayVisible = true, KittenColorPickerVisible = false, _overlayLayoutAvailable = false;
    private SaberOverlayLayout _overlayLayout = default;
    private sealed class TestSettings
    {
        internal float AngryForceRadius = 10, ClutterPickTolerance = .2f;
        internal float ForceBreakupGroanPixels = 6, ForceBreakupSplitPixels = 18, Volume = .55f;
        internal float ForceMaximumSpread = 20;
        internal int ForceCaptureLimit = ForceCaptureLimits.Default;
        internal int ForceBreakupPieceLimit = ForceBreakupLimits.Default;
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
    private static void ShowSaberTooltip(string text) => ImGui.SetTooltip(text);
    private static readonly List<(float2 Center, string Text)> DrawnLabels = [];
    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text,
        float maxWidth = float.MaxValue, float fontSize = 0)
    {
        DrawnLabels.Add((center, text));
        var measured = ImGui.CalcTextSize(text);
        float scale = fontSize > 0 ? Math.Min(1, fontSize / ImGui.GetFontSize()) : 1;
        if (measured.X > 0) scale = Math.Min(scale, maxWidth / measured.X);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
            center - measured * (scale * .5f), color, text, 0);
    }
    private SaberRuntime()
    {
        _forceBreakupClock = new ManualForceBreakupClock();
        _forceLayout = default;
        _owner = new KittenEva(); Program.ControlledVehicle = _owner;
        Program.EditorFlag = Program.Modal = Program.HasMouseInputLatch = false;
        Universe.SimulationSpeed = 1;
        Program.MainViewport = new(); CursorTarget.Hit = true;
        ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
        ImGui.Io.MousePos = new(300, 200);
        ImGui.Tooltip = "";
        ForceTargeting.Picks = 0; ForceTargeting.Next = new(new Vehicle());
        ForceTargeting.FailPick = false;
    }

    public static void Main()
    {
        ForceOverlayChecks.Run();
        ForceThrowDragChecks.Run();
        NativeShiftSampling();
        ForcePickerButtons();
        ForceThrowSlider();
        ForceSettingSliders();
        ForceStatusBox();
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
        var wheelDisplayed = runtime._force.GroupOffsetBody;
        Check(wheelDisplayed.Z > 0 && wheelDisplayed.Z < 1.5 && !runtime._forceMovePending,
            "settled handoff begins smoothing accumulated wheel depth without applying the raw target");
        Check(runtime.ScrollForcePointer(2) && runtime._forceMoveOffset == new double3(0, 0, 2.5)
            && runtime._force.GroupOffsetBody == wheelDisplayed,
            "wheel input during settling adds to the spring target while leaving the displayed group unchanged in the callback");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._forceOffsetSmoother.Target == new double3(0, 0, 2.5)
            && runtime._force.GroupOffsetBody.Z > wheelDisplayed.Z && runtime._force.GroupOffsetBody.Z < 2.5,
            "the settled handoff continues from the visible position toward the accumulated wheel target");
        for (int i = 0; i < 120; i++) runtime.UpdateForceBeforePhysics(1.0 / 60);
        Check((runtime._force.GroupOffsetBody - new double3(0, 0, 2.5)).Length() < 1e-5,
            "idle wheel depth settles at the accumulated requested distance after smoothing");

        var depthBeforeSpread = runtime._forceMoveOffset;
        Check(runtime.ScrollForcePointer(2, shift: true) && runtime.ScrollForcePointer(1, shift: true)
            && Math.Abs(runtime._forceSpreadTarget - Math.Pow(.9, 3)) < 1e-12
            && runtime._forceMoveOffset == depthBeforeSpread && !runtime._forceMovePending,
            "Shift+wheel ticks accumulate a multiplicative spread target without changing pending camera depth");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.GroupSpread < 1 && runtime._force.GroupSpread > runtime._forceSpreadTarget
            && runtime._force.GroupOffsetBody.Z > 2.49,
            "the settled handoff eases group spread while retaining the existing translation target");

        runtime._force.Events.Clear();
        runtime._forceMoveOwner = runtime._owner;
        Check(runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ), "held movement starts");
        runtime.MoveForcePointer(new(80, 0));
        var dragOffsetBeforeSpread = runtime._forceDrag.Offset;
        Check(runtime.ScrollForcePointer(2, shift: true) && runtime._forceDrag.Offset == dragOffsetBeforeSpread
            && runtime._forceMoveOffset == dragOffsetBeforeSpread,
            "Shift+wheel during an active drag changes spread without resetting the translation gesture");
        Check(runtime.ScrollForcePointer(2) && runtime._forceMoveOffset == new double3(8, 0, 1),
            "releasing Shift mid-drag restores ordinary wheel depth while preserving the camera-plane offset");
        runtime.QueueForceAction(ForceAction.Throw);
        Check(runtime.ForceHolding, "throw clicks queue work and retain ownership until the safe handoff");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == runtime._force.GroupOffsetBody
            && runtime._force.ReleasedOffset != new double3(8, 0, 1)
            && runtime._force.ReleasedOffset.X > 0 && runtime._force.ReleasedOffset.X < 8
            && runtime._force.ReleasedSpread >= ForceMath.MinimumGroupSpread && runtime._force.ReleasedSpread < 1
            && runtime._force.GroupSpread == ForceMath.DefaultGroupSpread && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread
            && runtime._force.Events.SequenceEqual(new[] { "Offset", "Spread", "Update", "Throw" }),
            "throw uses displayed translation and spread, then resets both spread factors for the next capture");

        runtime = new(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceMoveOwner = runtime._owner;
        runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ);
        runtime.MoveForcePointer(new(80, 0));
        var finalDragOffset = runtime._forceMoveOffset;
        runtime._forceDrag.Release();
        Check(runtime.ScrollForcePointer(-2) && runtime._forceMoveOffset == finalDragOffset - double3.UnitZ,
            "hover wheel after releasing a drag preserves the pending camera-plane movement");
        runtime.QueueForceAction(ForceAction.Release); runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == runtime._force.GroupOffsetBody
            && runtime._force.ReleasedOffset != finalDragOffset - double3.UnitZ && !runtime.ScrollForcePointer(1),
            "release retains the currently displayed smoothed offset and later wheel input cannot move an unowned group");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._force.GroupOffsetBody = new(2, 0, 0);
        runtime._forceMoveOwner = runtime._owner;
        runtime._forceOffsetSmoother.Reset(runtime._force.GroupOffsetBody);
        runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._forceOffsetSmoother.Advance(1.0 / 60);
        runtime._force.GroupOffsetBody = runtime._forceOffsetSmoother.Offset;
        runtime._forceMoveOffset = new(8, 0, 0); runtime._forceMovePending = true;
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.Move, runtime._forceLayout.MoveCenter)
            && runtime._forceDrag.Offset == runtime._force.GroupOffsetBody
            && runtime._forceMoveOffset == runtime._force.GroupOffsetBody,
            "re-grabbing during motion rebases at the smoothed displayed position instead of the pending endpoint");

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

        runtime = new(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._force.GroupSpread = .75; runtime._forceSpreadTarget = .5;
        runtime._forceMoveOwner = runtime._owner; runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._forceOffsetSmoother.Advance(.016);
        runtime._owner = new(); Program.ControlledVehicle = runtime._owner; runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForceHolding && runtime._forceOffsetSmoother.Offset == double3.Zero
            && runtime._forceOffsetSmoother.Target == double3.Zero && runtime._forceOffsetSmoother.Velocity == double3.Zero
            && runtime._force.GroupSpread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread,
            "an owner switch releases the old capture and clears translation and spread before another kitten can use Force");

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

        runtime = new(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceSpreadTarget = .4; runtime._force.GroupSpread = .5;
        runtime._forceMoveOwner = runtime._owner; runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._force.FailUpdate = true; runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._forceStatus.StartsWith("Force suspended:") && !runtime.ForceHolding
            && runtime._forceOffsetSmoother.Offset == double3.Zero && runtime._forceOffsetSmoother.Target == double3.Zero
            && runtime._forceOffsetSmoother.Velocity == double3.Zero
            && runtime._force.GroupSpread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread,
            "Force handoff failures release locally and clear translation and spread without escaping into global saber reset");
        runtime._forceOffsetSmoother.SetTarget(new(5, 0, 0)); runtime._forceOffsetSmoother.Advance(.016);
        runtime.DisposeForce(); runtime.ResetForce();
        Check(runtime._force.Disposed && runtime._forceOffsetSmoother.Offset == double3.Zero
            && runtime._forceOffsetSmoother.Target == double3.Zero && runtime._forceOffsetSmoother.Velocity == double3.Zero,
            "shutdown clears spring state and a subsequent world reset remains safe and idempotent");
        runtime = new(); runtime.BeginForcePicker(false); ForceTargeting.FailPick = true;
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForcePicking && !runtime._forceClickPending && runtime._force.CaptureCalls == 0
            && runtime._forceStatus.StartsWith("Force picker failed:"),
            "settled picker exceptions cancel locally without skipping the surrounding saber overlay or mutating physics");
        FreshCaptureShiftSpread();
        CaptureLiftRuntime();
        ConfigurableSpreadRuntime();
        ExpansionBreakupRequests();
        BreakupGestureResetAndAudioLifecycle();
        Console.WriteLine("Force UI runtime checks passed (picker button states, deferred clicks, native UI priority, settled commands, stale owners/targets, final move/throw, cleanup).");
    }

    private static SaberRuntime CapturedForLift(bool angry, int capturedCount = 1)
    {
        var runtime = new SaberRuntime();
        runtime._force.SimulatedCapturedCount = capturedCount;
        runtime.BeginForcePicker(angry);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(0);
        return runtime;
    }

    private static void CaptureLiftRuntime()
    {
        var regular = CapturedForLift(false);
        Check(regular.ForceHolding && regular._forceCaptureLift.IsActive
            && regular._forceCaptureLift.Progress == 0 && regular._force.GroupOffsetBody == double3.Zero
            && ReferenceEquals(regular._forceCaptureLiftOwner, regular._owner)
            && ReferenceEquals(regular._forceMoveOwner, regular._owner),
            "a successful regular single-object capture starts one lift at zero in the capture owner's movement handoff");
        regular.UpdateForceBeforePhysics(.1);
        regular.UpdateForceBeforePhysics(.1);
        regular.UpdateForceBeforePhysics(.025);
        Check(regular._force.GroupOffsetBody == new double3(0, 0, -.5)
            && regular._forceOffsetSmoother.Offset == regular._force.GroupOffsetBody
            && regular._forceOffsetSmoother.Target == regular._force.GroupOffsetBody,
            "the settled handoff applies the eased BODY-up offset directly and seeds the spring at the displayed position");

        var angry = CapturedForLift(true, capturedCount: 4);
        angry._force.CanBreakup = true;
        Check(angry._force.CapturedCount == 4 && angry._forceCaptureLift.IsActive,
            "a successful multi-object Angry Force capture starts the same shared group lift");
        angry.UpdateForceBeforePhysics(.05);
        double liftBeforeShift = angry._forceCaptureLift.Progress;
        Check(angry.ScrollForcePointer(1, shift: true) && angry._forceCaptureLift.IsActive,
            "Shift+scroll changes spread without cancelling the capture lift");
        for (int i = 0; i < 4; i++)
            Check(angry.ScrollForcePointer(-.125, shift: true), "fractional breakup input is accepted during the lift");
        angry.UpdateForceBeforePhysics(.016);
        Check(angry._forceBreakupSequence.StageOneActive && angry._force.BreakupActive
            && angry._force.BreakupCalls == 1 && angry._force.CaptureCalls == 1
            && angry._forceCaptureLift.IsActive && angry._forceCaptureLift.Progress > liftBeforeShift,
            "early breakup preparation continues under the same lift without restarting capture feedback");

        var depth = CapturedForLift(false);
        depth.UpdateForceBeforePhysics(.05);
        double3 beforeDepth = depth._force.GroupOffsetBody;
        Check(depth.ScrollForcePointer(1) && !depth._forceCaptureLift.IsActive
            && depth._force.GroupOffsetBody == beforeDepth
            && depth._forceOffsetSmoother.Offset == beforeDepth
            && depth._forceOffsetSmoother.Target == beforeDepth,
            "accepted ordinary depth input cancels the unfinished cue at the visible pose without a snap");
        double3 depthTarget = depth._forceMoveOffset;
        depth.UpdateForceBeforePhysics(.016);
        Check(depth._force.GroupOffsetBody.Z > beforeDepth.Z && depth._force.GroupOffsetBody.Z < depthTarget.Z,
            "manual depth smoothly takes over from the displayed lift position");

        var drag = CapturedForLift(false);
        drag.UpdateForceBeforePhysics(.05);
        double3 beforeDrag = drag._force.GroupOffsetBody;
        drag._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        Check(drag.HandleForceOverlayPress(ForceOverlayHit.Move, drag._forceLayout.MoveCenter)
            && !drag._forceCaptureLift.IsActive && drag._force.GroupOffsetBody == beforeDrag
            && drag._forceOffsetSmoother.Offset == beforeDrag && drag._forceOffsetSmoother.Target == beforeDrag,
            "accepted Move drag input takes over the lift at the displayed group position");
        drag.MoveForcePointer(drag._forceLayout.MoveCenter + new float2(20, 0));
        drag.UpdateForceBeforePhysics(.016);
        Check(drag._force.GroupOffsetBody.X > beforeDrag.X && drag._forceCaptureLift.Progress == 0,
            "manual drag moves the group after takeover without replaying capture feedback");

        var failure = new SaberRuntime();
        ForceTargeting.Next = new(new Vehicle()) { Live = false };
        failure.BeginForcePicker(false);
        failure.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        failure.UpdateForceFrame();
        failure.UpdateForceBeforePhysics(.016);
        Check(failure._force.CaptureCalls == 1 && !failure.ForceHolding
            && !failure._forceCaptureLift.IsActive && failure._forceCaptureLiftOwner == null
            && failure._forceOffsetSmoother.Offset == double3.Zero,
            "a rejected initial capture clears movement state and never starts lift feedback");

        var completed = CapturedForLift(false);
        for (int i = 0; i < 4; i++) completed.UpdateForceBeforePhysics(.1);
        completed.UpdateForceBeforePhysics(.05);
        Check(completed._force.GroupOffsetBody == new double3(0, 0, -1)
            && !completed._forceCaptureLift.IsActive,
            "the runtime retains exactly one metre of BODY-up offset when the lift completes");
        for (int i = 0; i < 4; i++) completed.UpdateForceBeforePhysics(.1);
        Check(completed._force.GroupOffsetBody == new double3(0, 0, -1)
            && completed._force.CaptureCalls == 1 && !completed._forceCaptureLift.IsActive,
            "later ordinary handoffs hold the endpoint without accumulating or replaying the lift");
        completed._forceRequests.Enqueue(new(completed._owner!, ForceAction.Capture, ForceTargeting.Next));
        completed.UpdateForceBeforePhysics(.016);
        Check(completed._force.CaptureCalls == 2 && completed._force.GroupOffsetBody == new double3(0, 0, -1)
            && !completed._forceCaptureLift.IsActive,
            "a capture rejected while a hold exists neither restarts the lift nor changes the held pose");

        var release = CapturedForLift(false);
        release.UpdateForceBeforePhysics(.05);
        release.QueueForceAction(ForceAction.Release);
        release.UpdateForceBeforePhysics(.016);
        Check(!release._forceCaptureLift.IsActive && release._forceCaptureLiftOwner == null
            && release._force.ReleasedOffset == release._force.GroupOffsetBody
            && release._force.ReleasedOffset.Z < 0,
            "release clears the cue after handing off the current displayed group pose");

        var thrown = CapturedForLift(false);
        thrown.UpdateForceBeforePhysics(.05);
        thrown.QueueForceAction(ForceAction.Throw);
        thrown.UpdateForceBeforePhysics(.016);
        Check(!thrown._forceCaptureLift.IsActive && thrown._forceCaptureLiftOwner == null
            && thrown._force.ReleasedOffset == thrown._force.GroupOffsetBody
            && thrown._force.ReleasedThrowSpeed == ForceMath.ThrowSpeed,
            "throw clears the cue and uses the existing pose and speed without adding lift momentum");

        var ownerChange = CapturedForLift(false);
        ownerChange.UpdateForceBeforePhysics(.05);
        ownerChange._owner = new(); Program.ControlledVehicle = ownerChange._owner;
        ownerChange.UpdateForceBeforePhysics(.016);
        Check(!ownerChange.ForceHolding && !ownerChange._forceCaptureLift.IsActive
            && ownerChange._forceCaptureLiftOwner == null,
            "owner change releases the held group and clears its unfinished lift");

        var reset = CapturedForLift(false);
        reset.ResetForce();
        Check(!reset._forceCaptureLift.IsActive && reset._forceCaptureLiftOwner == null,
            "world reset clears the unfinished lift");
        var disposed = CapturedForLift(false);
        disposed.DisposeForce();
        Check(!disposed._forceCaptureLift.IsActive && disposed._forceCaptureLiftOwner == null,
            "runtime disposal clears the unfinished lift");

        var hidden = CapturedForLift(false);
        hidden.OverlayVisible = false;
        hidden.UpdateForceVisibility();
        Check(hidden._forceCaptureLift.IsActive && hidden.ForceHolding,
            "hiding the HUD follows the existing held-group movement policy while the lift continues");
    }

    private static void ConfigurableSpreadRuntime()
    {
        var spread = new SaberRuntime();
        spread._force.HasCapture = true; spread._force.Owner = spread._owner;
        Check(spread.ScrollForcePointer(-.5, shift: true)
            && Math.Abs(spread._forceSpreadTarget - Math.Pow(.9, -.5)) < 1e-12,
            "fractional Shift+scroll remains continuous for group spread input");
        for (int i = 0; i < 15; i++) spread.ScrollForcePointer(-1, shift: true);
        Check(spread._forceSpreadTarget > 4 && spread._forceSpreadTarget <= spread._settings.ForceMaximumSpread,
            "the default spread ceiling allows expansion past the previous four-times limit");
        spread.UpdateForceBeforePhysics(.016);
        Check(spread._force.GroupSpread > 1 && spread._force.GroupSpread < spread._forceSpreadTarget,
            "expanded group spread still eases through the settled handoff");

        var lowered = new SaberRuntime();
        lowered._force.HasCapture = true; lowered._force.Owner = lowered._owner;
        lowered._force.GroupSpread = 10; lowered._forceSpreadTarget = 10;
        lowered._settings.ForceMaximumSpread = 2;
        lowered._forceMoveOwner = lowered._owner;
        lowered._forceOffsetSmoother.Reset(lowered._force.GroupOffsetBody);
        lowered.UpdateForceBeforePhysics(.016);
        Check(lowered._forceSpreadTarget == 2 && lowered._force.GroupSpread < 10
            && lowered._force.GroupSpread > 2,
            "lowering the spread ceiling clamps the target and eases the held group down without teleporting");

        var raised = new SaberRuntime();
        raised._force.HasCapture = true; raised._force.Owner = raised._owner;
        raised._settings.ForceMaximumSpread = 100;
        Check(raised.ScrollForcePointer(-41.5, shift: true)
            && raised._forceSpreadTarget > 75 && raised._forceSpreadTarget < 85,
            "raising the configurable spread ceiling allows Shift+scroll to request about eighty-times spread");
        raised.UpdateForceBeforePhysics(.016);
        for (int i = 0; i < 180; i++) raised.UpdateForceBeforePhysics(1.0 / 60);
        Check(Math.Abs(raised._force.GroupSpread - raised._forceSpreadTarget) < 1e-5,
            "a raised spread ceiling remains reachable after its normal smooth handoff");
    }

    private static void FreshCaptureShiftSpread()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true;
        runtime._force.Owner = runtime._owner;
        Check(runtime.ScrollForcePointer(1, shift: true) && !runtime._forceMovePending
            && runtime._forceSpreadTarget == .9,
            "Shift+wheel over a freshly captured group queues spread without claiming the translation gesture");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.GroupSpread < 1 && runtime._force.GroupSpread > .9
            && runtime._force.GroupOffsetBody == double3.Zero,
            "spread smoothing advances for an idle capture and leaves a single object's translation unchanged");
    }

    private static void NativeShiftSampling()
    {
        var window = new GlfwWindow();
        Check(!SaberRuntime.ForceShiftHeld(window), "native Shift sampling reports released keys");
        window.LeftShift = GlfwKeyAction.Press;
        Check(SaberRuntime.ForceShiftHeld(window), "left Shift is sampled directly from GLFW at scroll-event time");
        window.LeftShift = GlfwKeyAction.Release; window.RightShift = GlfwKeyAction.Press;
        Check(SaberRuntime.ForceShiftHeld(window), "right Shift is sampled directly from GLFW at scroll-event time");
        window.RightShift = GlfwKeyAction.Release;
    }

    private static void ExpansionBreakupRequests()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-1);
        runtime.ScrollForcePointer(1, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 0 && runtime._forceBreakupAudio.Groans == 0,
            "depth scrolling and Shift compression never request breakup or play a cue");

        for (int i = 0; i < 4; i++)
            Check(runtime.ScrollForcePointer(-.125, shift: true), "fractional Shift+scroll is accepted");
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 1 && runtime._forceBreakupAudio.Groans == 1
            && runtime._force.BreakupCommitCalls == 0 && runtime._forceBreakupSequence.TensionPixels == 6,
            "four fractional native scroll events accumulate six continuous pixels and start stage one once");

        runtime.ScrollForcePointer(-1, shift: true);
        AdvanceForce(runtime, 10, elapsedRealSeconds: .016);
        Check(runtime._forceBreakupSequence.SplitRequested && runtime._force.BreakupCommitCalls == 0,
            "a large simulation step cannot substitute for real elapsed time after crossing stage two");
        AdvanceForce(runtime, .1, elapsedRealSeconds: .1);
        Check(runtime._force.BreakupCommitCalls == 0,
            "the monotonic clock remains below the .25 second visual gate");
        AdvanceForce(runtime, .1, elapsedRealSeconds: .1);
        Check(runtime._force.BreakupCommitCalls == 0,
            "real elapsed time just below .25 seconds still cannot commit");
        AdvanceForce(runtime, .1, elapsedRealSeconds: .04);
        Check(runtime._force.BreakupCommitCalls == 1 && runtime._forceBreakupAudio.Breaks == 1,
            "ready breakup commits as soon as .25 seconds of monotonic real time passes and plays one split cue");

        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 1 && runtime._force.BreakupCommitCalls == 1,
            "a committed gesture cannot repeat either cue or source commit");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-1.5, shift: true); // 18 px, both thresholds in one native event.
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 1 && runtime._forceBreakupAudio.Groans == 1
            && runtime._force.BreakupCommitCalls == 0,
            "one event crossing both thresholds starts stage one and latches stage two without skipping its delay");
        runtime.ScrollForcePointer(.6, shift: true); // unwind below split, retain stage one.
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupActive && !runtime._forceBreakupSequence.SplitRequested
            && runtime._force.BreakupCommitCalls == 0,
            "inward scrolling below stage two cancels commit while retaining stage-one preparation");
        runtime.ScrollForcePointer(.6, shift: true); // unwind below stage one.
        AdvanceForce(runtime, .016);
        Check(!runtime._force.BreakupActive && !runtime._forceBreakupSequence.StageOneActive
            && runtime._force.BreakupCancels == 1,
            "inward scrolling below stage one cancels warmed preparation");
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 2 && runtime._forceBreakupAudio.Groans == 2,
            "a later outward crossing after cancellation starts a fresh stage-one cue and delay");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        runtime.ScrollForcePointer(-1, shift: true);
        AdvanceForce(runtime, .016, elapsedRealSeconds: .5);
        Check(runtime._force.BreakupCommitCalls == 1 && runtime._forceBreakupAudio.Breaks == 1,
            "a true frame hitch past .25 seconds commits immediately without waiting extra capped simulation frames");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-1.5, shift: true);
        AdvanceForce(runtime, .016);
        runtime.ScrollForcePointer(-1, shift: true);
        runtime.QueueForceAction(ForceAction.Throw);
        AdvanceForce(runtime, .5);
        Check(runtime._force.BreakupCommitCalls == 0 && !runtime._force.BreakupActive && !runtime.ForceHolding,
            "a pending throw cancels prepared breakup before the handoff can commit it");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime.ScrollForcePointer(-2, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 0 && runtime._forceBreakupAudio.Groans == 0,
            "already broken debris and clutter retain ordinary spread without breakup cues");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime._settings.ForceBreakupPieceLimit = 73;
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupPieceLimits.SequenceEqual([73]),
            "the configured global piece cap reaches the controller when a breakup plan starts");
        runtime._settings.ForceBreakupPieceLimit = 8;
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupCalls == 1 && runtime._force.BreakupCancels == 0
            && runtime._force.BreakupActive && runtime._force.BreakupPieceLimits.SequenceEqual([73]),
            "changing only the cap does not restart an active plan; it keeps its original budget snapshot");
        runtime.ScrollForcePointer(.6, shift: true);
        AdvanceForce(runtime, .016);
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._force.BreakupPieceLimits.SequenceEqual([73, 8]),
            "a new plan picks up the cap edited during the previous plan");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        runtime._settings.ForceBreakupGroanPixels = 10;
        runtime._settings.ForceBreakupSplitPixels = 20;
        AdvanceForce(runtime, .016);
        Check(!runtime._forceBreakupSequence.StageOneActive && !runtime._force.BreakupActive
            && runtime._force.BreakupCancels == 1,
            "editing thresholds cancels active preparation before the new thresholds take effect");

        runtime = new();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-1, shift: true);
        AdvanceForce(runtime, .016);
        runtime._owner = new KittenEva(); Program.ControlledVehicle = runtime._owner;
        AdvanceForce(runtime, .016);
        Check(!runtime.ForceHolding && runtime._force.BreakupCommitCalls == 0,
            "an owner switch releases the old hold before pending breakup can commit");

        var split = new ForceBreakupSequence();
        var whole = split.ApplyScroll(ForceBreakupSequence.PixelsFromNativeScroll(-.5), 4, true);
        var partitioned = new ForceBreakupSequence();
        ForceBreakupInputResult each = default;
        for (int i = 0; i < 4; i++)
            each = partitioned.ApplyScroll(ForceBreakupSequence.PixelsFromNativeScroll(-.125), 4, true);
        Check(whole.StageOneStarted && each.StageOneStarted
            && Math.Abs(split.TensionPixels - partitioned.TensionPixels) < 1e-12,
            "continuous pixel accumulation is invariant to partitioning the same fractional wheel travel");
        var exactSix = new ForceBreakupSequence();
        var exactSixResult = exactSix.ApplyScroll(6, 4, true);
        var tenths = new ForceBreakupSequence();
        ForceBreakupInputResult tenthsResult = default;
        for (int i = 0; i < 10; i++) tenthsResult = tenths.ApplyScroll(.6, 4, true);
        Check(exactSixResult.StageOneStarted && tenthsResult.StageOneStarted
            && tenths.StageOneActive && tenths.TensionPixels < 6 && tenths.TensionPixels > 5.9,
            "relative threshold tolerance accepts ten fractional 0.6px events without quantizing their accumulated distance");
        var fractionalSplit = new ForceBreakupSequence();
        fractionalSplit.ApplyScroll(6, 4, true);
        for (int i = 0; i < 19; i++) fractionalSplit.ApplyScroll(.6, 4, true);
        fractionalSplit.ApplyScroll(.599999999999999, 4, true);
        Check(fractionalSplit.TensionPixels < 18 && fractionalSplit.TensionPixels > 17.9
            && fractionalSplit.SplitRequested,
            "the same distance tolerance handles fractional accumulation at the split threshold");
        var nearUnwind = new ForceBreakupSequence();
        nearUnwind.ApplyScroll(6, 4, true);
        var withinTolerance = nearUnwind.ApplyScroll(-1e-12, 4.1, true);
        var beyondTolerance = nearUnwind.ApplyScroll(-1e-10, 4.2, true);
        Check(!withinTolerance.StageOneCancelled && beyondTolerance.StageOneCancelled
            && nearUnwind.TensionPixels > 5.9,
            "groan-threshold unwind uses the same tiny tolerance and preserves continuous residual distance");
        var residual = new ForceBreakupSequence();
        residual.ApplyScroll(10, 1, true);
        var unwound = residual.ApplyScroll(-5, 1.1, true);
        var rearmed = residual.ApplyScroll(1, 1.2, true);
        Check(unwound.StageOneCancelled && residual.StageOneActive && rearmed.StageOneStarted
            && residual.TensionPixels == 6 && residual.StageOneStartedAt == 1.2,
            "unwinding below stage one retains the remaining pixel distance and rearms with a fresh delay");
        var both = new ForceBreakupSequence();
        var crossed = both.ApplyScroll(ForceBreakupSequence.PixelsFromNativeScroll(-1.5), 0, true);
        Check(crossed.StageOneStarted && crossed.SplitRequested && !both.MinimumDelayElapsed(.249)
            && both.MinimumDelayElapsed(.25),
            "a single scroll event may cross both thresholds, but the exact .25 second gate still applies");
    }

    private static void BreakupGestureResetAndAudioLifecycle()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._force.CanBreakup = true;
        runtime.ScrollForcePointer(-.25, shift: true); // 3px, below stage one.
        AdvanceForce(runtime, .016);
        Check(runtime._forceBreakupSequence.TensionPixels == 3 && runtime._forceBreakupAudio.Groans == 0,
            "subthreshold tension accumulates without starting audio");
        runtime.OverlayVisible = false;
        runtime.UpdateForceVisibility();
        runtime.OverlayVisible = true;
        AdvanceForce(runtime, .016); // consume the deferred cancellation before new input.
        runtime.ScrollForcePointer(-.25, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._forceBreakupSequence.TensionPixels == 3 && runtime._forceBreakupAudio.Groans == 0
            && !runtime._force.BreakupActive,
            "hiding the overlay clears subthreshold tension before a later gesture can reuse it");

        runtime = new SaberRuntime();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._force.CanBreakup = true;
        Universe.SimulationSpeed = 0;
        runtime.ScrollForcePointer(-.5, shift: true);
        AdvanceForce(runtime, .016);
        Check(runtime._forceBreakupAudio.Groans == 1 && runtime._forceBreakupAudio.PausedAtGroan
            && runtime._forceBreakupAudio.LastPaused,
            "the current paused state is applied before the groan channel starts");

        runtime.ScrollForcePointer(-1, shift: true);
        AdvanceForce(runtime, .016, elapsedRealSeconds: .3);
        Check(runtime._forceBreakupAudio.Breaks == 1 && runtime._forceBreakupAudio.PausedAtBreak
            && runtime._forceBreakupAudio.LastPaused,
            "the paused state is also applied before the split channel starts");
        Universe.SimulationSpeed = 1;
        AdvanceForce(runtime, .016);
        AdvanceForce(runtime, .016);
        Check(!runtime._forceBreakupAudioActive && !runtime._forceBreakupAudio.HasActiveChannels,
            "finished one-shot channels clear the runtime audio polling flag");
        int completedUpdates = runtime._forceBreakupAudio.Updates;
        AdvanceForce(runtime, .016);
        Check(runtime._forceBreakupAudio.Updates == completedUpdates,
            "the runtime stops polling audio after both one-shot channels finish");
    }

    private static void AdvanceForce(SaberRuntime runtime, double dtPlayer, double? elapsedRealSeconds = null)
    {
        runtime._playerTime += double.IsFinite(dtPlayer) ? Math.Clamp(dtPlayer, 0, .1) : 0;
        ((ManualForceBreakupClock)runtime._forceBreakupClock).Advance(elapsedRealSeconds ?? dtPlayer);
        runtime.UpdateForceBeforePhysics(dtPlayer);
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
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            runtime.DrawForceOverlayCore(default);
        }
        bool Label(float2 center, string text) => DrawnLabels.Contains((center, text));
        ImDrawListPtr.Circle Fill(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && circle.Filled);
        ImDrawListPtr.Circle Ring(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && !circle.Filled);
        Draw();
        ImGui.Io.MousePos = runtime._forceLayout.MoveCenter;
        Draw();
        Check(DrawnLabels.Any(label => label.Text == "drag + [shift] wheel")
            && ImGui.Tooltip.Contains("Shift+scroll compresses or expands the group"),
            "the Move Force label and hover tip explain Shift+wheel spread control");
        ImGui.Io.MousePos = new(300, 200);
        var idleForce = Fill(forceCenter);
        var idleAngry = Fill(angryCenter);
        var idleForceRing = Ring(forceCenter);
        uint faintWhiteRim = new ImColor8(255, 255, 255, 64).Value;
        Check(Label(forceCenter, "FORCE") && !DrawnLabels.Any(label => label.Text == "CANCEL"),
            "idle Force controls show their action labels");
        Check(idleForceRing.Color == faintWhiteRim
            && ImDrawListPtr.Texts.Any(text => text.Value == "FORCE" && text.Color == new ImColor8(210, 230, 255, 255).Value),
            "the blue Force button keeps its blue label inside a faint white rim");

        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        Draw();
        Check(runtime.ForcePicking && !runtime._angryForce && Label(forceCenter, "CANCEL")
            && !Label(angryCenter, "CANCEL") && Fill(forceCenter).Color == idleForce.Color
            && Ring(forceCenter).Color == faintWhiteRim && Ring(forceCenter).Thickness > idleForceRing.Thickness,
            "the active Force picker keeps the shared blue fill and displays Cancel with a thicker faint white ring");
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
        Check(runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0 && runtime.ForceHolding,
            "pressing Angry Force starts a transient minimum-speed slider without throwing or releasing ownership");
        runtime.MoveForcePointer(angryCenter - new float2(0, runtime._forceLayout.SliderTravel));
        Check(runtime._forceThrowDrag.Speed == ForceThrowDrag.MaximumSpeed
            && runtime._forceRequests.Count == 0,
            "dragging up selects the maximum throw speed without touching the settled queue");
        Check(runtime.CompleteForceThrowSlider(angryCenter - new float2(0, runtime._forceLayout.SliderTravel))
            && runtime._forceRequests.TryPeek(out var thrown) && thrown.Action == ForceAction.Throw
            && thrown.ThrowSpeed == ForceThrowDrag.MaximumSpeed && !runtime._forceThrowDrag.Held
            && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed,
            "left release queues one maximum-speed throw snapshot and resets the transient slider");
        Check(!runtime.CompleteForceThrowSlider(angryCenter) && runtime._forceRequests.Count == 1,
            "a duplicate release cannot enqueue a second throw");
    }

    private static void ForceThrowSlider()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true;
        runtime._force.Owner = runtime._owner;
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
            ImGui.GetFontSize(), ImGui.CalcTextSize("200 m/s").X);
        runtime._forceLayoutAvailable = true;
        runtime._force.HasCapture = true;
        runtime._force.Owner = runtime._owner;
        var center = runtime._forceLayout.AngryCenter;

        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center)
            && runtime._forceThrowDrag.Speed == 25 && runtime._forceRequests.Count == 0,
            "a fresh capture can begin with Shift-independent throw speed at 25 m/s");
        Check(runtime.CompleteForceThrowSlider(center) && runtime._forceRequests.TryPeek(out var defaultRequest)
            && defaultRequest.Action == ForceAction.Throw && defaultRequest.ThrowSpeed == 25
            && runtime._forceThrowDrag.Speed == 25,
            "press-release without dragging queues one default-speed throw");
        runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForceHolding && runtime._force.ReleasedThrowSpeed == 25,
            "the settled release applies the queued default speed and clears capture");

        runtime._forceRequests.Clear(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later capture can start a new throw drag");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel * 4));
        Check(runtime._forceThrowDrag.Speed == 200 && runtime._forceRequests.Count == 0,
            "extreme upward pointer motion clamps to 200 m/s and never throws before release");
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.SliderTravel * 4))
            && runtime._forceRequests.TryPeek(out var maximumRequest) && maximumRequest.ThrowSpeed == 200
            && runtime._forceThrowDrag.Speed == 25,
            "release preserves a maximum-speed request after the visible slider resets to 25 m/s");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedThrowSpeed == 200,
            "the controller receives the immutable selected-speed snapshot at the safe handoff");

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a capture can begin a drag before cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        Program.Modal = true; runtime.UpdateForceVisibility(); Program.Modal = false;
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == 25
            && runtime._forceRequests.Count == 0 && runtime.ForceHolding,
            "opening a native modal cancels the throw gesture without throwing or releasing capture");

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after modal cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        ImGui.Io.AppFocusLost = true;
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.SliderTravel))
            && !runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == 25
            && runtime._forceRequests.Count == 0,
            "a release delivered during application focus loss cancels instead of throwing");
        ImGui.Io.AppFocusLost = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after focus-loss cancellation");
        Program.HasMouseInputLatch = true;
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.SliderTravel))
            && !runtime._forceThrowDrag.Held && runtime._forceRequests.Count == 0,
            "a release whose mouse ownership was lost cancels instead of throwing");
        Program.HasMouseInputLatch = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after input-latch cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        ImGui.Io.WantCaptureKeyboard = true;
        Check(runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == 25
            && runtime._forceRequests.Count == 0
            && runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels a held throw slider even while ImGui wants keyboard input");
        ImGui.Io.WantCaptureKeyboard = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after Escape cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        var replacement = new KittenEva();
        runtime._owner = replacement; Program.ControlledVehicle = replacement;
        runtime.UpdateForceVisibility();
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == 25
            && runtime._forceRequests.Count == 0,
            "controlled-owner loss cancels the transient throw without queueing work");
        runtime._owner = runtime._force.Owner; Program.ControlledVehicle = runtime._owner;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after owner recovery");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        runtime.OverlayVisible = false; runtime.UpdateForceVisibility(); runtime.OverlayVisible = true;
        Check(!runtime._forceThrowDrag.Held && runtime._forceRequests.Count == 0,
            "hiding the HUD cancels the throw gesture without a throw");

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after HUD cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.SliderTravel));
        runtime._force.HasCapture = false;
        runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == 25
            && runtime._forceRequests.Count == 0,
            "capture loss cancels the throw gesture without queueing a throw");

        var errorRuntime = new SaberRuntime();
        errorRuntime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        errorRuntime._forceLayoutAvailable = true;
        errorRuntime._force.HasCapture = true; errorRuntime._force.Owner = errorRuntime._owner;
        errorRuntime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, errorRuntime._forceLayout.AngryCenter);
        errorRuntime._force.FailUpdate = true;
        errorRuntime.UpdateForceBeforePhysics(.016);
        Check(!errorRuntime._forceThrowDrag.Held && errorRuntime._forceThrowDrag.Speed == 25
            && errorRuntime._forceRequests.Count == 0 && !errorRuntime.ForceHolding,
            "a settled Force error cancels the slider and releases safely without throwing");

        var disposeRuntime = new SaberRuntime();
        disposeRuntime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        disposeRuntime._forceLayoutAvailable = true;
        disposeRuntime._force.HasCapture = true; disposeRuntime._force.Owner = disposeRuntime._owner;
        disposeRuntime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, disposeRuntime._forceLayout.AngryCenter);
        disposeRuntime.DisposeForce();
        Check(!disposeRuntime._forceThrowDrag.Held && disposeRuntime._forceThrowDrag.Speed == 25
            && disposeRuntime._forceRequests.Count == 0 && disposeRuntime._force.Disposed,
            "runtime disposal cancels the transient slider without a throw");

        Program.ControlledVehicle = runtime._owner;
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
            ImGui.GetFontSize(), ImGui.CalcTextSize("200 m/s").X);
        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, runtime._forceLayout.AngryCenter)
            && runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Matches(runtime._forceLayout),
            "the throw pill can be drawn for an active gesture");
        runtime.MoveForcePointer(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.SliderTravel));
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceOverlayCore(default);
        var pill = ImDrawListPtr.Rects.Single(rect => rect.Filled);
        var rim = ImDrawListPtr.Rects.Single(rect => !rect.Filled);
        var thumb = ImDrawListPtr.Circles.Single(circle => circle.Filled
            && circle.Center == runtime._forceThrowDrag.Thumb);
        var label = ImDrawListPtr.Texts.Last(text => text.Value == "200 m/s");
        int labelDrawn = ImDrawListPtr.Events.FindLastIndex(e => e.Kind == "text" && e.Text == "200 m/s");
        int lastCircle = ImDrawListPtr.Events.FindLastIndex(e => e.Kind is "circle-filled" or "circle");
        Check(pill.Min == runtime._forceLayout.SliderEnvelope(ForceOverlayHit.AngryForce).Min
            && pill.Min.Y < runtime._forceLayout.AngryCenter.Y && pill.Max.Y > runtime._forceLayout.AngryCenter.Y
            && rim.Min == pill.Min && rim.Max == pill.Max && rim.Color == new ImColor8(255, 255, 255, 64).Value
            && Math.Abs(thumb.Radius * 2 - (pill.Max.X - pill.Min.X) * .8f) < 1e-3f
            && !DrawnLabels.Any(value => value.Text == "THROW")
            && !DrawnLabels.Any(value => value.Center.X == runtime._forceLayout.AngryCenter.X
                && (value.Text is "THROW" or "ANGRY" or "FORCE"))
            && !ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == runtime._forceLayout.AngryCenter),
            "the active throw control draws only its rounded upward pill, faint rim, and 80%-width thumb without ghosted button labels");
        Check(label.Size == ImGui.GetFontSize() && label.Wrap == 0 && label.Position.X >= pill.Max.X
            && label.Position.X + ImGui.CalcTextSize("200 m/s").X <= runtime._forceLayout.ControlsMax.X + 1e-3f
            && labelDrawn > lastCircle,
            "the external speed value stays regular-sized, single-line, within bounds, and above every control circle");
        runtime.CancelForceThrowDrag();

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, runtime._forceLayout.AngryCenter),
            "delayed owner verification starts from a clean default slider");
        runtime.MoveForcePointer(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.SliderTravel));
        Check(runtime.CompleteForceThrowSlider(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.SliderTravel))
            && runtime._forceRequests.TryPeek(out var delayed) && delayed.ThrowSpeed == 200,
            "a released speed is snapshotted before delayed owner verification");
        int throwsBeforeOwnerChange = runtime._force.Events.Count(value => value == "Throw");
        var oldOwner = runtime._owner;
        var newOwner = new KittenEva(); runtime._owner = newOwner; Program.ControlledVehicle = newOwner;
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.Events.Count(value => value == "Throw") == throwsBeforeOwnerChange
            && runtime._forceRequests.Count == 0,
            "an owner change before the safe handoff cancels the queued throw instead of applying it");
        runtime._owner = oldOwner; Program.ControlledVehicle = oldOwner;
    }

    private static void ForceSettingSliders()
    {
        var runtime = new SaberRuntime();
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        var originalLayout = runtime._forceLayout;
        runtime._forceLayoutAvailable = true;
        var count = runtime._forceLayout.CaptureLimitCenter;
        var distance = runtime._forceLayout.AngryRadiusCenter;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count)
            && runtime._forceSettingDrag.Held && runtime._settings.ForceCaptureLimit == ForceCaptureLimits.Default,
            "pressing count opens its pill without changing the shared setting");
        runtime.MoveForcePointer(count + new float2(10000, runtime._forceLayout.SliderTravel * .5f));
        Check(runtime._settings.ForceCaptureLimit == 384,
            "count drag writes the rounded value directly to shared settings");
        runtime.MoveForcePointer(count + new float2(-10000, 10000));
        Check(runtime._settings.ForceCaptureLimit == ForceCaptureLimits.Maximum
            && !runtime._forceMovePending && runtime._force.CaptureCalls == 0 && runtime._forceQuery == null,
            "a held settings drag keeps updating outside its bounds without moving or targeting the world");

        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceOverlayCore(default);
        var pill = ImDrawListPtr.Rects.Single(rect => rect.Filled);
        var pillRim = ImDrawListPtr.Rects.Single(rect => !rect.Filled);
        uint blackHalf = new ImColor8(0, 0, 0, 128).Value;
        uint blackRim = new ImColor8(255, 255, 255, 64).Value;
        var thumb = ImDrawListPtr.Circles.Single(circle => circle.Filled
            && circle.Center == runtime._forceSettingDrag.Thumb(runtime._settings.ForceCaptureLimit));
        var valueLabel = ImDrawListPtr.Texts.Last(text => text.Value == ForceCaptureLimits.Maximum.ToString());
        Check(Math.Abs(thumb.Radius * 2 - (pill.Max.X - pill.Min.X) * .8f) < 1e-3f
            && valueLabel.Size == ImGui.GetFontSize() && valueLabel.Position.X >= pill.Max.X
            && Math.Abs(valueLabel.Position.Y + valueLabel.Size * .5f - thumb.Center.Y) < 1e-3f
            && valueLabel.Wrap == 0 && valueLabel.Position.X <= runtime._forceLayout.ControlsMax.X + 1e-3f,
            "the slider nub fills 80% of its pill; its regular-size, no-wrap value sits to the right and centers on the nub");
        var valueDraws = ImDrawListPtr.Texts.Where(text => text.Value == ForceCaptureLimits.Maximum.ToString()
            && text.Position.X >= pill.Max.X).ToArray();
        int forceFill = ImDrawListPtr.Events.FindIndex(e => e.Kind == "circle-filled" && e.Center == runtime._forceLayout.ForceCenter);
        int angryFill = ImDrawListPtr.Events.FindIndex(e => e.Kind == "circle-filled" && e.Center == runtime._forceLayout.AngryCenter);
        int valueForeground = ImDrawListPtr.Events.FindLastIndex(e => e.Kind == "text"
            && e.Text == ForceCaptureLimits.Maximum.ToString());
        int lastCircle = ImDrawListPtr.Events.FindLastIndex(e => e.Kind is "circle-filled" or "circle");
        Check(valueDraws.Length == 2 && valueDraws[0].Color == new ImColor8(0, 0, 0, 255).Value
            && valueDraws[1].Color == new ImColor8(255, 255, 255, 255).Value
            && valueDraws[0].Position == valueDraws[1].Position + new float2(runtime._forceLayout.Scale)
            && forceFill >= 0 && angryFill >= 0 && valueForeground > forceFill && valueForeground > angryFill
            && lastCircle >= 0 && valueForeground > lastCircle,
            "the active count value draws after every circle with a readable dark shadow behind its white label");
        Check(pill.Min == runtime._forceSettingDrag.PillMin && pill.Max == runtime._forceSettingDrag.PillMax
            && pill.Color == blackHalf && pill.Rounding == runtime._forceLayout.ButtonRadius
            && pillRim.Min == pill.Min && pillRim.Max == pill.Max && pillRim.Color == blackRim
            && pillRim.Rounding == pill.Rounding && pillRim.Thickness == Math.Max(.5f, runtime._forceLayout.Scale),
            "the selected setting draws as a rounded black pill with the shared faint white rim");
        Check(ImDrawListPtr.Circles.Any(circle => !circle.Filled && circle.Center == runtime._forceLayout.MoveCenter
                && circle.Color == blackRim && circle.Thickness == Math.Max(.5f, runtime._forceLayout.Scale))
            && ImDrawListPtr.Circles.Any(circle => !circle.Filled && circle.Center == distance
                && circle.Color == blackRim && circle.Thickness == Math.Max(.5f, runtime._forceLayout.Scale)),
            "Move Force and the inactive distance setting use the same faint white rim");
        ImGui.Io.MousePos = count;
        runtime.DrawForceOverlayCore(default);
        Check(ImGui.Tooltip == "max # objects force can capture",
            "the capture-count tooltip uses the requested exact lowercase text");
        runtime._forceLayout = originalLayout.WithControlsOffset(float2.Zero, new(1920, 1080), new(.02f, 0));
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceOverlayCore(default);
        Check(!runtime._forceSettingDrag.Held && ImDrawListPtr.Rects.Count == 0,
            "a viewport or offset layout change collapses stale press-time pill geometry");
        runtime._forceLayout = originalLayout;
        runtime._settings.AngryForceRadius = 14.6f;
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceOverlayCore(default);
        Check(ImDrawListPtr.Rects.Count == 0
            && ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == count
                && circle.Radius == runtime._forceLayout.ButtonRadius && circle.Color == blackHalf)
            && ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == distance
                && circle.Radius == runtime._forceLayout.ButtonRadius && circle.Color == blackHalf)
            && ImDrawListPtr.Circles.Any(circle => !circle.Filled && circle.Center == count && circle.Color == blackRim)
            && ImDrawListPtr.Circles.Any(circle => !circle.Filled && circle.Center == distance && circle.Color == blackRim)
            && DrawnLabels.Any(label => label.Center.Y < count.Y && label.Text == "COUNT")
            && DrawnLabels.Any(label => label.Center.Y < distance.Y && label.Text == "DIST")
            && DrawnLabels.Any(label => label.Center.Y > count.Y && label.Text == ForceCaptureLimits.Maximum.ToString())
            && DrawnLabels.Any(label => label.Center.Y > distance.Y && label.Text == "15m")
            && runtime._settings.AngryForceRadius == 14.6f,
            "closed count and distance circles show caption above rounded current values while retaining the precise radius");
        ImGui.Io.MousePos = distance;
        runtime.DrawForceOverlayCore(default);
        Check(ImGui.Tooltip == "force capture bubble size",
            "the capture-radius tooltip uses the requested exact lowercase text");

        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryRadius, distance);
        runtime.MoveForcePointer(distance - new float2(0, 10000));
        Check(runtime._settings.AngryForceRadius == 1 && runtime._settings.ForceCaptureLimit == ForceCaptureLimits.Maximum,
            "distance drag clamps its own shared setting without maintaining a separate value copy");
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceOverlayCore(default);
        var distancePill = ImDrawListPtr.Rects.Single(rect => rect.Filled);
        var distanceRim = ImDrawListPtr.Rects.Single(rect => !rect.Filled);
        var distanceThumb = ImDrawListPtr.Circles.Single(circle => circle.Filled
            && circle.Center == runtime._forceSettingDrag.Thumb(runtime._settings.AngryForceRadius));
        var distanceValue = ImDrawListPtr.Texts.Last(text => text.Value == "1m" && text.Position.X >= distancePill.Max.X);
        int distanceValueForeground = ImDrawListPtr.Events.FindLastIndex(e => e.Kind == "text" && e.Text == "1m");
        int distanceLastCircle = ImDrawListPtr.Events.FindLastIndex(e => e.Kind is "circle-filled" or "circle");
        Check(distanceRim.Color == blackRim && distanceRim.Min == distancePill.Min && distanceRim.Max == distancePill.Max
            && Math.Abs(distanceThumb.Radius * 2 - (distancePill.Max.X - distancePill.Min.X) * .8f) < 1e-3f
            && distanceValue.Size == ImGui.GetFontSize() && distanceValue.Wrap == 0
            && Math.Abs(distanceValue.Position.Y + distanceValue.Size * .5f - distanceThumb.Center.Y) < 1e-3f
            && distanceValueForeground > distanceLastCircle,
            "the active distance value and faint-rimmed pill draw after all circles, with the value centered on its 80% nub");
        var replacement = new KittenEva(); runtime._owner = replacement; Program.ControlledVehicle = replacement;
        runtime.UpdateForceVisibility();
        Check(!runtime._forceSettingDrag.Held, "changing the controlled owner collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryRadius, distance);
        Program.Modal = true; runtime.UpdateForceVisibility(); Program.Modal = false;
        Check(!runtime._forceSettingDrag.Held, "opening a native modal collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryRadius, distance);
        runtime.OverlayVisible = false; runtime.UpdateForceVisibility(); runtime.OverlayVisible = true;
        Check(!runtime._forceSettingDrag.Held, "hiding the overlay collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        Check(runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !runtime._forceSettingDrag.Held
            && runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels a settings drag and consumes its matching key release");
    }

    private static void ForceStatusBox()
    {
        var runtime = new SaberRuntime
        {
            _forceLayout = ForceOverlayLayout.Create(
                SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080)),
            _forceLayoutAvailable = true
        };
        void Draw()
        {
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            runtime.DrawForceOverlayCore(default);
        }
        string ReadStatusText()
        {
            uint white = new ImColor8(255, 255, 255, 255).Value;
            var lines = ImDrawListPtr.Texts
                .Where(text => text.Color == white
                    && text.Position.X >= runtime._forceLayout.StatusMin.X
                    && text.Position.X < runtime._forceLayout.StatusMax.X
                    && text.Position.Y >= runtime._forceLayout.StatusMin.Y
                    && text.Position.Y < runtime._forceLayout.StatusMax.Y
                    && text.Wrap == 0)
                .OrderBy(text => text.Position.Y)
                .Select(text => text.Value);
            return string.Join(" ", lines).Trim();
        }

        runtime.SetForceStatus("Angry Force: select a target and objects within 14 m of the hit.", ForceStatusSeverity.None);
        Draw();
        Check(!ImDrawListPtr.Texts.Any(text => text.Value.Contains("Angry Force")),
            "normal picker instructions do not appear in the Force status box");

        const string warning = "Force capture refused: the target is no longer available.";
        runtime.SetForceStatus(warning, ForceStatusSeverity.Warning);
        Draw();
        Check(ReadStatusText() == warning,
            "explicit warnings render inside the centered status bounds above Move");

        const string error = "Force update failed: body state was invalid.";
        runtime.SetForceStatus(error, ForceStatusSeverity.Error);
        Draw();
        Check(ReadStatusText() == error,
            "explicit Force errors render in the status box");

        runtime._force.Status = "Force release partial failure";
        runtime._force.StatusSeverity = ForceStatusSeverity.Warning;
        runtime.SyncForceStatusFromController();
        Draw();
        Check(ReadStatusText() == runtime._force.Status,
            "controller warnings are forwarded through explicit severity");

        runtime._forceRequests.Enqueue(new(runtime._owner!, ForceAction.Capture, ForceTargeting.Next));
        runtime.UpdateForceBeforePhysics(.016);
        Draw();
        Check(runtime._force.HasCapture && runtime._forceStatusSeverity == ForceStatusSeverity.None
            && !ImDrawListPtr.Texts.Any(text => text.Value.Contains("Force release partial failure")),
            "a successful capture clears the obsolete controller warning and status text");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
