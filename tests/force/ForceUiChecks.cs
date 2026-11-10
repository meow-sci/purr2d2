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
    private bool ForceUiVisible => OverlayVisible && _settings.ForceUiEnabled;
    private SaberOverlayLayout _overlayLayout = default;
    private sealed class TestSettings
    {
        internal bool ForceUiEnabled = true;
        internal float AngryForceRadius = 10, ClutterPickTolerance = .2f;
        internal float ForceBreakupGroanPixels = 6, ForceBreakupSplitPixels = 18, Volume = .55f;
        internal float ForceMaximumSpread = 20;
        internal float ForceBubbleLineLengthPercent = 100;
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
    private SaberType SaberTypeFor(KittenEva kitten) => SaberType.Light;
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
    private void DrawForceFrameForChecks()
    {
        RefreshForceHoverPreview();
        DrawForceWorldOverlay(default);
        DrawForceOverlayCore(default);
    }
    private bool _arrangeHud = false;
    private bool HudApplicationHasFocus => !ImGui.Io.AppFocusLost;
    private GlfwWindow HudModifierWindow => Program.GetWindow();
    private bool HudWindowAcceptsInput(GlfwWindow window) => window == Program.GetWindow();
    private bool HudKeyboardOwnsInput(GlfwWindow window) => false;
    private SaberOverlayHit HudCombinedHit(float2 pointer) => _overlayLayout.Hit(pointer);
    private SaberRuntime()
    {
        _forceBreakupClock = new ManualForceBreakupClock();
        _forceLayout = default;
        _owner = new KittenEva(); Program.ControlledVehicle = _owner;
        Program.EditorFlag = Program.Modal = Program.HasMouseInputLatch = false;
        Program.Window.LeftShift = Program.Window.RightShift = GlfwKeyAction.Release;
        Universe.SimulationSpeed = 1;
        Program.MainViewport = new(); CursorTarget.Hit = true;
        ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
        ImGui.Io.MousePos = new(300, 200);
        ImGui.Tooltip = "";
        ForceTargeting.Picks = 0; ForceTargeting.PreviewCalls = 0; ForceTargeting.ValidCalls = 0;
        ForceTargeting.Next = new(new Vehicle());
        ForceTargeting.FailPick = false;
    }

    public static void Main()
    {
        ForceRotationDragChecks.Run();
        ForceRotationHandle();
        ForceRotationShiftAxes();
        ForceCaptureRadiusDragChecks.Run();
        ForceCaptureBubbleChecks.Run();
        ForceOverlayChecks.Run();
        ForceThrowDragChecks.Run();
        NativeShiftSampling();
        ForcePickerButtons();
        ForceCaptureRadiusSelection();
        ForceNativeHoverPreview();
        ForceThrowSlider();
        ForceCaptureRadiusGesture();
        ForceSettingSliders();
        ForceStatusBox();
        var runtime = new SaberRuntime { _selectingSlice = true };
        runtime._settings.AngryForceRadius = 14;
        runtime._settings.ClutterPickTolerance = .375f;
        runtime._settings.ForceCaptureLimit = 256;
        runtime.BeginForcePicker(true);
        Check(runtime._forceStatus.Contains("2 m"), "the Angry Force instruction starts with the two metre capture radius");
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
            && ForceTargeting.LastRay.Origin == new double3(80, 60, 7) && runtime._force.CapturedRadius == 2
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0,
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
        double displayedSpreadBeforeInput = runtime._force.GroupSpread;
        Check(runtime.ScrollForcePointer(2, shift: true) && runtime.ScrollForcePointer(1, shift: true)
            && Math.Abs(runtime._forceSpreadTarget - Math.Pow(.9, 3)) < 1e-12
            && runtime._force.GroupSpread == displayedSpreadBeforeInput
            && runtime._forceMoveOffset == depthBeforeSpread && !runtime._forceMovePending,
            "Shift+wheel ticks queue a multiplicative spread target without changing the displayed factor or pending camera depth");
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
        double throwSpreadTarget = runtime._forceSpreadTarget;
        runtime.QueueForceAction(ForceAction.Throw);
        Check(runtime.ForceHolding, "throw clicks queue work and retain ownership until the safe handoff");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == runtime._force.GroupOffsetBody
            && runtime._force.ReleasedOffset != new double3(8, 0, 1)
            && runtime._force.ReleasedOffset.X > 0 && runtime._force.ReleasedOffset.X < 8
            && runtime._force.ReleasedSpread >= ForceMath.MinimumGroupSpread && runtime._force.ReleasedSpread < 1
            && runtime._force.ReleasedSpread > throwSpreadTarget
            && runtime._force.GroupSpread == ForceMath.DefaultGroupSpread && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0
            && runtime._force.Events.SequenceEqual(new[] { "Offset", "Spread", "Update", "Throw" }),
            "throw snapshots the eased displayed spread rather than its pending target, then resets spread for the next capture");

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
            && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0,
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

        runtime = new(); runtime.BeginForcePicker(false);
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._settings.ForceUiEnabled = false; runtime.UpdateForceVisibility();
        Check(runtime.OverlayVisible && runtime.ForceHolding && !runtime._selectingForce && !runtime._forceLayoutAvailable,
            "hiding only the Furce UI cancels its picker without releasing held objects or hiding other controls");

        runtime = new(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceSpreadTarget = .4; runtime._force.GroupSpread = .5;
        runtime._forceMoveOwner = runtime._owner; runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._force.FailUpdate = true; runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._forceStatus.StartsWith("Furce suspended:") && !runtime.ForceHolding
            && runtime._forceOffsetSmoother.Offset == double3.Zero && runtime._forceOffsetSmoother.Target == double3.Zero
            && runtime._forceOffsetSmoother.Velocity == double3.Zero
            && runtime._force.GroupSpread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadTarget == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0,
            "Force handoff failures release locally and clear translation and spread without escaping into global saber reset");
        runtime._forceOffsetSmoother.SetTarget(new(5, 0, 0)); runtime._forceOffsetSmoother.Advance(.016);
        runtime.DisposeForce(); runtime.ResetForce();
        Check(runtime._force.Disposed && runtime._forceOffsetSmoother.Offset == double3.Zero
            && runtime._forceOffsetSmoother.Target == double3.Zero && runtime._forceOffsetSmoother.Velocity == double3.Zero
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0,
            "shutdown clears spring state and a subsequent world reset remains safe and idempotent");
        runtime = new(); runtime.BeginForcePicker(false); ForceTargeting.FailPick = true;
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForcePicking && !runtime._forceClickPending && runtime._force.CaptureCalls == 0
            && runtime._forceStatus.StartsWith("Furce picker failed:"),
            "settled picker exceptions cancel locally without skipping the surrounding saber overlay or mutating physics");
        FreshCaptureShiftSpread();
        CaptureLiftRuntime();
        ConfigurableSpreadRuntime();
        ForceSpreadRuntimeSmoothing();
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
        Check(angry.ScrollForcePointer(.1, shift: true), "fractional spread input can start during capture lift");
        angry.UpdateForceBeforePhysics(.016);
        double liftBeforeShift = angry._forceCaptureLift.Progress;
        double spreadBeforeShift = angry._forceSpreadSmoother.Spread;
        double velocityBeforeShift = angry._forceSpreadSmoother.Velocity;
        Check(angry.ScrollForcePointer(1, shift: true) && angry._forceCaptureLift.IsActive
            && angry._forceSpreadSmoother.Spread == spreadBeforeShift
            && angry._forceSpreadSmoother.Velocity == velocityBeforeShift,
            "Shift+scroll changes the target without cancelling the capture lift or restarting spread motion");
        for (int i = 0; i < 4; i++)
            Check(angry.ScrollForcePointer(-.125, shift: true)
                && angry._forceSpreadSmoother.Spread == spreadBeforeShift
                && angry._forceSpreadSmoother.Velocity == velocityBeforeShift,
                "fractional breakup input is accepted during the lift without restarting spread motion");
        angry.UpdateForceBeforePhysics(.016);
        Check(angry._forceBreakupSequence.StageOneActive && angry._force.BreakupActive
            && angry._force.BreakupCalls == 1 && angry._force.CaptureCalls == 1
            && angry._forceCaptureLift.IsActive && angry._forceCaptureLift.Progress > liftBeforeShift
            && angry._forceSpreadSmoother.Velocity < 0,
            "early breakup preparation continues under the same lift without restarting capture feedback");

        var depth = CapturedForLift(false);
        depth.UpdateForceBeforePhysics(.05);
        depth.ScrollForcePointer(.1, shift: true);
        depth.UpdateForceBeforePhysics(.016);
        double spreadBeforeDepth = depth._forceSpreadSmoother.Spread;
        double spreadVelocityBeforeDepth = depth._forceSpreadSmoother.Velocity;
        double3 beforeDepth = depth._force.GroupOffsetBody;
        Check(depth.ScrollForcePointer(1) && !depth._forceCaptureLift.IsActive
            && depth._force.GroupOffsetBody == beforeDepth
            && depth._forceOffsetSmoother.Offset == beforeDepth
            && depth._forceOffsetSmoother.Target == beforeDepth
            && depth._forceSpreadSmoother.Spread == spreadBeforeDepth
            && depth._forceSpreadSmoother.Velocity == spreadVelocityBeforeDepth,
            "ordinary depth input takes over translation without restarting the independent spread spring");
        double3 depthTarget = depth._forceMoveOffset;
        depth.UpdateForceBeforePhysics(.016);
        Check(depth._force.GroupOffsetBody.Z > beforeDepth.Z && depth._force.GroupOffsetBody.Z < depthTarget.Z,
            "manual depth smoothly takes over from the displayed lift position");

        var drag = CapturedForLift(false);
        drag.UpdateForceBeforePhysics(.05);
        drag.ScrollForcePointer(.1, shift: true);
        drag.UpdateForceBeforePhysics(.016);
        double spreadBeforeDrag = drag._forceSpreadSmoother.Spread;
        double spreadVelocityBeforeDrag = drag._forceSpreadSmoother.Velocity;
        double3 beforeDrag = drag._force.GroupOffsetBody;
        drag._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        Check(drag.HandleForceOverlayPress(ForceOverlayHit.Move, drag._forceLayout.MoveCenter)
            && !drag._forceCaptureLift.IsActive && drag._force.GroupOffsetBody == beforeDrag
            && drag._forceOffsetSmoother.Offset == beforeDrag && drag._forceOffsetSmoother.Target == beforeDrag
            && drag._forceSpreadSmoother.Spread == spreadBeforeDrag
            && drag._forceSpreadSmoother.Velocity == spreadVelocityBeforeDrag,
            "accepted Move drag input takes over translation at the displayed pose without restarting spread motion");
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
            && lowered._force.GroupSpread > 2
            && lowered._forceSpreadSmoother.Spread == lowered._force.GroupSpread
            && lowered._forceSpreadSmoother.Target == 2,
            "lowering the spread ceiling resynchronizes an externally seeded factor and eases it toward the clamped target");

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

    private static void ForceSpreadRuntimeSmoothing()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        double initial = runtime._force.GroupSpread;
        Check(runtime.ScrollForcePointer(1, shift: true) && runtime._force.GroupSpread == initial,
            "Shift+wheel queues spread input without changing the displayed controller factor");
        double target = runtime._forceSpreadTarget;
        runtime.UpdateForceBeforePhysics(.016);
        double first = runtime._force.GroupSpread;
        double onePoleFirstStep = Math.Abs(Math.Log(target))
            * (1 - Math.Exp(-ForceOffsetSmoother.NaturalFrequency * .016));
        Check(first < initial && first > target
            && Math.Abs(Math.Log(first / initial)) < onePoleFirstStep,
            "the runtime begins spread motion with a gentler first increment than the previous one-pole curve");

        double previous = first;
        for (int i = 0; i < 600 && runtime._force.GroupSpread != target; i++)
        {
            runtime.UpdateForceBeforePhysics(.016);
            Check(runtime._force.GroupSpread <= previous && runtime._force.GroupSpread >= target,
                "spread settles monotonically after wheel input stops");
            previous = runtime._force.GroupSpread;
        }
        Check(runtime._force.GroupSpread == target && runtime._forceSpreadSmoother.Velocity == 0,
            "the settled runtime spread reaches the exact target with zero velocity");
        int settledWrites = runtime._force.Events.Count(item => item == "Spread");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.Events.Count(item => item == "Spread") == settledWrites,
            "a stationary spread does not issue redundant controller writes");
        runtime.ResetForce();
        Check(runtime._force.GroupSpread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && runtime._forceSpreadSmoother.Velocity == 0,
            "world reset seeds the spread spring from the controller's restored default factor");

        var reversal = new SaberRuntime();
        reversal._force.HasCapture = true; reversal._force.Owner = reversal._owner;
        reversal.ScrollForcePointer(1, shift: true);
        for (int i = 0; i < 5; i++) reversal.UpdateForceBeforePhysics(.016);
        double beforeReverse = reversal._force.GroupSpread;
        double velocityBeforeReverse = reversal._forceSpreadSmoother.Velocity;
        Check(velocityBeforeReverse < 0 && reversal.ScrollForcePointer(-2, shift: true)
            && reversal._force.GroupSpread == beforeReverse
            && reversal._forceSpreadSmoother.Velocity == velocityBeforeReverse
            && reversal._forceSpreadTarget > 1,
            "a rapid reversal queues the new target while preserving current display and spring velocity");
        reversal.UpdateForceBeforePhysics(.008);
        Check(reversal._force.GroupSpread < beforeReverse && reversal._forceSpreadSmoother.Velocity < 0,
            "the settled handoff carries a finite amount of existing motion through a reversal");
        double reverseTarget = reversal._forceSpreadTarget;
        for (int i = 0; i < 600 && reversal._force.GroupSpread != reverseTarget; i++)
            reversal.UpdateForceBeforePhysics(.016);
        Check(reversal._force.GroupSpread == reverseTarget && reversal._forceSpreadSmoother.Velocity == 0,
            "the reversed runtime target settles without ringing or overshoot");

        var release = new SaberRuntime();
        release._force.HasCapture = true; release._force.Owner = release._owner;
        release.ScrollForcePointer(1, shift: true);
        double releaseTarget = release._forceSpreadTarget;
        release.QueueForceAction(ForceAction.Release);
        release.UpdateForceBeforePhysics(.016);
        Check(release._force.ReleasedSpread > releaseTarget && release._force.ReleasedSpread < 1
            && release._force.GroupSpread == ForceMath.DefaultGroupSpread
            && release._forceSpreadSmoother.Spread == ForceMath.DefaultGroupSpread
            && release._forceSpreadSmoother.Target == ForceMath.DefaultGroupSpread
            && release._forceSpreadSmoother.Velocity == 0,
            "release snapshots the eased displayed factor, then clears spread state for the next capture");
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
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            runtime.DrawForceFrameForChecks();
        }
        bool Label(float2 center, string text) => DrawnLabels.Contains((center, text));
        ImDrawListPtr.Circle Fill(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && circle.Filled);
        ImDrawListPtr.Circle Ring(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && !circle.Filled);
        Draw();
        ImGui.Io.MousePos = runtime._forceLayout.MoveCenter;
        Draw();
        Check(DrawnLabels.Any(label => label.Text == "drag + [shift] wheel"
                && label.Center.X == runtime._forceLayout.MoveCenter.X
                && label.Center.Y > runtime._forceLayout.MoveCenter.Y)
            && DrawnLabels.Any(label => label.Text == "THE FURCE"
                && label.Center.X == runtime._forceLayout.MoveCenter.X
                && label.Center.Y < runtime._forceLayout.MoveCenter.Y)
            && ImGui.Tooltip == "",
            "the Move Force circle centers its title above its hint without a hover tooltip");
        ImGui.Io.MousePos = new(300, 200);
        var idleForce = Fill(forceCenter);
        var idleAngry = Fill(angryCenter);
        var idleForceRing = Ring(forceCenter);
        uint faintWhiteRim = new ImColor8(255, 255, 255, 64).Value;
        Check(Label(forceCenter, "FURCE") && !DrawnLabels.Any(label => label.Text == "CANCEL"),
            "idle Force controls show their action labels");
        Check(idleForceRing.Color == faintWhiteRim
            && ImDrawListPtr.Texts.Any(text => text.Value == "FURCE" && text.Color == new ImColor8(210, 230, 255, 255).Value),
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
            && runtime._forceRequests.Count == 0 && Label(forceCenter, "FURCE") && Fill(forceCenter) == idleForce,
            "pressing the active Force button cancels selection, clears pending capture and restores its label and color");

        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(300, 200)); runtime.UpdateForceFrame();
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter); Draw();
        Check(!runtime.ForcePicking && runtime._forceCaptureRadiusDrag.Held
            && runtime._forceCaptureRadiusDrag.Radius == 2
            && runtime._forceQuery == null && !runtime._forceClickPending
            && Label(forceCenter, "FURCE") && ImDrawListPtr.Rects.Any(rect => rect.Filled),
            "switching from the Force picker opens the two metre Angry radius pill before target selection");
        Check(runtime.CompleteForceThrowSlider(angryCenter) && runtime.ForcePicking && runtime._angryForce,
            "releasing the Angry radius pill enters target selection");
        Draw();
        Check(runtime._forceQuery == null && !runtime._forceClickPending
            && Label(forceCenter, "FURCE") && Label(angryCenter, "CANCEL")
            && Fill(forceCenter) == idleForce && Fill(angryCenter).Color != idleAngry.Color
            && !ImDrawListPtr.Circles.Any(circle => circle.Center == angryCenter && !circle.Filled),
            "the released Angry picker displays Cancel without retaining an old click");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter); Draw();
        Check(runtime.ForcePicking && !runtime._angryForce && Label(forceCenter, "CANCEL")
            && !Label(angryCenter, "CANCEL") && Fill(angryCenter) == idleAngry,
            "switching from Angry Force back to Force updates both button states");
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter);
        Check(runtime._forceCaptureRadiusDrag.Held
            && runtime.CompleteForceThrowSlider(angryCenter) && runtime.ForcePicking && runtime._angryForce,
            "switching from Force into Angry first requires release of the radius pill");
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter); Draw();
        Check(!runtime.ForcePicking && !DrawnLabels.Any(label => label.Text == "CANCEL") && Fill(angryCenter) == idleAngry,
            "pressing the active Angry Force button cancels the picker without opening another radius pill");

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime.BeginForcePicker(false); Draw();
        Check(Label(forceCenter, "RELEASE") && Label(angryCenter, "THROW")
            && !DrawnLabels.Any(label => label.Text == "CANCEL"),
            "a held Force group preserves Release and Throw instead of picker cancellation labels");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Force, forceCenter);
        Check(runtime._forceRequests.TryPeek(out var release) && release.Action == ForceAction.Release,
            "the Force button still queues release while holding a group");
        runtime._forceRequests.Clear();
        Program.Window.LeftShift = GlfwKeyAction.Release;
        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angryCenter);
        Check(runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0 && runtime.ForceHolding,
            "holding Angry Force starts the separate minimum-speed pill without throwing or releasing ownership");
        runtime.MoveForcePointer(angryCenter - new float2(0, runtime._forceLayout.ThrowSliderTravel));
        Check(runtime._forceThrowDrag.Speed == ForceThrowDrag.MaximumSpeed
            && runtime._forceRequests.Count == 0,
            "dragging up selects the maximum throw speed without touching the settled queue");
        Check(runtime.CompleteForceThrowSlider(angryCenter - new float2(0, runtime._forceLayout.ThrowSliderTravel))
            && runtime._forceRequests.TryPeek(out var thrown) && thrown.Action == ForceAction.Throw
            && thrown.ThrowSpeed == ForceThrowDrag.MaximumSpeed && !runtime._forceThrowDrag.Held
            && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed,
            "left release queues one maximum-speed throw snapshot and resets the transient slider");
        Check(!runtime.CompleteForceThrowSlider(angryCenter) && runtime._forceRequests.Count == 1,
            "a duplicate release cannot enqueue a second throw");
    }

    private static void ForceCaptureRadiusSelection()
    {
        var runtime = EmptyRadiusSelectionRuntime();
        var center = runtime._forceLayout.AngryCenter;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center)
            && runtime._forceCaptureRadiusDrag.Held
            && !runtime.ForcePicking && !runtime._selectingForce && runtime._forceCaptureRadius == 2,
            "an idle Angry press opens its selection pill at two metres without starting target selection");
        Check(runtime._forceCaptureRadiusDrag.Thumb.Y == runtime._forceCaptureRadiusDrag.TrackBottom
            && runtime._forceCaptureRadiusDrag.PillMin.Y < center.Y,
            "the initial two metre thumb sits at the bottom of the pill, which opens upward");
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear();
        ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
        Check(DrawnLabels.Any(label => label.Text == "2m") && DrawnLabels.Any(label => label.Text == "50m")
            && ImDrawListPtr.Rects.Count(rect => rect.Filled) == 1
            && ImDrawListPtr.Lines.Any(line => line.Thickness == 2 && line.Color == new ImColor8(110, 255, 174, 230).Value)
            && Program.MainViewport.Camera.LastSphereCenter == Program.MainViewport.Camera.GetPositionEgo(runtime._owner!)
            && Program.MainViewport.Camera.LastSphereRadius == 2,
            "the fresh radius pill shows its endpoints and a kitten-centred preview sphere without drawing the Angry button");
        var radiusPill = ImDrawListPtr.Rects.Single(rect => rect.Filled);
        var radiusValue = ImDrawListPtr.Texts.Last(text => text.Value == "2 m");
        int radiusValueDrawn = ImDrawListPtr.Events.FindLastIndex(e => e.Kind == "text" && e.Text == "2 m");
        int radiusThumbDrawn = ImDrawListPtr.Events.FindLastIndex(e => e.Kind is "circle-filled" or "circle");
        Check(radiusValue.Position.X >= radiusPill.Max.X + runtime._forceLayout.SliderValueLabelGap - 1e-3f
            && Math.Abs(radiusValue.Position.Y - (runtime._forceCaptureRadiusDrag.Thumb.Y - ImGui.GetFontSize() * .5f)) < 1e-3f
            && radiusValueDrawn > radiusThumbDrawn,
            "the selection value sits to the right of the pill and draws above the thumb, like the count control");
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForcePicking && runtime._forceQuery == null && runtime._forceRequests.Count == 0
            && runtime._force.CaptureCalls == 0 && ForceTargeting.Picks == 0,
            "frame updates and settled physics do not query targets or capture during radius selection");

        var top = center - new float2(0, runtime._forceLayout.SliderTravel);
        Check(runtime.FilterForceRadiusCursor(top) && runtime._forceCaptureRadius == 50,
            "dragging to the top selects fifty metres");
        var fourteen = center - new float2(0, runtime._forceLayout.SliderTravel * .25f);
        Check(runtime.FilterForceRadiusCursor(fourteen) && Math.Abs(runtime._forceCaptureRadius - 14) < 1e-9,
            "dragging back down reverses selection to fourteen metres");
        runtime.CompleteForceThrowSlider(fourteen);
        Check(runtime.ForcePicking && runtime._angryForce && runtime._selectingForce
            && !runtime._forceCaptureRadiusDrag.Held && runtime._forceCaptureRadius == 14
            && !runtime._forceClickPending && runtime._forceQuery == null && runtime._forceRequests.Count == 0,
            "releasing the pill snapshots fourteen metres and only then enters Angry target selection");
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear();
        ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
        Check(DrawnLabels.Contains((center, "CANCEL")) && DrawnLabels.Contains((runtime._forceLayout.ForceCenter, "FURCE")),
            "the released Angry picker puts Cancel on its button while the ordinary Furce button stays available");

        runtime.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        runtime.UpdateForceFrame();
        Check(runtime._forceQuery is { Capture: true } && runtime._forceRequests.Count == 0
            && !runtime.ForceHolding,
            "a separate target click snapshots a capture query after radius selection is complete");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime.ForceHolding && ForceTargeting.Picks == 1 && runtime._force.CaptureCalls == 1
            && runtime._force.CapturedRadius == 14,
            "the later target click captures once with the selected fourteen metre radius");

        var plain = EmptyRadiusSelectionRuntime();
        center = plain._forceLayout.AngryCenter;
        Check(plain.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center)
            && plain.CompleteForceThrowSlider(center) && plain.ForcePicking && plain._angryForce
            && plain._forceCaptureRadius == 2 && plain._forceQuery == null && plain._forceRequests.Count == 0
            && plain._force.CaptureCalls == 0 && ForceTargeting.Picks == 0,
            "press and release without dragging enters the two metre picker without querying or capturing");
        Check(!plain.CompleteForceThrowSlider(center) && plain._forceQuery == null
            && plain._forceRequests.Count == 0 && plain._force.CaptureCalls == 0,
            "a duplicate release after plain selection is inert");

        var outside = EmptyRadiusSelectionRuntime();
        center = outside._forceLayout.AngryCenter;
        outside.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center);
        var outsidePill = center + new float2(0, outside._forceLayout.ButtonRadius + 20);
        Check(outside.CompleteForceThrowSlider(outsidePill) && outside.ForcePicking
            && outside._forceCaptureRadius == 2 && outside._forceQuery == null
            && outside._forceRequests.Count == 0 && outside._force.CaptureCalls == 0,
            "a finite release outside the pill still enters the picker without querying or capturing");
        Check(!outside.CompleteForceThrowSlider(outsidePill),
            "duplicate outside release cannot reopen or alter the picker");

        var escaped = EmptyRadiusSelectionRuntime();
        center = escaped._forceLayout.AngryCenter;
        escaped.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center);
        escaped.MoveForcePointer(center - new float2(0, escaped._forceLayout.SliderTravel * .25f));
        Check(escaped.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !escaped._forceCaptureRadiusDrag.Held && !escaped.ForcePicking
            && escaped._forceRequests.Count == 0 && escaped.CompleteForceThrowSlider(center) == false
            && escaped.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels radius selection and its paired pointer release cannot enter target selection");
        escaped.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center);
        Check(escaped._forceCaptureRadiusDrag.Held
            && escaped._forceCaptureRadius == 2,
            "a new Angry press after cancellation starts at two metres again");
        escaped.CancelForceThrowDrag();

        var direct = EmptyRadiusSelectionRuntime();
        direct.BeginForcePicker(true, 14);
        Check(direct._forceCaptureRadius == 14 && direct._angryForce,
            "direct Angry picker entry preserves its validated radius argument");
        direct.BeginForcePicker(true, 1);
        Check(direct._forceCaptureRadius == 2,
            "a direct picker radius below the pill minimum falls back to two metres");

        var switched = EmptyRadiusSelectionRuntime();
        center = switched._forceLayout.AngryCenter;
        switched.BeginForcePicker(false);
        switched.HandleForcePickerPress(GlfwMouseButton.Left, new(100, 90));
        switched.UpdateForceFrame();
        switched.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center);
        Check(!switched.ForcePicking && switched._forceQuery == null && !switched._forceClickPending
            && switched._forceCaptureRadiusDrag.Held,
            "starting radius selection clears a previous Force picker query and click");
        switched.HandleForceOverlayPress(ForceOverlayHit.Force, switched._forceLayout.ForceCenter);
        Check(switched.ForcePicking && !switched._angryForce && !switched._forceCaptureRadiusDrag.Held
            && !switched.CompleteForceThrowSlider(center) && switched.ForcePicking && !switched._angryForce
            && switched._forceQuery == null && switched._forceRequests.Count == 0,
            "switching to ordinary Furce retires selection before the paired release");

        Invalidated("modal", value => Program.Modal = true);
        Program.Modal = false;
        Invalidated("native mouse latch", value => Program.HasMouseInputLatch = true);
        Program.HasMouseInputLatch = false;
        Invalidated("application focus loss", value => ImGui.Io.AppFocusLost = true);
        ImGui.Io.AppFocusLost = false;
        Invalidated("hidden HUD", value => value.OverlayVisible = false);
        Invalidated("missing layout", value => value._forceLayoutAvailable = false);
        Invalidated("changed layout", value => value._forceLayout = value._forceLayout with
            { AngryCenter = value._forceLayout.AngryCenter + new float2(1, 0) });
        Invalidated("owner change", value =>
        { value._owner = new KittenEva(); Program.ControlledVehicle = value._owner; });
        Invalidated("system change", value => value._owner!.System = new CelestialSystem());
        Invalidated("parent change", value => value._owner!.Parent = new TestParentBody());
        Invalidated("capture generation change", value => value._forceCaptureGeneration++);

        static void Invalidated(string reason, Action<SaberRuntime> invalidate)
        {
            var value = EmptyRadiusSelectionRuntime();
            var angry = value._forceLayout.AngryCenter;
            value.HandleForceOverlayPress(ForceOverlayHit.AngryForce, angry);
            value.MoveForcePointer(angry - new float2(0, value._forceLayout.SliderTravel * .25f));
            invalidate(value);
            value.UpdateForceVisibility();
            Check(!value._forceCaptureRadiusDrag.Held && !value.ForcePicking
                && !value.CompleteForceThrowSlider(angry) && value._forceQuery == null
                && value._forceRequests.Count == 0 && value._force.CaptureCalls == 0,
                $"{reason} invalidation cancels selection without opening a target picker");
        }

        static SaberRuntime EmptyRadiusSelectionRuntime()
        {
            var value = new SaberRuntime();
            value._forceLayout = ForceOverlayLayout.Create(
                SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
                ImGui.GetFontSize(), ImGui.CalcTextSize($"{ForceThrowDrag.MaximumSpeed:0} m/s").X);
            value._forceLayoutAvailable = true;
            Program.Modal = Program.HasMouseInputLatch = false;
            Program.Window.LeftShift = Program.Window.RightShift = GlfwKeyAction.Release;
            ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
            return value;
        }
    }

    private static void ForceNativeHoverPreview()
    {
        var runtime = CreateNativeHoverRuntime(out var targetA, out var partA, out var pickA);
        ImGui.Io.MousePos = new(100, 90);
        Program.MainViewport.Camera.Generation = 4;
        Program.MainViewport.PartPicker.Offer(partA, 5);
        runtime.UpdateForceFrame();
        Check(ForceTargeting.PreviewCalls == 1 && ForceTargeting.Picks == 0
            && runtime._forceHoverPreview is { } firstPreview && ReferenceEquals(firstPreview.Vehicle, targetA)
            && runtime._forceQuery is { Capture: false },
            "the current-frame native PartPicker hit previews immediately without running the settled query");
        runtime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 1 && ReferenceEquals(runtime._forceHover, pickA),
            "the full settled query still confirms the native preview at its existing rate");
        DrawForceOverlay(runtime);
        var firstHit = runtime._forceHoverPreview!.Value.HitEgo;
        Check(ForceHoverMarkerAt(firstHit, new ImColor8(100, 255, 180, 255).Value)
            && HasCaptureBubbleVisuals(),
            "a settled native preview draws a green marker and an Angry capture sphere");

        runtime._playerTime = .01;
        int previewCallsBeforeMove = ForceTargeting.PreviewCalls;
        ImGui.Io.MousePos = new(120, 100);
        Program.MainViewport.PartPicker.Offer(partA, 8);
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(.016);
        var movedHit = new double3(100, 70, -4);
        Check(ForceTargeting.PreviewCalls == previewCallsBeforeMove + 1 && ForceTargeting.Picks == 1
            && runtime._forceHoverPreview is { } movedPreview && movedPreview.HitEgo == movedHit,
            "native preview follows the current pointer and hit distance every frame inside the full-query throttle");
        DrawForceOverlay(runtime);
        Check(ForceHoverMarkerAt(movedHit, new ImColor8(100, 255, 180, 255).Value)
            && HasCaptureBubbleVisuals(),
            "the confirmed marker and capture sphere use the latest native hit point");

        var runtimeForCache = CreateNativeHoverRuntime(out var cacheTarget, out var cachePart, out var cachePick);
        Program.MainViewport.PartPicker.Offer(cachePart, 5);
        runtimeForCache.UpdateForceFrame(); runtimeForCache.UpdateForceBeforePhysics(.016);
        DrawForceOverlay(runtimeForCache);
        Check(ReferenceEquals(runtimeForCache._forceHover, cachePick), "the label cache fixture has a settled target");
        string cachedName = cacheTarget.Id;
        var cachePresentation = runtimeForCache._forceHoverPreview!.Value;
        runtimeForCache.GetForceHoverLabel(cachePresentation.Vehicle, cachePresentation.Part, null,
            cachedName, confirmed: true, wrap: 320, out _, out _);
        int measuresBeforeWarmLoop = ImGui.CalcTextSizeCalls;
        long bytesBeforeWarmLoop = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            runtimeForCache.RefreshForceHoverPreview();
            runtimeForCache.GetForceHoverLabel(cachePresentation.Vehicle, cachePresentation.Part, null,
                cachedName, confirmed: true, wrap: 320, out _, out _);
        }
        long bytesAfterWarmLoop = GC.GetAllocatedBytesForCurrentThread();
        Check(bytesAfterWarmLoop == bytesBeforeWarmLoop && ImGui.CalcTextSizeCalls == measuresBeforeWarmLoop,
            "warmed native preview and hover-label cache helpers allocate nothing and reuse measured text");

        Program.ControlledVehicle = runtime._owner;
        ForceTargeting.Picks = 1;
        ForceTargeting.Next = pickA;
        int previewCallsBeforePending = ForceTargeting.PreviewCalls;
        runtime._playerTime = .02;
        var targetB = CreateNativeTarget(runtime._owner!, "Pending vessel B", out var partB, out _);
        ImGui.Io.MousePos = new(140, 110);
        Program.MainViewport.PartPicker.Offer(partB, 7);
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        var pendingHit = new double3(120, 80, -3);
        Check(ForceTargeting.PreviewCalls == previewCallsBeforePending + 1 && ForceTargeting.Picks == 1
            && runtime._forceHoverPreview is { } pendingPreview && ReferenceEquals(pendingPreview.Vehicle, targetB)
            && ReferenceEquals(runtime._forceHover, pickA),
            "a different native vessel appears immediately while the settled hover identity remains unchanged");
        DrawForceOverlay(runtime);
        Check(ForceHoverMarkerAt(pendingHit, new ImColor8(195, 200, 210, 230).Value)
            && HasCaptureBubbleVisuals()
            && ImDrawListPtr.Texts.Any(text => text.Value.Contains("Pending vessel B", StringComparison.Ordinal)),
            "a new vessel uses a muted pending marker, cached label and current-hit sphere before its settle query");

        runtime._playerTime = .03;
        Program.MainViewport.PartPicker.Reset();
        runtime.UpdateForceFrame(); runtime.UpdateForceBeforePhysics(.016);
        DrawForceOverlay(runtime);
        Check(ForceTargeting.Picks == 1 && runtime._forceHoverPreview == null
            && !ForceHoverMarkerAt(pendingHit, new ImColor8(195, 200, 210, 230).Value)
            && !ForceHoverMarkerAt(new double3(80, 60, -5), new ImColor8(100, 255, 180, 255).Value)
            && !HasCaptureBubbleVisuals(),
            "losing the native hit immediately removes stale vessel markers and their sphere");

        var clutter = new ClutterForceTarget { DisplayName = "Ground rock" };
        var clutterPick = new ForcePick(null, null, clutter, runtime._owner!.Parent, runtime._owner.System)
            { HitEgo = new(12, 15, -6) };
        runtime._forceHover = clutterPick;
        ImGui.Io.MousePos = new(300, 200);
        int validCallsBeforeClutterDraw = ForceTargeting.ValidCalls;
        DrawForceOverlay(runtime);
        Check(ForceHoverMarkerAt(clutterPick.HitEgo, new ImColor8(100, 255, 180, 255).Value)
            && HasCaptureBubbleVisuals() && ForceTargeting.ValidCalls == validCallsBeforeClutterDraw,
            "clutter marker and Angry sphere keep their world anchor without a physics-validity query on draw");
        int clutterMeasures = ImGui.CalcTextSizeCalls;
        long clutterBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            runtime.RefreshForceHoverPreview();
            runtime.GetForceHoverLabel(null, null, clutterPick, "Ground rock", confirmed: true,
                wrap: 320, out _, out _);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == clutterBytes
            && ImGui.CalcTextSizeCalls == clutterMeasures
            && ReferenceEquals(runtime._forceHoverLabelClutter, clutterPick),
            "a warmed clutter fallback keeps its measured label and allocates nothing across no-native-hit frames");

        Program.MainViewport.PartPicker.Offer(partA, 5);
        ImGui.Io.WantCaptureMouse = true;
        runtime.UpdateForceFrame();
        Check(runtime._forceHoverPreview == null && runtime._forceHoverPreviewOwner == null,
            "a native UI mouse claim clears the render-only preview immediately");
        ImGui.Io.WantCaptureMouse = false;
        runtime.UpdateForceFrame();
        Check(runtime._forceHoverPreview is not null, "a valid native hit can be previewed again after UI priority ends");
        runtime.CancelForcePicker();
        Check(runtime._forceHoverPreview == null && runtime._forceHoverPreviewOwner == null
            && runtime._forceHoverLabelVehicle == null && runtime._forceHoverLabelPart == null,
            "cancelling the picker releases its preview and cached target references");

        var radiusSwitch = CreateNativeHoverRuntime(out _, out var radiusPart, out _);
        radiusSwitch.BeginForcePicker(false);
        ImGui.Io.MousePos = new(300, 200);
        Program.MainViewport.PartPicker.Offer(radiusPart, 5);
        radiusSwitch.UpdateForceFrame();
        Check(radiusSwitch._forceHoverPreview is not null,
            "a regular Furce picker has a current-frame vessel preview before switching modes");
        radiusSwitch.HandleForceOverlayPress(ForceOverlayHit.AngryForce, radiusSwitch._forceLayout.AngryCenter);
        Check(radiusSwitch._forceCaptureRadiusDrag.Held && !radiusSwitch.ForcePicking
            && radiusSwitch._forceHoverPreview == null && radiusSwitch._forceHoverPreviewOwner == null,
            "switching into radius selection clears the old picker preview and cached target");

        var worldChange = CreateNativeHoverRuntime(out var worldTarget, out var worldPart, out _);
        Program.MainViewport.PartPicker.Offer(worldPart, 5);
        worldChange.UpdateForceFrame();
        worldChange._owner!.System = new CelestialSystem();
        worldChange.UpdateForceFrame();
        Check(worldChange._forceHoverPreview == null && worldChange._forceHoverLabelVehicle == null
            && !ReferenceEquals(worldTarget.System, worldChange._owner.System),
            "a system change clears stale native preview and label references");

        var clickRuntime = CreateNativeHoverRuntime(out _, out var clickPart, out var clickPick);
        clickRuntime.UpdateForceFrame(); clickRuntime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 1, "the click-ray fixture has one throttled hover query");
        clickRuntime._playerTime = .02;
        Program.MainViewport.Camera.Generation = 9;
        var clickPoint = new float2(200, 150);
        ImGui.Io.MousePos = clickPoint;
        Program.MainViewport.PartPicker.Offer(clickPart, 5);
        clickRuntime.HandleForcePickerPress(GlfwMouseButton.Left, clickPoint);
        clickRuntime.UpdateForceFrame();
        var immutableRay = clickRuntime._forceQuery!.Value.Ray.Cci;
        Program.MainViewport.PartPicker.Offer(partB, 2);
        ImGui.Io.MousePos = new(300, 200);
        clickRuntime.UpdateForceFrame();
        Check(clickRuntime._forceQuery is { Capture: true } clickQuery && clickQuery.Ray.Cci.Origin == immutableRay.Origin
            && immutableRay.Origin == new double3(180, 120, 9) && ForceTargeting.Picks == 1,
            "visual preview motion cannot replace the click's immutable camera ray");
        clickRuntime.UpdateForceBeforePhysics(.016);
        Check(ForceTargeting.Picks == 2 && ForceTargeting.LastRay.Origin == immutableRay.Origin
            && clickRuntime.ForceHolding && clickRuntime._force.CaptureCalls == 1
            && clickRuntime._force.CapturedRadius == 14 && ReferenceEquals(clickRuntime._force.Owner, clickRuntime._owner),
            "the later settled query alone captures the target using the original click snapshot");
        DrawForceOverlay(clickRuntime);
        Check(!HasCaptureBubbleVisuals(), "the Angry capture sphere disappears as soon as capture is applied");

        static void DrawForceOverlay(SaberRuntime value)
        {
            ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            value.DrawForceFrameForChecks();
        }

        static bool ForceHoverMarkerAt(double3 hit, uint color)
        {
            var expected = new float2((float)hit.X, (float)hit.Y);
            return ImDrawListPtr.Circles.Any(circle => circle.Center == expected && !circle.Filled
                && Math.Abs(circle.Radius - 19) < 1e-4 && circle.Color == color);
        }

        static bool HasCaptureBubbleVisuals() => ImDrawListPtr.Lines.Any(line => line.Thickness == 2 && line.Color == new ImColor8(110, 255, 174, 230).Value);
    }

    private static SaberRuntime CreateNativeHoverRuntime(out Vehicle target, out Part part, out ForcePick pick)
    {
        var runtime = new SaberRuntime();
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
            ImGui.GetFontSize(), ImGui.CalcTextSize($"{ForceThrowDrag.MaximumSpeed:0} m/s").X);
        runtime._forceLayoutAvailable = true;
        runtime.BeginForcePicker(true, 14);
        target = CreateNativeTarget(runtime._owner!, "Vessel A", out part, out pick);
        ForceTargeting.Next = pick;
        Program.Modal = Program.HasMouseInputLatch = false;
        ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
        return runtime;
    }

    private static Vehicle CreateNativeTarget(KittenEva owner, string name, out Part part, out ForcePick pick)
    {
        var target = new Vehicle { Id = name, System = owner.System, Parent = owner.Parent };
        part = new Part { Tree = new PartTree { OwningVehicle = target } };
        pick = new ForcePick(target, part, null, owner.Parent, owner.System);
        return target;
    }

    private static void ForceThrowSlider()
    {
        var runtime = new SaberRuntime();
        runtime._force.HasCapture = true;
        runtime._force.Owner = runtime._owner;
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
            ImGui.GetFontSize(), ImGui.CalcTextSize($"{ForceThrowDrag.MaximumSpeed:0} m/s").X);
        runtime._forceLayoutAvailable = true;
        runtime._force.HasCapture = true;
        runtime._force.Owner = runtime._owner;
        var center = runtime._forceLayout.AngryCenter;

        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center)
            && runtime._forceThrowDrag.Held && runtime._forceRequests.Count == 0,
            "a held Angry press opens the separate speed pill without immediately throwing");
        Check(runtime.CompleteForceThrowSlider(center) && runtime._forceRequests.TryPeek(out var defaultRequest)
            && defaultRequest.Action == ForceAction.Throw && defaultRequest.ThrowSpeed == ForceThrowDrag.MinimumSpeed
            && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed,
            "press-release without dragging queues one default-speed throw");
        runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForceHolding && runtime._force.ReleasedThrowSpeed == ForceThrowDrag.MinimumSpeed,
            "the settled release applies the queued default speed and clears capture");

        runtime._forceRequests.Clear(); runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayoutAvailable = true;
        Program.Window.LeftShift = GlfwKeyAction.Press;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later capture can start a new throw drag");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel * 4));
        Check(runtime._forceThrowDrag.Speed == ForceThrowDrag.MaximumSpeed && runtime._forceRequests.Count == 0,
            "extreme upward pointer motion clamps to the maximum speed and never throws before release");
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel * 4))
            && runtime._forceRequests.TryPeek(out var maximumRequest) && maximumRequest.ThrowSpeed == ForceThrowDrag.MaximumSpeed
            && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed,
            "release preserves a maximum-speed request after the visible slider resets to its minimum");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedThrowSpeed == ForceThrowDrag.MaximumSpeed,
            "the controller receives the immutable selected-speed snapshot at the safe handoff");

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner; runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a capture can begin a drag before cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        Program.Modal = true; runtime.UpdateForceVisibility(); Program.Modal = false;
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0 && runtime.ForceHolding,
            "opening a native modal cancels the throw gesture without throwing or releasing capture");

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after modal cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        ImGui.Io.AppFocusLost = true;
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel))
            && !runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0,
            "a release delivered during application focus loss cancels instead of throwing");
        ImGui.Io.AppFocusLost = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after focus-loss cancellation");
        Program.HasMouseInputLatch = true;
        Check(runtime.CompleteForceThrowSlider(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel))
            && !runtime._forceThrowDrag.Held && runtime._forceRequests.Count == 0,
            "a release whose mouse ownership was lost cancels instead of throwing");
        Program.HasMouseInputLatch = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after input-latch cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        ImGui.Io.WantCaptureKeyboard = true;
        Check(runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0
            && runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels a held throw slider even while ImGui wants keyboard input");
        ImGui.Io.WantCaptureKeyboard = false;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after Escape cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        var replacement = new KittenEva();
        runtime._owner = replacement; Program.ControlledVehicle = replacement;
        runtime.UpdateForceVisibility();
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0,
            "controlled-owner loss cancels the transient throw without queueing work");
        runtime._owner = runtime._force.Owner; Program.ControlledVehicle = runtime._owner;

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after owner recovery");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        runtime.OverlayVisible = false; runtime.UpdateForceVisibility(); runtime.OverlayVisible = true;
        Check(!runtime._forceThrowDrag.Held && runtime._forceRequests.Count == 0,
            "hiding the HUD cancels the throw gesture without a throw");

        runtime._forceLayoutAvailable = true;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center), "a later throw drag starts after HUD cancellation");
        runtime.MoveForcePointer(new(center.X, center.Y - runtime._forceLayout.ThrowSliderTravel));
        runtime._force.HasCapture = false;
        runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && runtime._forceRequests.Count == 0,
            "capture loss cancels the throw gesture without queueing a throw");

        var errorRuntime = new SaberRuntime();
        errorRuntime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        errorRuntime._forceLayoutAvailable = true;
        errorRuntime._force.HasCapture = true; errorRuntime._force.Owner = errorRuntime._owner;
        Program.Window.LeftShift = GlfwKeyAction.Press;
        errorRuntime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, errorRuntime._forceLayout.AngryCenter);
        errorRuntime._force.FailUpdate = true;
        errorRuntime.UpdateForceBeforePhysics(.016);
        Check(!errorRuntime._forceThrowDrag.Held && errorRuntime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && errorRuntime._forceRequests.Count == 0 && !errorRuntime.ForceHolding,
            "a settled Force error cancels the slider and releases safely without throwing");

        var disposeRuntime = new SaberRuntime();
        disposeRuntime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080));
        disposeRuntime._forceLayoutAvailable = true;
        disposeRuntime._force.HasCapture = true; disposeRuntime._force.Owner = disposeRuntime._owner;
        Program.Window.LeftShift = GlfwKeyAction.Press;
        disposeRuntime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, disposeRuntime._forceLayout.AngryCenter);
        disposeRuntime.DisposeForce();
        Check(!disposeRuntime._forceThrowDrag.Held && disposeRuntime._forceThrowDrag.Speed == ForceThrowDrag.MinimumSpeed
            && disposeRuntime._forceRequests.Count == 0 && disposeRuntime._force.Disposed,
            "runtime disposal cancels the transient slider without a throw");

        Program.ControlledVehicle = runtime._owner;
        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayout = ForceOverlayLayout.Create(
            SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
            ImGui.GetFontSize(), ImGui.CalcTextSize($"{ForceThrowDrag.MaximumSpeed:0} m/s").X);
        runtime._forceLayoutAvailable = true;
        Program.Window.LeftShift = GlfwKeyAction.Press;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, runtime._forceLayout.AngryCenter)
            && runtime._forceThrowDrag.Held && runtime._forceThrowDrag.Matches(runtime._forceLayout),
            "the throw pill can be drawn for an active gesture");
        runtime.MoveForcePointer(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.ThrowSliderTravel));
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
        var pill = ImDrawListPtr.Rects.Single(rect => rect.Filled);
        var rim = ImDrawListPtr.Rects.Single(rect => !rect.Filled);
        var thumb = ImDrawListPtr.Circles.Single(circle => circle.Filled
            && circle.Center == runtime._forceThrowDrag.Thumb);
        string maximumSpeedLabel = $"{ForceThrowDrag.MaximumSpeed:0} m/s";
        var label = ImDrawListPtr.Texts.Last(text => text.Value == maximumSpeedLabel);
        int labelDrawn = ImDrawListPtr.Events.FindLastIndex(e => e.Kind == "text" && e.Text == maximumSpeedLabel);
        int lastCircle = ImDrawListPtr.Events.FindLastIndex(e => e.Kind is "circle-filled" or "circle");
        Check(pill.Min == runtime._forceLayout.SliderEnvelope(ForceOverlayHit.AngryForce).Min
            && Math.Abs(pill.Max.Y - pill.Min.Y
                - (runtime._forceLayout.SliderTravel + 2 * runtime._forceLayout.ButtonRadius) * 1.5f) < 1e-3f
            && pill.Min.Y < runtime._forceLayout.AngryCenter.Y && pill.Max.Y > runtime._forceLayout.AngryCenter.Y
            && rim.Min == pill.Min && rim.Max == pill.Max && rim.Color == new ImColor8(255, 255, 255, 64).Value
            && Math.Abs(thumb.Radius * 2 - (pill.Max.X - pill.Min.X) * .8f) < 1e-3f
            && !DrawnLabels.Any(value => value.Text == "THROW")
            && !DrawnLabels.Any(value => value.Center.X == runtime._forceLayout.AngryCenter.X
                && (value.Text is "THROW" or "ANGRY" or "FURCE"))
            && !ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == runtime._forceLayout.AngryCenter),
            "the active throw control draws only its rounded upward pill, faint rim, and 80%-width thumb without ghosted button labels");
        Check(label.Size == ImGui.GetFontSize() && label.Wrap == 0 && label.Position.X >= pill.Max.X
            && label.Position.X + ImGui.CalcTextSize(maximumSpeedLabel).X <= runtime._forceLayout.ControlsMax.X + 1e-3f
            && labelDrawn > lastCircle,
            "the external speed value stays regular-sized, single-line, within bounds, and above every control circle");
        runtime.CancelForceThrowDrag();

        runtime._force.HasCapture = true; runtime._force.Owner = runtime._owner;
        runtime._forceLayoutAvailable = true;
        Program.Window.LeftShift = GlfwKeyAction.Press;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, runtime._forceLayout.AngryCenter),
            "delayed owner verification starts from a clean default slider");
        runtime.MoveForcePointer(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.ThrowSliderTravel));
        Check(runtime.CompleteForceThrowSlider(new(runtime._forceLayout.AngryCenter.X,
            runtime._forceLayout.AngryCenter.Y - runtime._forceLayout.ThrowSliderTravel))
            && runtime._forceRequests.TryPeek(out var delayed) && delayed.ThrowSpeed == ForceThrowDrag.MaximumSpeed,
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

    private static void ForceCaptureRadiusGesture()
    {
        var runtime = RadiusRuntime();
        var center = runtime._forceLayout.AngryCenter;
        Check(runtime.HandleForceOverlayPress(ForceOverlayHit.AngryForce, center)
            && runtime._forceCaptureRadiusDrag.Held && runtime._forceRequests.Count == 0,
            "an idle Angry press opens the radius pill without starting target selection");
        runtime.MoveForcePointer(center - new float2(0, runtime._forceLayout.SliderTravel * .5f));
        Check(Math.Abs(runtime._forceCaptureRadius - 26) < 1e-9 && !runtime.ForcePicking
            && runtime._forceQuery == null && runtime._force.CaptureCalls == 0 && ForceTargeting.Picks == 0,
            "radius adjustment reaches twenty-six metres without querying or capturing");
        ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear();
        runtime.DrawForceFrameForChecks();
        Check(ImDrawListPtr.Lines.Any(line => line.Thickness == 2 && line.Color == new ImColor8(110, 255, 174, 230).Value)
            && Program.MainViewport.Camera.LastSphereCenter == Program.MainViewport.Camera.GetPositionEgo(runtime._owner!)
            && Program.MainViewport.Camera.LastSphereRadius == 26,
            "the live radius sphere stays at the kitten's position with the current size while the pill is held");
        Check(runtime.CompleteForceThrowSlider(center - new float2(0, runtime._forceLayout.SliderTravel * .5f))
            && runtime.ForcePicking && runtime._angryForce && runtime._forceCaptureRadius == 26,
            "pill release retains the chosen radius and enters Angry target selection");

        var target = CreateNativeTarget(runtime._owner!, "Angry sphere target", out var part, out var pick);
        ForceTargeting.Next = pick;
        ImGui.Io.MousePos = new(100, 90);
        Program.MainViewport.PartPicker.Offer(part, 5);
        runtime.UpdateForceFrame();
        runtime.UpdateForceBeforePhysics(.016);
        ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Texts.Clear();
        runtime.DrawForceFrameForChecks();
        var targetHit = new double3(80, 60, -5);
        Check(ReferenceEquals(runtime._forceHoverPreview?.Vehicle, target)
            && ImDrawListPtr.Lines.Any(line => line.Thickness == 2 && line.Color == new ImColor8(110, 255, 174, 230).Value)
            && ImDrawListPtr.Circles.Any(circle => circle.Center == new float2(80, 60) && !circle.Filled)
            && Program.MainViewport.Camera.LastSphereCenter == targetHit
            && Program.MainViewport.Camera.LastSphereRadius == 26,
            "the target picker moves the sphere and marker to its current native hit");
        Check(runtime.HandleForcePickerPress(GlfwMouseButton.Left, ImGui.Io.MousePos),
            "a separate target click starts a capture query after radius selection");
        runtime.UpdateForceFrame();
        Check(runtime._forceQuery is { Capture: true } && runtime._force.CaptureCalls == 0,
            "the target click only snapshots work for the settled capture handoff");
        runtime.UpdateForceBeforePhysics(.016);
        ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Texts.Clear();
        runtime.DrawForceFrameForChecks();
        Check(runtime.ForceHolding && runtime._force.CaptureCalls == 1
            && runtime._force.CapturedRadius == 26 && !HasCaptureBubbleVisuals(),
            "settled capture applies the selected radius once and hides the sphere");

        static bool HasCaptureBubbleVisuals() => ImDrawListPtr.Lines.Any(line => line.Thickness == 2 && line.Color == new ImColor8(110, 255, 174, 230).Value);

        static SaberRuntime RadiusRuntime()
        {
            var value = new SaberRuntime();
            value._forceLayout = ForceOverlayLayout.Create(
                SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080),
                ImGui.GetFontSize(), ImGui.CalcTextSize($"{ForceThrowDrag.MaximumSpeed:0} m/s").X);
            value._forceLayoutAvailable = true;
            Program.Modal = Program.HasMouseInputLatch = false;
            Program.Window.LeftShift = Program.Window.RightShift = GlfwKeyAction.Release;
            ImGui.Io.WantCaptureMouse = ImGui.Io.WantCaptureKeyboard = ImGui.Io.WantTextInput = ImGui.Io.AppFocusLost = false;
            return value;
        }
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

        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
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
            && !ImDrawListPtr.Circles.Any(circle => circle.Center == distance),
            "Move Force keeps its faint white rim while the obsolete distance setting remains hidden");
        runtime.ReleaseForceSettingDrag();
        ImGui.Io.MousePos = count;
        runtime.DrawForceFrameForChecks();
        Check(ImGui.Tooltip == "max # objects Furce can capture",
            "the capture-count tooltip uses the requested exact lowercase text");
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        runtime._forceLayout = originalLayout.WithControlsOffset(float2.Zero, new(1920, 1080), new(.02f, 0));
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
        Check(!runtime._forceSettingDrag.Held && ImDrawListPtr.Rects.Count == 0,
            "a viewport or offset layout change collapses stale press-time pill geometry");
        runtime._forceLayout = originalLayout;
        runtime._settings.AngryForceRadius = 14.6f;
        DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear(); ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
        runtime.DrawForceFrameForChecks();
        Check(ImDrawListPtr.Rects.Count == 0
            && ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == count
                && circle.Radius == runtime._forceLayout.ButtonRadius && circle.Color == blackHalf)
            && !ImDrawListPtr.Circles.Any(circle => circle.Center == distance)
            && DrawnLabels.Any(label => label.Center.Y < count.Y && label.Text == "COUNT")
            && DrawnLabels.Any(label => label.Center.Y > count.Y && label.Text == ForceCaptureLimits.Maximum.ToString())
            && !DrawnLabels.Any(label => label.Text is "DIST" or "15m")
            && runtime._settings.AngryForceRadius == 14.6f,
            "the obsolete distance control is hidden while its serialized compatibility value remains untouched");
        ImGui.Tooltip = "";
        ImGui.Io.MousePos = distance;
        runtime.DrawForceFrameForChecks();
        Check(ImGui.Tooltip == "" && runtime.ForceOverlayHitAt(distance) == ForceOverlayHit.None,
            "the hidden legacy distance slot has no tooltip or overlay hit target");

        runtime.HandleForceOverlayPress(ForceOverlayHit.AngryRadius, distance);
        runtime.MoveForcePointer(distance - new float2(0, 10000));
        Check(!runtime._forceSettingDrag.Held && runtime._settings.AngryForceRadius == 14.6f
            && runtime._settings.ForceCaptureLimit == ForceCaptureLimits.Maximum,
            "pressing the hidden distance slot cannot change its compatibility value or the capture limit");
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        var replacement = new KittenEva(); runtime._owner = replacement; Program.ControlledVehicle = replacement;
        runtime.UpdateForceVisibility();
        Check(!runtime._forceSettingDrag.Held, "changing the controlled owner collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        Program.Modal = true; runtime.UpdateForceVisibility(); Program.Modal = false;
        Check(!runtime._forceSettingDrag.Held, "opening a native modal collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        runtime.OverlayVisible = false; runtime.UpdateForceVisibility(); runtime.OverlayVisible = true;
        Check(!runtime._forceSettingDrag.Held, "hiding the overlay collapses an open settings pill");

        runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.CaptureLimit, count);
        Check(runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && !runtime._forceSettingDrag.Held
            && runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels a settings drag and consumes its matching key release");
    }

    private static void ForceRotationHandle()
    {
        var runtime = new SaberRuntime
        {
            _forceLayout = ForceOverlayLayout.Create(
                SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080)),
            _forceLayoutAvailable = true
        };
        var layout = runtime._forceLayout;
        var top = layout.RotationHandleCenter(0);
        Check(runtime.ForceOverlayHitAt(top) == ForceOverlayHit.None,
            "the rotation handle has no input target without captured items");
        runtime._force.Capture(runtime._owner!, ForceTargeting.Next!, true);
        Check(runtime.ForceOverlayHitAt(top) == ForceOverlayHit.Rotate,
            "either capture mode exposes the top rotation handle");
        ImDrawListPtr.Circles.Clear();
        runtime.DrawForceFrameForChecks();
        Check(ImDrawListPtr.Circles.Any(circle => circle.Filled && circle.Center == top
            && circle.Radius == layout.ButtonRadius * .25f
            && circle.Color == new ImColor8(255, 255, 255, 64).Value),
            "the idle handle is a solid quarter-size white circle at 25 percent opacity");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, top);
        runtime.MoveForcePointer(layout.RotationHandleCenter(Math.PI));
        Check(runtime._forceRotationDrag.Held && runtime._force.ItemRotation == 0
            && Math.Abs(runtime._forceRotationTarget - Math.PI) < 1e-5,
            "dragging publishes a half turn without mutating physics from a cursor event");
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(Math.Abs(runtime._force.ItemRotation - Math.PI) < 1e-5,
            "the settled handoff applies the rotation intent");
        // No cursor move callback before release: sample the final pointer on release too.
        runtime.CompleteForceRotationDrag(layout.RotationHandleCenter(Math.PI * 2));
        Check(!runtime._forceRotationDrag.Held && runtime._forceRotationPending,
            "button release retains its final rotation intent until the settled handoff");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(Math.PI * 2));
        Check(runtime._forceRotationDrag.Held && runtime._forceRotationPending,
            "regrabbing before the next handoff starts at the displayed pending angle");
        runtime.CompleteForceRotationDrag(layout.RotationHandleCenter(Math.PI * 2));
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(Math.Abs(runtime._force.ItemRotation - Math.PI * 2) < 1e-5
            && runtime.ForceOverlayHitAt(layout.RotationHandleCenter(Math.PI * 2)) == ForceOverlayHit.Rotate,
            "release retains the endpoint and its input target");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(runtime._force.ItemRotation));
        runtime.MoveForcePointer(top);
        runtime.OverlayVisible = false;
        runtime.UpdateForceVisibility();
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(!runtime._forceRotationDrag.Held && !runtime._forceRotationPending
            && Math.Abs(runtime._force.ItemRotation - Math.PI * 2) < 1e-5,
            "hiding the UI cancels unpublished rotation while retaining the displayed item pose");
        runtime.OverlayVisible = true; runtime._forceLayoutAvailable = true;
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(runtime._force.ItemRotation));
        runtime.MoveForcePointer(top);
        runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press);
        Check(!runtime._forceRotationDrag.Held && !runtime._forceRotationPending,
            "Escape cancels an active rotation gesture");
        runtime.FilterForceKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release);
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(runtime._force.ItemRotation));
        runtime.MoveForcePointer(top);
        runtime._forceCaptureGeneration++;
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(!runtime._forceRotationDrag.Held && Math.Abs(runtime._force.ItemRotation - Math.PI * 2) < 1e-5,
            "a stale gesture cannot rotate a different capture generation");
        runtime._force.Release(false);
        runtime.ResetForceMovement();
        runtime.ResetForceCaptureSession();
        runtime._force.Capture(runtime._owner!, ForceTargeting.Next!, false);
        Check(runtime._force.ItemRotation == 0 && runtime.ForceOverlayHitAt(top) == ForceOverlayHit.Rotate,
            "a new regular capture starts with a neutral handle at the top");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, top);
        runtime.MoveForcePointer(layout.RotationHandleCenter(-Math.PI));
        runtime._forceLayout = layout with { MoveCenter = layout.MoveCenter + new float2(1, 0) };
        runtime.MoveForcePointer(top);
        Check(!runtime._forceRotationDrag.Held && !runtime._forceRotationPending,
            "layout changes invalidate press-time geometry before another rotation is published");
        runtime._forceLayout = layout;
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, top);
        runtime.MoveForcePointer(layout.RotationHandleCenter(Math.PI));
        ImGui.Io.AppFocusLost = true;
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(!runtime._forceRotationDrag.Held && runtime._force.ItemRotation == 0,
            "focus loss rejects pending input at the settled handoff even without another cursor event");
        ImGui.Io.AppFocusLost = false;
        runtime.ResetForce();
    }

    private static void ForceRotationShiftAxes()
    {
        var runtime = new SaberRuntime
        {
            _forceLayout = ForceOverlayLayout.Create(
                SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2), float2.Zero, new(1920, 1080)),
            _forceLayoutAvailable = true
        };
        var layout = runtime._forceLayout;
        runtime._force.Capture(runtime._owner!, ForceTargeting.Next!, false);
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(0));
        runtime.MoveForcePointer(layout.RotationHandleCenter(Math.PI));
        Program.Window.LeftShift = GlfwKeyAction.Press;
        runtime.MoveForcePointer(layout.RotationHandleCenter(Math.PI));
        Check(runtime._forceRotationSegments.Count == 1,
            "changing Shift without pointer movement does not rotate or reset the item");
        runtime.MoveForcePointer(layout.RotationHandleCenter(Math.PI / 2));
        Program.Window.LeftShift = GlfwKeyAction.Release;
        Program.Window.RightShift = GlfwKeyAction.Press;
        runtime.MoveForcePointer(layout.RotationHandleCenter(0));
        Program.Window.RightShift = GlfwKeyAction.Release;
        runtime.CompleteForceRotationDrag(layout.RotationHandleCenter(-Math.PI / 2));
        Check(runtime._force.RotationSegments.Count == 0 && runtime._forceRotationSegments.Count == 4,
            "multiple axis changes before one physics handoff preserve ordered intent without touching physics");
        runtime.UpdateForceBeforePhysics(1d / 60);
        var segments = runtime._force.RotationSegments;
        Check(segments.Count == 4 && !segments[0].AlternateAxis && segments[1].AlternateAxis
            && segments[2].AlternateAxis && !segments[3].AlternateAxis
            && Math.Abs(segments[3].Rotation + Math.PI / 2) < 1e-5,
            "both Shift keys select the alternate axis live, including the final release sample");
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(segments.Count == 4 && runtime._forceRotationSegments.Count == 0,
            "each rotation segment is applied exactly once");
        runtime.HandleForceOverlayPress(ForceOverlayHit.Rotate, layout.RotationHandleCenter(runtime._force.ItemRotation));
        Program.Window.LeftShift = GlfwKeyAction.Press;
        runtime.MoveForcePointer(layout.RotationHandleCenter(0));
        runtime.CancelForceRotationDrag();
        runtime.UpdateForceBeforePhysics(1d / 60);
        Check(segments.Count == 4 && runtime._forceRotationSegments.Count == 0,
            "cancelling a modified drag discards all unpublished segments");
        Program.Window.LeftShift = GlfwKeyAction.Release;
        runtime.ResetForce();
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
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Lines.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            runtime.DrawForceFrameForChecks();
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
