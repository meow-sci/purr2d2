using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using System.Runtime.InteropServices;
using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private enum HudPanel { Saber, Force, Kittens }

    private sealed class HudHost(string name)
    {
        private readonly string _attachedName = name + "Arrange";
        private readonly string _detachedName = name + "Detached";
        internal string Name => Detached ? _detachedName : _attachedName;
        internal bool Active, Detached, Restore = true;
        internal float2 Min, Max, Position;
        internal GlfwWindow? Window;
        internal HudWindowSettings? Settings;
    }

    private readonly HudHost[] _hudHosts =
    [new("Purr2D2 | Saber###Purr2D2SaberHud"), new("Purr2D2 | Furce###Purr2D2ForceHud"),
        new("Purr2D2 | Kittens###Purr2D2KittenHud")];
    private bool _arrangeHud;
    private GlfwWindow? _hudGestureWindow;
    private OverlayLayout _kittenHudLayout;

    private bool IsHudDetached(HudPanel panel) => HudSettings(panel).Detached && HudDetachmentAvailable;

    private OverlayLayout DetachedHudLayout()
    {
        float font = ImGui.GetFontSize();
        float scale = Math.Clamp(font / 16, .75f, 2);
        return WithAutoSwingValueMetrics(OverlayLayout.Create(float2.Zero, new float2(1920, 1080) * scale,
            font, OverlayKittens.Count, 0, false));
    }

    private void PrepareDetachedHudGeometry(HudPanel panel, out float2 min, out float2 max)
    {
        var layout = DetachedHudLayout();
        switch (panel)
        {
            case HudPanel.Saber:
                _overlayLayout = layout with { ShowBFL9KControls = BFL9KUiVisible, ShowKittenList = false };
                min = layout.ControlsMin; max = layout.ControlsMax; break;
            case HudPanel.Kittens:
                _kittenHudLayout = layout with { ShowBFL9KControls = false, ShowKittenList = KittenListVisible };
                min = layout.ListMin; max = layout.ListMax; break;
            default:
                _forceLayout = ForceOverlayLayout.Create(layout, float2.Zero, new float2(1920, 1080) * layout.Scale,
                    ImGui.GetFontSize(), ImGui.CalcTextSize("250 m/s").X);
                min = _forceLayout.ControlsMin; max = _forceLayout.ControlsMax; break;
        }
    }

    private OverlayLayout CurrentKittenOverlayLayout() => _kittenHudLayout;

    private SaberOverlayHit HudCombinedHit(float2 pointer)
    {
        var saberHit = (_overlayLayout with { ShowKittenList = false }).Hit(pointer);
        return saberHit != SaberOverlayHit.None ? saberHit
            : (_kittenHudLayout with { ShowBFL9KControls = false }).Hit(pointer);
    }

    private static bool HudDetachmentAvailable => Program.AllowMultipleWindows
        && (ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0;

    private HudWindowSettings HudSettings(HudPanel panel) => panel switch
    {
        HudPanel.Saber => _settings.SaberHudWindow,
        HudPanel.Force => _settings.ForceHudWindow,
        _ => _settings.KittenHudWindow
    };

    private void SetArrangeHud(bool arrange)
    {
        if (_arrangeHud == arrange) return;
        OverlayFocusLost();
        if (arrange && _selectingSlice) Queue(Command.CancelSlice);
        _arrangeHud = arrange;
        foreach (var host in _hudHosts) { host.Active = false; host.Restore = true; }
    }

    private void ResetHudPlacement()
    {
        OverlayFocusLost();
        _settings.SaberControlsOffsetX = _settings.SaberControlsOffsetY = 0;
        _settings.ForceControlsOffsetX = _settings.ForceControlsOffsetY = 0;
        _settings.KittenListOffsetX = _settings.KittenListOffsetY = 0;
        _settings.SaberHudWindow = new(); _settings.ForceHudWindow = new(); _settings.KittenHudWindow = new();
        foreach (var host in _hudHosts) { host.Active = false; host.Restore = true; }
    }

    private void InvalidateHudHosts()
    {
        foreach (var host in _hudHosts) { host.Active = false; host.Restore = true; }
    }

    private void SetHudDetached(HudPanel panel, bool detached)
    {
        OverlayFocusLost();
        var host = _hudHosts[(int)panel];
        var settings = HudSettings(panel);
        if (detached && !settings.HasPosition && host.Active)
        {
            settings.X = host.Position.X; settings.Y = host.Position.Y; settings.HasPosition = true;
        }
        settings.Detached = detached;
        host.Restore = true;
        host.Active = false;
    }

    private float2 HudOffset(HudPanel panel) => panel switch
    {
        HudPanel.Saber => new(_settings.SaberControlsOffsetX, _settings.SaberControlsOffsetY),
        HudPanel.Force => new(_settings.ForceControlsOffsetX, _settings.ForceControlsOffsetY),
        _ => new(_settings.KittenListOffsetX, _settings.KittenListOffsetY)
    };

    private OverlayLayout WithKittenListOffset(OverlayLayout layout, ImGuiViewportPtr viewport, float menuInset)
    {
        return SaberOverlayLayout.TryControlsTranslation(layout.ListMin, layout.ListMax,
            viewport.WorkPos, viewport.WorkSize, HudOffset(HudPanel.Kittens), menuInset, out var delta)
            ? layout.TranslateList(delta) : layout;
    }

    private void SaveAttachedHudMovement(HudPanel panel, float2 position, ImGuiViewportPtr viewport)
    {
        float menuInset = OverlayMenuBarInset(viewport);
        var defaults = WithAutoSwingValueMetrics(OverlayLayout.Create(viewport.WorkPos, viewport.WorkSize,
            ImGui.GetFontSize(), OverlayKittens.Count, menuInset, ShowSomersaultButtons && SomersaultOverlayControlsEnabled));
        var origin = panel switch
        {
            HudPanel.Saber => defaults.ControlsMin,
            HudPanel.Kittens => defaults.ListMin,
            _ => ForceOverlayLayout.Create(defaults, viewport.WorkPos, viewport.WorkSize,
                ImGui.GetFontSize(), ImGui.CalcTextSize("250 m/s").X).ControlsMin
        };
        var offset = (position - origin) / viewport.WorkSize;
        switch (panel)
        {
            case HudPanel.Saber: _settings.SaberControlsOffsetX = offset.X; _settings.SaberControlsOffsetY = offset.Y; break;
            case HudPanel.Force: _settings.ForceControlsOffsetX = offset.X; _settings.ForceControlsOffsetY = offset.Y; break;
            default: _settings.KittenListOffsetX = offset.X; _settings.KittenListOffsetY = offset.Y; break;
        }
    }

    private static float2 RecoverHudWindow(float2 position, float2 size, ImGuiViewportPtr main)
    {
        float titleHeight = ImGui.GetFrameHeight();
        foreach (var monitor in ImGui.GetPlatformIO().Monitors)
            if (HudPlacement.TitleVisible(position, size, monitor.WorkPos, monitor.WorkSize, titleHeight)) return position;
        // Includes monitor removal and changes to the desktop coordinate origin.
        return HudPlacement.ClampPosition(position, size, main.WorkPos, main.WorkSize);
    }

    private unsafe void DrawHudPanel(HudPanel panel, bool visible, float2 min, float2 max,
        Action<ImDrawListPtr, float2, float2, float2> render)
    {
        var host = _hudHosts[(int)panel];
        var settings = HudSettings(panel);
        var viewport = ImGui.GetMainViewport();
        bool detached = settings.Detached && HudDetachmentAvailable;
        if (!visible) { host.Active = false; host.Restore = true; return; }
        if (!_arrangeHud && !detached)
        {
            host.Active = false; host.Restore = true;
            render(ImGui.GetBackgroundDrawList(viewport), float2.Zero, viewport.WorkPos, viewport.WorkPos + viewport.WorkSize);
            return;
        }
        bool restore = host.Restore || !ReferenceEquals(host.Settings, settings) || host.Detached != detached;
        host.Settings = settings;
        host.Detached = detached;
        host.Restore = false;
        host.Active = false;
        var contentSize = max - min;
        var padding = ImGui.GetStyle().WindowPadding;
        var header = new float2(padding.X, padding.Y + ImGui.GetFrameHeightWithSpacing() * 2);
        var windowSize = contentSize + header + padding;
        if (detached)
        {
            var windowClass = new ImGuiWindowClass
            {
                ViewportFlagsOverrideSet = ImGuiViewportFlags.NoAutoMerge
                    | (settings.AlwaysOnTop ? ImGuiViewportFlags.TopMost : ImGuiViewportFlags.None),
                ViewportFlagsOverrideClear = settings.AlwaysOnTop ? ImGuiViewportFlags.None : ImGuiViewportFlags.TopMost
            };
            ImGui.SetNextWindowClass(&windowClass);
            var position = settings.HasPosition ? new float2(settings.X, settings.Y) : min - header;
            var recovered = RecoverHudWindow(position, windowSize, viewport);
            // Recover a removed monitor even while the panel is already open.
            if (restore || recovered != position) ImGui.SetNextWindowPos(recovered, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowViewport(viewport.ID);
            if (restore) ImGui.SetNextWindowPos(HudPlacement.ClampPosition(min - header, windowSize,
                viewport.WorkPos + new float2(0, OverlayMenuBarInset(viewport)),
                viewport.WorkSize - new float2(0, OverlayMenuBarInset(viewport))), ImGuiCond.Always);
        }
        bool open = true;
        bool draw = ImGui.Begin(host.Name, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.MenuBar
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            if (!open)
            {
                if (detached) SetHudDetached(panel, false);
                else SetArrangeHud(false);
                return;
            }
            if (ImGui.BeginMenuBar())
            {
                try
                {
                    if (detached)
                    {
                        if (ImGui.MenuItem("Reattach")) SetHudDetached(panel, false);
                        if (ImGui.BeginMenu("View"))
                        {
                            try
                            {
                                if (ImGui.MenuItem("Always on top", default(ImString), settings.AlwaysOnTop))
                                    settings.AlwaysOnTop = !settings.AlwaysOnTop;
                            }
                            finally { ImGui.EndMenu(); }
                        }
                    }
                    else if (ImGui.MenuItem("Detach", default(ImString), false, HudDetachmentAvailable))
                        SetHudDetached(panel, true);
                    if (_arrangeHud && ImGui.MenuItem("Done")) SetArrangeHud(false);
                }
                finally { ImGui.EndMenuBar(); }
            }
            if (!draw || host.Restore) return;
            if (detached)
            {
                // Begin selects this monitor's font/DPI; measure the canvas in that font.
                PrepareDetachedHudGeometry(panel, out min, out max);
                contentSize = max - min;
            }
            var position = ImGui.GetWindowPos();
            var contentMin = ImGui.GetCursorScreenPos();
            if (!detached && restore)
            {
                // Use actual chrome metrics so entering/leaving arrangement does not shift the canvas.
                var actualHeader = contentMin - position;
                float inset = OverlayMenuBarInset(viewport);
                var corrected = HudPlacement.ClampPosition(min - actualHeader, contentSize + actualHeader + padding,
                    viewport.WorkPos + new float2(0, inset), viewport.WorkSize - new float2(0, inset));
                ImGui.SetWindowPos(corrected, ImGuiCond.Always);
                position = ImGui.GetWindowPos(); contentMin = ImGui.GetCursorScreenPos();
            }
            // Keep every pill, transient value label and status line inside the window.
            host.Min = contentMin; host.Max = contentMin + contentSize;
            host.Position = position;
            var handle = ImGui.GetWindowViewport().PlatformHandle;
            host.Window = handle != 0 ? GCHandle.FromIntPtr(handle).Target as GlfwWindow : null;
            if (detached && host.Window is { } platformWindow
                && platformWindow.GetAttribute(GlfwWindowAttribute.Floating) != (settings.AlwaysOnTop ? 1 : 0))
                platformWindow.SetAttribute(GlfwWindowAttribute.Floating, settings.AlwaysOnTop ? 1 : 0);
            host.Active = true;
            if (detached)
            {
                settings.X = position.X; settings.Y = position.Y; settings.HasPosition = true;
            }
            else SaveAttachedHudMovement(panel, contentMin, viewport);
            ImGui.InvisibleButton("##HudCanvas", contentSize,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            render(ImGui.GetWindowDrawList(), contentMin - min, contentMin, host.Max);
        }
        finally { ImGui.End(); }
    }

    // Raw callback coordinates and ImGui's z-order protect covered panels and native chrome.
    private bool HudHostContainsPointer(GlfwWindow window, float2 pointer, out HudPanel panel)
    {
        panel = default;
        if (_arrangeHud || !OverlayVisible) return false;
        for (int i = 0; i < _hudHosts.Length; i++)
        {
            var host = _hudHosts[i];
            if (!host.Active || host.Window != window || !HudPlacement.Contains(host.Min, host.Max, pointer)) continue;
            ImGui.Internal.FindHoveredWindowEx(pointer, true, out var hovered, out _);
            var own = ImGui.Internal.FindWindowByName(host.Name);
            if (hovered.IsNull() || own.IsNull() || hovered.ID != own.ID) continue;
            panel = (HudPanel)i;
            return true;
        }
        return false;
    }

    private bool HudWindowAcceptsInput(GlfwWindow window) => window == Program.GetWindow()
        || _hudHosts.Any(host => host.Active && host.Window == window);

    private bool HudPanelAcceptsPointer(HudPanel panel, GlfwWindow window, float2 pointer)
    {
        var host = _hudHosts[(int)panel];
        return host.Active ? HudHostContainsPointer(window, pointer, out var underPointer) && underPointer == panel
            : window == Program.GetWindow();
    }

    private SaberOverlayHit HudSaberHit(GlfwWindow window, float2 pointer)
    {
        if (HudHostContainsPointer(window, pointer, out var panel))
            return panel switch
            {
                HudPanel.Kittens => (_kittenHudLayout with { ShowBFL9KControls = false }).Hit(pointer),
                HudPanel.Saber => (_overlayLayout with { ShowKittenList = false }).Hit(pointer),
                _ => SaberOverlayHit.None
            };
        if (window != Program.GetWindow()) return SaberOverlayHit.None;
        var hit = (_overlayLayout with
        {
            ShowBFL9KControls = _overlayLayout.ShowBFL9KControls && !_hudHosts[(int)HudPanel.Saber].Active,
            ShowKittenList = false
        }).Hit(pointer);
        return hit != SaberOverlayHit.None ? hit : (_kittenHudLayout with
        {
            ShowBFL9KControls = false,
            ShowKittenList = _kittenHudLayout.ShowKittenList && !_hudHosts[(int)HudPanel.Kittens].Active
        }).Hit(pointer);
    }

    private ForceOverlayHit HudForceHit(GlfwWindow window, float2 pointer) =>
        HudPanelAcceptsPointer(HudPanel.Force, window, pointer) ? ForceOverlayHitAt(pointer) : ForceOverlayHit.None;

    private bool DropSpinPointerInHudWindow(float2 pointer)
    {
        if (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y)) return false;
        var host = _hudHosts[(int)HudPanel.Saber];
        if (host.Active) return HudPlacement.Contains(host.Min, host.Max, pointer);
        var viewport = ImGui.GetMainViewport();
        return HudPlacement.Contains(viewport.Pos, viewport.Pos + viewport.Size, pointer);
    }

    private bool HudApplicationHasFocus
    {
        get
        {
            if (!DeferHudFocusLoss) return !ImGui.GetIO().AppFocusLost;
            foreach (var viewport in ImGui.GetPlatformIO().Viewports)
                if (viewport.PlatformHandle != 0
                    && GCHandle.FromIntPtr(viewport.PlatformHandle).Target is GlfwWindow window
                    && window.GetAttribute(GlfwWindowAttribute.Focused) != 0) return true;
            return false;
        }
    }

    private GlfwWindow HudModifierWindow => _hudGestureWindow ?? Program.GetWindow();

    private bool HudKeyboardOwnsInput(GlfwWindow window)
    {
        var focused = ImGui.GetCurrentContext().NavWindow;
        if (focused.IsNull()) return false;
        foreach (var host in _hudHosts)
        {
            if (!host.Active || host.Window != window) continue;
            var own = ImGui.Internal.FindWindowByName(host.Name);
            if (!own.IsNull() && focused.ID == own.ID) return true;
        }
        return false;
    }

    internal bool DeferHudFocusLoss => _hudHosts.Any(host => host.Active && host.Detached);

    internal bool BlockHudGameInput(GlfwWindow window) => window != Program.GetWindow()
        && (HudWindowAcceptsInput(window) || _overlayPointerCapture.AnyHeld
            || _overlayPointerCapture.EventFrame == Program.FrameNumber);

    internal bool RouteHudBackendButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action)
        => window != Program.GetWindow() && FilterOverlayMouseButton(window, button, action);

    internal bool RouteHudBackendScroll(GlfwWindow window, double2 offset)
        => window != Program.GetWindow() && FilterOverlayScroll(window, offset);

    internal void RouteHudBackendCursor(GlfwWindow window)
    {
        if (window != Program.GetWindow()) FilterOverlayCursor(window);
    }

    internal bool RouteHudBackendKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action)
        => window != Program.GetWindow() && HudWindowAcceptsInput(window)
            && (FilterSaberDropSpinKey(window, key, action) || FilterForceKey(window, key, action));
}
