using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using System.Runtime.InteropServices;

namespace Purr2D2.Runtime;

// Exercise the production host/ownership code with deterministic native-window and ImGui boundaries.
internal sealed partial class SaberRuntime
{
    private readonly SaberSettings _settings = new();
    private readonly SaberOverlayPointerCapture _overlayPointerCapture = new();
    private SaberOverlayLayout _overlayLayout;
    private ForceOverlayLayout _forceLayout;
    private readonly List<KittenEva> OverlayKittens = [new(), new()];
    private bool _selectingSlice = false, OverlayVisible = true;
    private bool BFL9KUiVisible => true;
    private bool KittenListVisible => true;
    private bool ShowSomersaultButtons => false;
    private const bool SomersaultOverlayControlsEnabled = false;
    private int _buttonRoutes, _scrollRoutes, _cursorRoutes;
    private enum Command { CancelSlice }
    private void Queue(Command command) { }
    private void OverlayFocusLost() { _overlayPointerCapture.Reset(); _hudGestureWindow = null; }
    private static float OverlayMenuBarInset(ImGuiViewportPtr viewport) => 24;
    private static SaberOverlayLayout WithAutoSwingValueMetrics(SaberOverlayLayout layout)
        => layout with { AutoSwingValueWidth = ImGui.CalcTextSize("3.00 s").X,
            AutoSwingValueHeight = ImGui.GetFontSize(), DropSpinValueWidth = ImGui.CalcTextSize("1000 ms").X,
            DropSpinValueHeight = ImGui.GetFontSize() };
    private ForceOverlayHit ForceOverlayHitAt(float2 pointer) => _forceLayout.Hit(pointer);
    private bool FilterOverlayMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action) { _buttonRoutes++; return true; }
    private bool FilterOverlayScroll(GlfwWindow window, double2 offset) { _scrollRoutes++; return true; }
    private bool FilterOverlayCursor(GlfwWindow window) { _cursorRoutes++; return true; }
    private bool FilterSaberDropSpinKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action) => true;
    private bool FilterForceKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action) => false;

    private void DrawTestPanel(HudPanel panel)
    {
        var viewport = ImGui.GetMainViewport();
        var layout = IsHudDetached(panel) ? DetachedHudLayout() : WithAutoSwingValueMetrics(
            SaberOverlayLayout.Create(viewport.WorkPos, viewport.WorkSize, 16, OverlayKittens.Count, 24, false));
        if (!IsHudDetached(panel)) layout = layout.WithControlsOffset(viewport.WorkPos, viewport.WorkSize,
            HudOffset(HudPanel.Saber), 24);
        _overlayLayout = layout;
        _kittenHudLayout = layout;
        _forceLayout = ForceOverlayLayout.Create(layout, float2.Zero, new(1920, 1080));
        var min = panel == HudPanel.Kittens ? layout.ListMin : layout.ControlsMin;
        var max = panel == HudPanel.Kittens ? layout.ListMax : layout.ControlsMax;
        DrawHudPanel(panel, true, min, max, (_, delta, _, _) =>
        {
            if (panel == HudPanel.Kittens) _kittenHudLayout = _kittenHudLayout.TranslateList(delta);
            else if (panel == HudPanel.Saber) _overlayLayout = _overlayLayout.TranslateControls(delta);
            else _forceLayout = _forceLayout.Translate(delta);
        });
        Check(ImGui.BeginDepth == 0, "window Begin/End remain balanced");
    }

    public static void Main()
    {
        ImGui.Reset();
        var r = new SaberRuntime();
        r.DrawTestPanel(HudPanel.Saber);
        Check(!r._hudHosts[0].Active, "locked attached HUD remains a click-through background drawing");
        r.SetArrangeHud(true); r.DrawTestPanel(HudPanel.Saber);
        var host = r._hudHosts[0];
        Check(host.Active && !host.Detached, "arrange mode creates an attached movable window");
        ImGui.Windows[host.Name].Position += new float2(-150, 50);
        float oldX = r._settings.SaberControlsOffsetX;
        r.DrawTestPanel(HudPanel.Saber);
        Check(r._settings.SaberControlsOffsetX < oldX, "moving an arrange window saves its attached offset");
        var savedOffset = new float2(r._settings.SaberControlsOffsetX, r._settings.SaberControlsOffsetY);
        r.DrawTestPanel(HudPanel.Saber); r.DrawTestPanel(HudPanel.Saber);
        Check((new float2(r._settings.SaberControlsOffsetX, r._settings.SaberControlsOffsetY) - savedOffset).Length() < .00001f,
            "stationary arrange windows do not accumulate position drift");
        Check(!r.HudHostContainsPointer(Program.Window, host.Min + new float2(20), out _),
            "arranging suspends custom gameplay input");
        ImGui.Clicks.Add("Detach"); r.DrawTestPanel(HudPanel.Saber);
        r.SetArrangeHud(false); r.DrawTestPanel(HudPanel.Saber);
        Check(host.Active && host.Detached && host.Window != Program.Window && r._settings.SaberHudWindow.HasPosition,
            "Detach creates an independent native host and saves its desktop position");
        Check(host.Window!.Floating == 0, "default detached panels explicitly clear KSA's native floating hint");
        ImGui.Clicks.Add("Always on top"); r.DrawTestPanel(HudPanel.Saber);
        Check(host.Window.Floating == 1, "Always on top updates the actual native window");
        ImGui.HoveredName = host.Name;
        Check(r.HudHostContainsPointer(host.Window, r._overlayLayout.SwingCenter, out var panel) && panel == HudPanel.Saber
            && r.HudSaberHit(host.Window, r._overlayLayout.SwingCenter) == SaberOverlayHit.Swing,
            "detached custom controls accept only their own native source and visible content");
        Check(!r.HudHostContainsPointer(Program.Window, r._overlayLayout.SwingCenter, out _)
            && !r.HudHostContainsPointer(host.Window, host.Min - new float2(0, 10), out _),
            "other windows and native title/menu bars retain normal input");
        Check(!r.RouteHudBackendButton(Program.Window, GlfwMouseButton.Left, GlfwButtonAction.Press)
            && r.RouteHudBackendButton(host.Window, GlfwMouseButton.Left, GlfwButtonAction.Press) && r._buttonRoutes == 1,
            "main backend events are suppression-only and secondary events route once");
        r.RouteHudBackendScroll(host.Window, new(0, 1)); r.RouteHudBackendCursor(host.Window);
        Check(r._scrollRoutes == 1 && r._cursorRoutes == 1 && r.BlockHudGameInput(host.Window),
            "secondary input has one HUD route and cannot fall through to the camera");
        Program.Window.Focused = false; ImGui.Io.AppFocusLost = true;
        Check(r.HudApplicationHasFocus, "moving focus from main into a HUD window preserves application focus");
        host.Window.Focused = false;
        Check(!r.HudApplicationHasFocus, "leaving every application window releases input");
        host.Window.Focused = true;

        float oldWidth = host.Max.X - host.Min.X;
        ImGui.DetachedFontSize = 32; ImGui.Main.WorkSize = new(180, 120);
        r.DrawTestPanel(HudPanel.Saber);
        Check(host.Max.X - host.Min.X > oldWidth * 1.9f && r._overlayLayout.AutoSwingValueWidth == 96,
            "detached content measures its own monitor font and remains valid with a tiny main viewport");

        r._settings.KittenHudWindow.Detached = true; r.DrawTestPanel(HudPanel.Kittens);
        var kittenHost = r._hudHosts[2]; ImGui.HoveredName = kittenHost.Name;
        r._overlayLayout = r._overlayLayout.TranslateControls(r._kittenHudLayout.ListMin - r._overlayLayout.SwingCenter + new float2(30));
        var rowPoint = r._overlayLayout.SwingCenter;
        Check(r.HudSaberHit(kittenHost.Window!, rowPoint) == SaberOverlayHit.Kittens,
            "overlapping native kitten and saber coordinates retain source-panel hit priority");

        ImGui.Main.WorkSize = new(1920, 1080); ImGui.DetachedFontSize = 16;
        r._settings.SaberHudWindow.X = -5000; r._settings.SaberHudWindow.Y = 9000;
        host.Restore = true; r.DrawTestPanel(HudPanel.Saber);
        Check(r._settings.SaberHudWindow.X >= 0 && r._settings.SaberHudWindow.Y >= 0
            && r._settings.SaberHudWindow.Y < 1080, "missing monitors recover detached panels onto the main display");
        Program.AllowMultipleWindows = false; r.DrawTestPanel(HudPanel.Saber);
        Check(!host.Active && r._settings.SaberHudWindow.Detached,
            "unsupported multi-window mode falls back without erasing the saved detached preference");
        Program.AllowMultipleWindows = true; r.DrawTestPanel(HudPanel.Saber);
        ImGui.CloseNext = true; r.DrawTestPanel(HudPanel.Saber);
        Check(!r._settings.SaberHudWindow.Detached && !host.Active, "closing a detached panel reattaches it without hiding the HUD");
        r.ResetHudPlacement();
        Check(!r._settings.KittenHudWindow.Detached && r._settings.SaberControlsOffsetX == 0,
            "Reset HUD layout recovers every panel and clears saved offsets");
        ImGui.Reset();
        Console.WriteLine("PASS: production HUD host lifecycle, native input ownership, layout recovery and per-monitor geometry");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
