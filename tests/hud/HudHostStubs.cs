using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace Brutal.GlfwApi
{
    public enum GlfwMouseButton { Left, Right }
    public enum GlfwButtonAction { Release, Press }
    public enum GlfwKey { Escape }
    public enum GlfwKeyAction { Release, Press }
    public enum GlfwWindowAttribute { Focused, Floating }
    public sealed class GlfwWindow
    {
        public bool Focused = true;
        public int Floating = 1;
        public int GetAttribute(GlfwWindowAttribute attribute) => attribute == GlfwWindowAttribute.Focused ? (Focused ? 1 : 0) : Floating;
        public void SetAttribute(GlfwWindowAttribute attribute, int value) { Floating = value; }
    }
}

namespace Brutal.ImGuiApi
{
    [Flags] public enum ImGuiConfigFlags { ViewportsEnable = 1 }
    [Flags] public enum ImGuiViewportFlags { None = 0, NoAutoMerge = 1, TopMost = 2 }
    [Flags] public enum ImGuiWindowFlags { NoSavedSettings = 1, NoCollapse = 2, NoResize = 4, AlwaysAutoResize = 8, MenuBar = 16, NoScrollbar = 32, NoScrollWithMouse = 64 }
    [Flags] public enum ImGuiButtonFlags { MouseButtonLeft = 1, MouseButtonRight = 2 }
    public enum ImGuiCond { Always }
    public readonly struct ImString { }
    public readonly struct ImDrawListPtr { }
    public struct ImGuiWindowClass { public ImGuiViewportFlags ViewportFlagsOverrideSet, ViewportFlagsOverrideClear; }
    public sealed class ImGuiViewportPtr
    {
        public int ID;
        public float2 Pos, Size, WorkPos, WorkSize;
        public nint PlatformHandle;
    }
    public sealed class FakeIo { public bool AppFocusLost; public ImGuiConfigFlags ConfigFlags = ImGuiConfigFlags.ViewportsEnable; }
    public sealed class FakePlatform
    {
        public readonly List<ImGuiViewportPtr> Viewports = [];
        public readonly List<(float2 WorkPos, float2 WorkSize)> Monitors = [];
    }
    public sealed class FakeWindow(string name, int id)
    {
        public string Name = name;
        public int ID = id;
        public float2 Position;
        public ImGuiViewportPtr Viewport = null!;
        public bool IsNull() => ID == 0;
    }
    public static class ImGui
    {
        public static readonly FakeIo Io = new();
        public static readonly FakePlatform Platform = new();
        public static readonly Dictionary<string, FakeWindow> Windows = [];
        public static readonly HashSet<string> Clicks = [];
        public static ImGuiViewportPtr Main = null!;
        public static string HoveredName = "";
        public static bool CloseNext;
        public static float FontSize = 16, DetachedFontSize = 16;
        public static int BeginDepth;
        private static FakeWindow _current = null!;
        private static float2? _nextPosition;
        private static bool _detached;
        public static readonly InternalApi Internal = new();

        public static void Reset()
        {
            foreach (var viewport in Platform.Viewports)
                if (viewport.PlatformHandle != 0) GCHandle.FromIntPtr(viewport.PlatformHandle).Free();
            Platform.Viewports.Clear(); Platform.Monitors.Clear(); Windows.Clear(); Clicks.Clear();
            Main = new() { ID = 1, Size = new(1920, 1080), WorkSize = new(1920, 1080),
                PlatformHandle = GCHandle.ToIntPtr(GCHandle.Alloc(KSA.Program.Window)) };
            Platform.Viewports.Add(Main); Platform.Monitors.Add((float2.Zero, Main.WorkSize));
            Io.AppFocusLost = false; Io.ConfigFlags = ImGuiConfigFlags.ViewportsEnable;
            KSA.Program.Window.Focused = true; KSA.Program.AllowMultipleWindows = true;
            FontSize = DetachedFontSize = 16; _nextPosition = null; _detached = false;
            HoveredName = ""; CloseNext = false; BeginDepth = 0;
        }
        public static FakeIo GetIO() => Io;
        public sealed class FakeContext { public FakeWindow NavWindow => _current; }
        public static FakeContext GetCurrentContext() => new();
        public static FakePlatform GetPlatformIO() => Platform;
        public static ImGuiViewportPtr GetMainViewport() => Main;
        public static float GetFontSize() => FontSize;
        public static float2 CalcTextSize(string text) => new(text.Length * FontSize * .5f, FontSize);
        public static float GetFrameHeight() => 20;
        public static float GetFrameHeightWithSpacing() => 22;
        public static FakeStyle GetStyle() => new();
        public sealed class FakeStyle { public float2 WindowPadding => new(8); }
        public static unsafe void SetNextWindowClass(ImGuiWindowClass* windowClass)
            => _detached = (windowClass->ViewportFlagsOverrideSet & ImGuiViewportFlags.NoAutoMerge) != 0;
        public static void SetNextWindowViewport(int id) { _detached = false; }
        public static void SetNextWindowPos(float2 position, ImGuiCond condition) { _nextPosition = position; }
        public static bool Begin(string name, ref bool open, ImGuiWindowFlags flags)
        {
            if (!Windows.TryGetValue(name, out var window)) Windows.Add(name, window = new(name, Windows.Count + 10) { Viewport = Main });
            if (_detached && window.Viewport == Main)
            {
                window.Viewport = new() { ID = Windows.Count + 100,
                    PlatformHandle = GCHandle.ToIntPtr(GCHandle.Alloc(new Brutal.GlfwApi.GlfwWindow())) };
                Platform.Viewports.Add(window.Viewport);
            }
            if (!_detached) window.Viewport = Main;
            if (_nextPosition is { } position) window.Position = position;
            _nextPosition = null; _detached = false; _current = window; BeginDepth++;
            FontSize = window.Viewport == Main ? 16 : DetachedFontSize;
            if (CloseNext) { open = false; CloseNext = false; }
            return true;
        }
        public static void End() { BeginDepth--; FontSize = 16; }
        public static bool BeginMenuBar() => true;
        public static void EndMenuBar() { }
        public static bool BeginMenu(string name) => true;
        public static void EndMenu() { }
        public static bool MenuItem(string label, ImString shortcut = default, bool selected = false, bool enabled = true)
            => enabled && Clicks.Remove(label);
        public static float2 GetWindowPos() => _current.Position;
        public static void SetWindowPos(float2 position, ImGuiCond condition) { _current.Position = position; }
        public static float2 GetCursorScreenPos() => _current.Position + new float2(8, 52);
        public static ImGuiViewportPtr GetWindowViewport() => _current.Viewport;
        public static ImDrawListPtr GetWindowDrawList() => default;
        public static ImDrawListPtr GetBackgroundDrawList(ImGuiViewportPtr viewport) => default;
        public static bool InvisibleButton(string name, float2 size, ImGuiButtonFlags flags) => false;
        public sealed class InternalApi
        {
            public FakeWindow FindWindowByName(string name) => Windows.GetValueOrDefault(name) ?? new("", 0);
            public void FindHoveredWindowEx(float2 position, bool anyViewport, out FakeWindow hovered, out FakeWindow underneath)
            { hovered = FindWindowByName(HoveredName); underneath = new("", 0); }
        }
    }
}

namespace KSA
{
    public sealed class KittenEva { }
    public static class Program
    {
        public static readonly Brutal.GlfwApi.GlfwWindow Window = new();
        public static bool AllowMultipleWindows = true;
        public static ulong FrameNumber = 1;
        public static Brutal.GlfwApi.GlfwWindow GetWindow() => Window;
    }
}
