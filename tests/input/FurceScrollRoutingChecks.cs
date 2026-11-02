using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Runtime;

internal static class FurceScrollRoutingChecks
{
    private static readonly float2 World = new(1000, 540);

    internal static void Run()
    {
        var runtime = Active(out var window);
        SetPointer(runtime, window, World);
        window.LeftShift = GlfwKeyAction.Press;
        Check(runtime.SuppressOverlayImGuiScroll(window) && runtime.ScrollCalls.Count == 0,
            "backend scroll suppression owns Shift-wheel over the world without applying the Force change");
        CheckRoute(runtime, window, new(0, .75), native: true, backend: true, ticks: .75, shift: true,
            "Shift-wheel over held objects routes once to spread and suppresses native game and ImGui scroll");
        Check(runtime.ScrollCalls.Count == 1 && runtime.ScrollCalls[0] == (.75, true),
            "backend suppression before and after the native callback never applies spread twice");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        CheckRoute(runtime, window, new(0, -1.25), native: false, backend: false, ticks: null, shift: false,
            "ordinary wheel over the world passes through to the camera");
        CheckRoute(runtime, window, new(.5, 0), native: false, backend: false, ticks: null, shift: false,
            "unmodified horizontal wheel over the world passes through unchanged");

        runtime = Active(out window);
        SetPointer(runtime, window, runtime._forceLayout.MoveCenter);
        CheckRoute(runtime, window, new(0, -.625), native: true, backend: true, ticks: -.625, shift: false,
            "ordinary wheel over THE FURCE keeps its depth movement route");

        runtime = Active(out window);
        SetPointer(runtime, window, runtime._forceLayout.MoveCenter);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, .375), native: true, backend: true, ticks: .375, shift: true,
            "Shift-wheel over THE FURCE routes to spread");

        foreach (bool shift in new[] { false, true })
        {
            runtime = Active(out window);
            SetPointer(runtime, window, World);
            runtime._forceDrag.Held = true;
            runtime._forceMoveOwner = runtime._owner;
            HoldPointer(runtime);
            window.LeftShift = shift ? GlfwKeyAction.Press : GlfwKeyAction.Release;
            CheckRoute(runtime, window, new(0, -.25), native: true, backend: true, ticks: -.25, shift,
                shift ? "an active Force move drag routes wheel input to spread"
                    : "an active Force move drag routes ordinary wheel input to depth");
        }

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(2.5, -.375), native: true, backend: true, ticks: -.375, shift: true,
            "spread routing prefers the vertical delta and preserves its exact fractional sign");
        CheckRoute(runtime, window, new(-.625, 0), native: true, backend: true, ticks: -.625, shift: true,
            "Shift-wheel falls back to a finite horizontal delta when vertical movement is zero");
        CheckRoute(runtime, window, new(.25, double.NaN), native: true, backend: true, ticks: null, shift: true,
            "a nonfinite vertical delta is rejected instead of becoming a horizontal spread step");

        NoForceRoute("scroll without an active Force capture cannot change spread", (r, _) => r._force.HasCapture = false);
        NoForceRoute("scroll from a stale Force owner cannot change spread", (r, _) => r._force.Owner = new KittenEva());
        NoForceRoute("scroll after the controlled owner changes cannot change spread", (_, _) => Program.ControlledVehicle = new KittenEva());
        NoForceRoute("scroll outside the main viewport cannot change spread", (_, _) => Program.MainViewport.CursorHit = false);
        NoForceRoute("focus loss blocks Force scroll routing", (_, _) => ImGui.Io.AppFocusLost = true);
        NoForceRoute("a modal blocks Force scroll routing", (_, _) => Program.Modal = true);
        NoForceRoute("native text input blocks Force scroll routing", (_, _) => ImGui.Io.WantTextInput = true);
        NoForceRoute("native ImGui mouse ownership blocks Force scroll routing", (_, _) => ImGui.Io.WantCaptureMouse = true);
        NoForceRoute("the viewport input latch blocks Force scroll routing", (_, _) => Program.HasMouseInputLatch = true);
        NoForceRoute("disabled Force controls block Force scroll routing", (r, _) => r._forceUiVisible = false);
        NoForceRoute("a hidden Force layout blocks Force scroll routing", (r, _) => r._forceLayoutAvailable = false);
        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._forcePicking = true;
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, .5), native: true, backend: true, ticks: null, shift: true,
            "the Force picker retains its overlay wheel ownership without changing Force targets");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._dropSpinDrag.Held = true;
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, .5), native: true, backend: true, ticks: null, shift: true,
            "the drop spin pill owns both wheel routes without changing Furce spread");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        var unrelatedWindow = new GlfwWindow
        { CursorPos = new(World.X, World.Y), LeftShift = GlfwKeyAction.Press };
        CheckRoute(runtime, unrelatedWindow, new(0, 1), native: false, backend: false, ticks: null, shift: false,
            "a detached window cannot route Force scroll or claim the main viewport");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        HoldPointer(runtime);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: true, ticks: null, shift: true,
            "an unrelated held overlay gesture owns scroll without changing Force targets");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._forceDrag.Held = true;
        runtime._forceMoveOwner = new KittenEva();
        HoldPointer(runtime);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: true, ticks: null, shift: true,
            "a Force drag captured by a different owner cannot change the current group");

        runtime = Active(out window);
        SetPointer(runtime, window, runtime._overlayLayout.SwingCenter);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: true, ticks: null, shift: true,
            "other overlay controls retain their wheel ownership without routing to Force");

        runtime = Active(out window);
        SetPointer(runtime, window, runtime._forceLayout.AngryRadiusCenter);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: false, backend: false, ticks: null, shift: true,
            "Shift-wheel over a non-Move Force control is not mistaken for world scroll");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._settingsAvailable = true;
        runtime._settingsMin = World - new float2(10);
        runtime._settingsMax = World + new float2(10);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: false, ticks: null, shift: true,
            "the settings window receives its wheel without publishing a Force target");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._pickerAvailable = true;
        runtime._pickerMin = World - new float2(10);
        runtime._pickerMax = World + new float2(10);
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: false, ticks: null, shift: true,
            "the color picker receives its wheel without publishing a Force target");

        runtime = Active(out window);
        SetPointer(runtime, window, World);
        runtime._overlayUiButtons = 1;
        window.LeftShift = GlfwKeyAction.Press;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: false, ticks: null, shift: true,
            "a captured native overlay control blocks Force wheel routing");

        runtime = Active(out window);
        for (int i = 0; i < 40; i++) runtime.OverlayKittens.Add(new KittenEva());
        runtime._overlayLayout = SaberOverlayLayout.Create(float2.Zero, Program.MainViewport.Size, 16,
            runtime.OverlayKittens.Count);
        SetPointer(runtime, window, runtime._overlayLayout.ListMin + new float2(20, 50));
        runtime._overlayListScroll = 400;
        float expectedScroll = runtime._overlayListScroll - runtime._overlayLayout.RowHeight * 3;
        CheckRoute(runtime, window, new(0, 1), native: true, backend: true, ticks: null, shift: false,
            "the kitten list retains its existing vertical wheel ownership");
        Check(MathF.Abs(runtime._overlayListScroll - expectedScroll) < 1e-3f && runtime.ScrollCalls.Count == 0,
            "list scrolling updates only its own position and leaves Force targets untouched");

        Console.WriteLine("PASS: production Force scroll routing shares ownership between GLFW and ImGui callbacks without duplicate target updates");
    }

    private static SaberRuntime Active(out GlfwWindow window)
    {
        Program.Modal = Program.HasMouseInputLatch = Program.EditorFlag = false;
        Program.MainViewport.CursorHit = true;
        Program.MainViewport.Visible = true;
        Program.MainViewport.Pos = float2.Zero;
        Program.MainViewport.Size = new(1920, 1080);
        Program.ControlledVehicle = null;
        Program.Window.CursorPos = new double2(0, 0);
        Program.Window.X = Program.Window.Y = 0;
        Program.Window.LeftShift = Program.Window.RightShift = GlfwKeyAction.Release;
        Program.Window.GetKeyCalls = 0;
        SaberRuntime.ShiftHelperCalls = 0;
        ImGui.Io.AppFocusLost = ImGui.Io.WantCaptureMouse = ImGui.Io.WantTextInput = false;
        ImGui.Io.ConfigFlags = ImGuiConfigFlags.None;
        ImGui.Io.MousePos = float2.Zero;
        window = Program.Window;
        var runtime = new SaberRuntime();
        runtime.Activate(new KittenEva());
        return runtime;
    }

    private static void SetPointer(SaberRuntime runtime, GlfwWindow window, float2 pointer)
    {
        window.CursorPos = new(pointer.X, pointer.Y);
        ImGui.Io.MousePos = pointer;
    }

    private static void HoldPointer(SaberRuntime runtime) => runtime._overlayPointerCapture.Consume(
        0, pressed: true, overOverlay: true, gameOwnsGesture: false, frame: 1);

    private static void NoForceRoute(string message, Action<SaberRuntime, GlfwWindow> invalidate)
    {
        var runtime = Active(out var window);
        SetPointer(runtime, window, World);
        window.LeftShift = GlfwKeyAction.Press;
        invalidate(runtime, window);
        CheckRoute(runtime, window, new(0, .5), native: false, backend: false, ticks: null, shift: true, message);
    }

    private static void CheckRoute(SaberRuntime runtime, GlfwWindow window, double2 offset,
        bool native, bool backend, double? ticks, bool shift, string message)
    {
        int shiftQueries = SaberRuntime.ShiftHelperCalls;
        int callsBefore = runtime.ScrollCalls.Count;
        bool filtered = runtime.FilterOverlayScroll(window, offset);
        int callsAfterNative = runtime.ScrollCalls.Count;
        bool suppressed = runtime.SuppressOverlayImGuiScroll(window);
        int expectedShiftQueries = ReferenceEquals(window, Program.Window) ? 2 : 0;
        Check(filtered == native && suppressed == backend,
            $"{message}: game={filtered}, ImGui={suppressed}");
        Check(SaberRuntime.ShiftHelperCalls - shiftQueries == expectedShiftQueries,
            "each scroll callback snapshots raw Shift once");
        if (ticks is { } expectedTicks)
        {
            Check(callsAfterNative == callsBefore + 1 && callsAfterNative == runtime.ScrollCalls.Count
                && runtime.ScrollCalls[^1] == (expectedTicks, shift),
                message + ": exact Force scroll target");
        }
        else
        {
            Check(callsAfterNative == callsBefore && runtime.ScrollCalls.Count == callsBefore,
                message + ": no unintended Force target");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Furce scroll routing check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
