using Brutal.Numerics;

namespace Brutal.GlfwApi
{
    public enum GlfwKey { Escape, Space }
    public enum GlfwKeyAction { Press, Release, Repeat }
    public sealed class GlfwWindow { }
}
namespace Brutal.ImGuiApi
{
    public sealed class DropIo { public bool AppFocusLost; }
    public sealed class DropViewport { public float2 Pos; public float2 Size = new(1920, 1080); }
    public static class ImGui
    {
        public static readonly DropIo Io = new();
        public static readonly DropViewport Viewport = new();
        private static readonly object Font = new();
        public static DropIo GetIO() => Io;
        public static DropViewport GetMainViewport() => Viewport;
        public static object GetFont() => Font;
        public static float GetFontSize() => 16;
    }
    public readonly struct ImColor8(int r, int g, int b, int a)
    { public readonly int R = r, G = g, B = b, A = a; }
    public enum ImDrawFlags { None }
    public readonly struct ImDrawListPtr
    {
        public void AddRectFilled(float2 min, float2 max, ImColor8 color, float rounding) { }
        public void AddRect(float2 min, float2 max, ImColor8 color, float rounding, ImDrawFlags flags, float thickness) { }
        public void AddLine(float2 a, float2 b, ImColor8 color, float thickness) { }
        public void AddCircleFilled(float2 center, float radius, ImColor8 color, int segments) { }
        public void AddCircle(float2 center, float radius, ImColor8 color, int segments, float thickness) { }
        public void AddText(object font, float size, float2 position, ImColor8 color, string text, float wrap)
            => DropDrawText.Calls.Add((font, size, position, color, text, wrap));
    }
    internal static class DropDrawText
    {
        internal static readonly List<(object Font, float Size, float2 Position, ImColor8 Color, string Text, float Wrap)> Calls = new();
    }
}
namespace KSA
{
    public sealed class KittenEva { public bool IsDisposed; public bool HasPhysicsBubble = true; }
    internal static class Program
    {
        internal static readonly Brutal.GlfwApi.GlfwWindow Window = new();
        internal static bool Modal, Latch;
        internal static Brutal.GlfwApi.GlfwWindow GetWindow() => Window;
        internal static bool IsModalOpen() => Modal;
        internal static bool HasMouseInputLatch => Latch;
    }
}
