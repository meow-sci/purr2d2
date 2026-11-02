using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Brutal.GlfwApi
{
    public enum GlfwKey { LeftShift, RightShift }
    public enum GlfwKeyAction { Release, Press }

    public sealed class GlfwWindow
    {
        public double2 CursorPos;
        public double X, Y;
        public GlfwKeyAction LeftShift, RightShift;
        public int GetKeyCalls;

        public GlfwKeyAction GetKey(GlfwKey key)
        {
            GetKeyCalls++;
            return key == GlfwKey.LeftShift ? LeftShift : RightShift;
        }
    }
}

namespace Brutal.ImGuiApi
{
    [Flags]
    public enum ImGuiConfigFlags { None = 0, ViewportsEnable = 1 }

    public sealed class TestIo
    {
        public bool AppFocusLost, WantCaptureMouse, WantTextInput;
        public ImGuiConfigFlags ConfigFlags;
        public float2 MousePos;
    }

    public static class ImGui
    {
        public static readonly TestIo Io = new();
        public static TestIo GetIO() => Io;
    }
}

namespace KSA
{
    public sealed class KittenEva { public bool IsDisposed; }

    public sealed class TestViewport
    {
        public bool Visible = true;
        public bool CursorHit = true;
        public float2 Pos = float2.Zero;
        public float2 Size = new(1920, 1080);
    }

    public static class CursorTarget
    {
        public static bool IsHitTestViewport(TestViewport viewport) => viewport.CursorHit;
    }

    public static class Program
    {
        public static readonly GlfwWindow Window = new();
        public static readonly TestViewport MainViewport = new();
        public static object? ControlledVehicle;
        public static bool HasMouseInputLatch, Modal, EditorFlag;
        public static GlfwWindow GetWindow() => Window;
        public static bool IsModalOpen() => Modal;
    }
}

namespace Purr2D2.Runtime
{
    using OverlayHit = Purr2D2.Core.SaberOverlayHit;
    using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

    internal sealed class TestScrollDrag { internal bool Held; }
    internal sealed class TestForceCapture { internal bool HasCapture; internal KittenEva? Owner; }

    internal sealed partial class SaberRuntime
    {
        internal readonly SaberOverlayPointerCapture _overlayPointerCapture = new();
        internal readonly TestScrollDrag _autoSwingDrag = new(), _dropSpinDrag = new(), _saberDeploymentDrag = new(),
            _forceCaptureRadiusDrag = new(), _forceThrowDrag = new(), _forceSettingDrag = new(),
            _forceDrag = new(), _overlayDrag = new();
        internal readonly TestForceCapture _force = new();
        internal bool _overlayLayoutAvailable, _forceLayoutAvailable, _forceUiVisible = true;
        internal bool _overlayVisible = true, _forcePicking;
        internal int _overlayUiButtons;
        internal float _overlayListScroll;
        internal float2 _settingsMin, _settingsMax, _pickerMin, _pickerMax;
        internal bool _settingsAvailable, _pickerAvailable;
        internal KittenEva? _owner, _forceThrowOwner, _forceMoveOwner;
        internal OverlayLayout _overlayLayout;
        internal ForceOverlayLayout _forceLayout;
        internal readonly List<KittenEva> OverlayKittens = [];
        internal readonly List<(double Ticks, bool Shift)> ScrollCalls = [];

        internal bool OverlayVisible => _overlayVisible;
        internal bool ForceUiVisible => _overlayVisible && _forceUiVisible;
        internal bool ForceCanUse => _owner is { IsDisposed: false }
            && ReferenceEquals(_owner, Program.ControlledVehicle) && !Program.EditorFlag;
        internal bool ForceHolding => _force.HasCapture;
        internal bool ForcePicking => _forcePicking && ForceUiVisible;

        internal void Activate(KittenEva owner)
        {
            _owner = owner;
            _forceThrowOwner = owner;
            _forceMoveOwner = owner;
            Program.ControlledVehicle = owner;
            _force.HasCapture = true;
            _force.Owner = owner;
            _settingsAvailable = _pickerAvailable = false;
            _settingsMin = _settingsMax = _pickerMin = _pickerMax = float2.Zero;
            _overlayUiButtons = 0;
            _overlayListScroll = 0;
            _forcePicking = false;
            _overlayLayout = SaberOverlayLayout.Create(float2.Zero, Program.MainViewport.Size, 16, 3);
            _overlayLayoutAvailable = _overlayLayout.Valid;
            _forceLayout = ForceOverlayLayout.Create(_overlayLayout, float2.Zero, Program.MainViewport.Size);
            _forceLayoutAvailable = _forceLayout.Valid;
            OverlayKittens.Add(owner);
        }

        internal bool KittenColorPickerContainsPointer(float2 pointer) => _pickerAvailable
            && pointer.X >= _pickerMin.X && pointer.X <= _pickerMax.X
            && pointer.Y >= _pickerMin.Y && pointer.Y <= _pickerMax.Y;

        internal bool SettingsContainsPointer(float2 pointer) => _settingsAvailable
            && pointer.X >= _settingsMin.X && pointer.X <= _settingsMax.X
            && pointer.Y >= _settingsMin.Y && pointer.Y <= _settingsMax.Y;

        internal ForceOverlayHit ForceOverlayHitAt(float2 pointer)
        {
            if (!_forceLayoutAvailable) return ForceOverlayHit.None;
            var hit = _forceLayout.Hit(pointer);
            return hit == ForceOverlayHit.AngryRadius ? ForceOverlayHit.None : hit;
        }

        internal void CancelForceThrowDrag() => _forceThrowDrag.Held = false;

        private bool ScrollForcePointer(double ticks, bool shift = false)
        {
            ScrollCalls.Add((ticks, shift));
            return true;
        }

        private static float2 OverlayPointer(GlfwWindow window)
        {
            var pointer = window.CursorPos;
            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                pointer.X += window.X;
                pointer.Y += window.Y;
            }
            return new((float)pointer.X, (float)pointer.Y);
        }

        internal static bool ForceShiftHeld(GlfwWindow window)
        {
            ShiftHelperCalls++;
            return window.GetKey(GlfwKey.LeftShift) == GlfwKeyAction.Press
                || window.GetKey(GlfwKey.RightShift) == GlfwKeyAction.Press;
        }

        internal static int ShiftHelperCalls;
    }
}
