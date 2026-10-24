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
        _forceLayout = default;
        _owner = new KittenEva(); Program.ControlledVehicle = _owner;
        Program.EditorFlag = Program.Modal = false;
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
        ForcePickerButtons();
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

        runtime._force.Events.Clear();
        runtime._forceMoveOwner = runtime._owner;
        Check(runtime._forceDrag.Begin(float2.Zero, 80, double3.Zero, double3.UnitX, double3.UnitY, -double3.UnitZ), "held movement starts");
        runtime.MoveForcePointer(new(80, 0));
        Check(runtime.ScrollForcePointer(2) && runtime._forceMoveOffset == new double3(8, 0, 1),
            "wheel depth during a held drag preserves the camera-plane offset");
        runtime.QueueForceAction(ForceAction.Throw);
        Check(runtime.ForceHolding, "throw clicks queue work and retain ownership until the safe handoff");
        runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._force.ReleasedOffset == runtime._force.GroupOffsetBody
            && runtime._force.ReleasedOffset != new double3(8, 0, 1)
            && runtime._force.ReleasedOffset.X > 0 && runtime._force.ReleasedOffset.X < 8
            && runtime._force.Events.SequenceEqual(new[] { "Offset", "Update", "Throw" }),
            "throw uses the offset displayed by one settled spring step instead of teleporting to the pending raw pointer target");

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
        runtime._forceMoveOwner = runtime._owner; runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._forceOffsetSmoother.Advance(.016);
        runtime._owner = new(); Program.ControlledVehicle = runtime._owner; runtime.UpdateForceBeforePhysics(.016);
        Check(!runtime.ForceHolding && runtime._forceOffsetSmoother.Offset == double3.Zero
            && runtime._forceOffsetSmoother.Target == double3.Zero && runtime._forceOffsetSmoother.Velocity == double3.Zero,
            "an owner switch releases the old capture and clears its spring state before another kitten can use Force");

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
        runtime._forceMoveOwner = runtime._owner; runtime._forceOffsetSmoother.SetTarget(new(8, 0, 0));
        runtime._force.FailUpdate = true; runtime.UpdateForceBeforePhysics(.016);
        Check(runtime._forceStatus.StartsWith("Force suspended:") && !runtime.ForceHolding
            && runtime._forceOffsetSmoother.Offset == double3.Zero && runtime._forceOffsetSmoother.Target == double3.Zero
            && runtime._forceOffsetSmoother.Velocity == double3.Zero,
            "Force handoff failures release locally and clear spring state without escaping into global saber reset");
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
            DrawnLabels.Clear(); ImDrawListPtr.Circles.Clear(); ImDrawListPtr.Rects.Clear();
            ImDrawListPtr.Texts.Clear(); ImDrawListPtr.Events.Clear();
            runtime.DrawForceOverlayCore(default);
        }
        bool Label(float2 center, string text) => DrawnLabels.Contains((center, text));
        ImDrawListPtr.Circle Fill(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && circle.Filled);
        ImDrawListPtr.Circle Ring(float2 center) => ImDrawListPtr.Circles.Single(circle => circle.Center == center && !circle.Filled);
        Draw();
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
        Check(runtime._forceRequests.TryPeek(out var thrown) && thrown.Action == ForceAction.Throw,
            "the Angry Force button still queues throw while holding a group");
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
