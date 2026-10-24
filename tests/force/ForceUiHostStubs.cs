using Brutal.Numerics;

namespace HarmonyLib { public sealed class Harmony { } }
namespace Brutal.GlfwApi
{
    public sealed class GlfwWindow
    {
        public GlfwKeyAction LeftShift, RightShift;
        public GlfwKeyAction GetKey(GlfwKey key) => key switch
        { GlfwKey.LeftShift => LeftShift, GlfwKey.RightShift => RightShift, _ => GlfwKeyAction.Release };
    }
    public enum GlfwMouseButton { Left, Right, Middle }
    public enum GlfwKey { Escape, LeftShift, RightShift }
    public enum GlfwKeyAction { Release, Press, Repeat }
}
namespace Brutal.ImGuiApi
{
    public enum ImDrawFlags { None }
    public readonly struct ImColor8(byte r, byte g, byte b, byte a) { public uint Value => (uint)(r | g << 8 | b << 16 | a << 24); }
    public sealed class TestIo { public bool WantCaptureMouse, WantCaptureKeyboard, WantTextInput, AppFocusLost; public float2 MousePos; }
    public sealed class TestViewport { public float2 Pos = float2.Zero, Size = new(640, 480); }
    public static class ImGui
    {
        public static readonly TestIo Io = new();
        public static string Tooltip = "";
        public static TestIo GetIO() => Io;
        public static TestViewport GetMainViewport() => new();
        public static float GetFontSize() => 16;
        public static int GetFont() => 0;
        public static float2 CalcTextSize(string text, bool hide = false, float wrap = 0)
        {
            float width = text.Length * GetFontSize() * .5f;
            if (wrap > 0 && width > wrap) return new(wrap, GetFontSize() * MathF.Ceiling(width / wrap));
            return new(width, GetFontSize());
        }
        public static void SetTooltip(string text) => Tooltip = text;
    }
    public readonly struct ImDrawListPtr
    {
        public readonly record struct Circle(float2 Center, float Radius, uint Color, float Thickness, bool Filled);
        public readonly record struct Rect(float2 Min, float2 Max, uint Color, float Rounding, bool Filled, float Thickness);
        public readonly record struct Text(int Font, float Size, float2 Position, uint Color, string Value, float Wrap);
        public readonly record struct DrawEvent(string Kind, float2 Center, string Text);
        public static readonly List<Circle> Circles = [];
        public static readonly List<Rect> Rects = [];
        public static readonly List<Text> Texts = [];
        public static readonly List<DrawEvent> Events = [];
        public void AddCircleFilled(float2 point, float radius, ImColor8 color, int segments)
        {
            Circles.Add(new(point, radius, color.Value, 0, true));
            Events.Add(new("circle-filled", point, ""));
        }
        public void AddCircle(float2 point, float radius, ImColor8 color, int segments, float thickness)
        {
            Circles.Add(new(point, radius, color.Value, thickness, false));
            Events.Add(new("circle", point, ""));
        }
        public void AddRectFilled(float2 min, float2 max, ImColor8 color, float rounding = 0)
        {
            Rects.Add(new(min, max, color.Value, rounding, true, 0));
            Events.Add(new("rect-filled", (min + max) * .5f, ""));
        }
        public void AddRect(float2 min, float2 max, ImColor8 color, float rounding = 0,
            ImDrawFlags flags = ImDrawFlags.None, float thickness = 1)
        {
            Rects.Add(new(min, max, color.Value, rounding, false, thickness));
            Events.Add(new("rect", (min + max) * .5f, ""));
        }
        public void PushClipRect(float2 a, float2 b, bool intersect) { }
        public void PopClipRect() { }
        public void AddText(int font, float size, float2 position, ImColor8 color, string text, float wrap)
        {
            Texts.Add(new(font, size, position, color.Value, text, wrap));
            Events.Add(new("text", position, text));
        }
    }
}
namespace KSA
{
    public class Vehicle { public string Id = "Target"; }
    public sealed class KittenEva : Vehicle { public bool IsDisposed; public doubleQuat Body2Ego = doubleQuat.Identity; }
    public struct Ray { public double3 Origin, Direction; }
    public sealed class TestViewport
    {
        public float2 Position = new(20, 30);
        public int Width => 640;
        public int Height => 480;
        public Camera Camera = new();
        public Camera GetCamera() => Camera;
    }
    public sealed class Camera
    {
        public int Generation;
        public int2 FramebufferSize => new(640, 480);
        public double3 GetForwardEcl() => -double3.UnitZ;
        public double3 GetRightEcl() => double3.UnitX;
        public double3 GetUpEcl() => double3.UnitY;
        public float2 EgoToScreen(double3 point) => new((float)point.X, (float)point.Y);
        public Ray ScreenToEgoRay(float2 point) => new() { Origin = new(point.X, point.Y, Generation), Direction = -double3.UnitZ };
    }
    public static class Cursor { public static float2 ToViewportLocal(TestViewport view, float2 point) => point - view.Position; }
    public static class CursorTarget { public static bool Hit = true; public static bool IsHitTestViewport(TestViewport view) => Hit; }
    public static class Program
    {
        public static object? ControlledVehicle;
        public static bool EditorFlag, Modal, HasMouseInputLatch;
        public static readonly Brutal.GlfwApi.GlfwWindow Window = new();
        public static TestViewport MainViewport = new();
        public static Brutal.GlfwApi.GlfwWindow GetWindow() => Window;
        public static bool IsModalOpen() => Modal;
        public static Camera GetMainCamera() => MainViewport.Camera;
    }
}
namespace Purr2D2.Force
{
    internal enum ForceStatusSeverity { None, Warning, Error }

    internal sealed record ForcePick(KSA.Vehicle Vehicle) { internal bool Live = true; internal string DisplayName => Vehicle.Id; }
    internal readonly record struct ForcePickRay(KSA.Ray Cci);
    internal static class ForceTargeting
    {
        internal static int Picks;
        internal static KSA.Ray LastRay;
        internal static double LastTolerance;
        internal static ForcePick? Next;
        internal static bool FailPick;
        internal static bool TrySnapshotRay(KSA.KittenEva owner, KSA.Ray ray, out ForcePickRay snapshot)
        { snapshot = new(ray); return true; }
        internal static bool TryPick(KSA.KittenEva owner, ForcePickRay ray, out ForcePick? pick, out string reason,
            double clutterTolerance = .2)
        {
            if (FailPick) throw new InvalidOperationException("Test picker read failure");
            Picks++; LastRay = ray.Cci; LastTolerance = clutterTolerance;
            pick = Next; reason = "No target"; return pick != null;
        }
        internal static bool TryHitEgo(ForcePick pick, KSA.Camera camera, out double3 point) { point = new(0, 0, -5); return true; }
    }
    internal sealed class ForceController : IDisposable
    {
        internal bool HasCapture, Disposed, FailUpdate;
        internal int CaptureCalls, BreakupCalls;
        internal int CapturedLimit;
        internal double CapturedRadius;
        internal double GroupSpread = ForceMath.DefaultGroupSpread;
        internal int CapturedCount => HasCapture ? 1 : 0;
        internal KSA.KittenEva? Owner;
        internal double3 GroupOffsetBody, ReleasedOffset;
        internal double ReleasedSpread = ForceMath.DefaultGroupSpread;
        internal double ReleasedThrowSpeed = ForceMath.ThrowSpeed;
        internal string Status = "";
        internal ForceStatusSeverity StatusSeverity;
        internal readonly List<string> Events = [];
        internal void Install(HarmonyLib.Harmony harmony) { Status = ""; StatusSeverity = ForceStatusSeverity.None; }
        internal bool Capture(KSA.KittenEva owner, ForcePick pick, bool angry, double angryRadius = 10,
            int captureLimit = Purr2D2.Core.ForceCaptureLimits.Default)
        {
            CaptureCalls++; CapturedRadius = angryRadius; CapturedLimit = captureLimit; Events.Add("Capture");
            if (!pick.Live) { Status = "Stale target"; StatusSeverity = ForceStatusSeverity.Warning; return false; }
            HasCapture = true; Owner = owner; GroupOffsetBody = double3.Zero; GroupSpread = ForceMath.DefaultGroupSpread; Status = "Holding"; StatusSeverity = ForceStatusSeverity.None; return true;
        }
        internal void Update(KSA.KittenEva? owner)
        {
            Events.Add("Update");
            if (FailUpdate) throw new InvalidOperationException("Test failure");
            if (HasCapture && !ReferenceEquals(owner, Owner)) { Release(false); Status = "Owner changed"; StatusSeverity = ForceStatusSeverity.Warning; }
        }
        internal void SetOffset(double3 offset) { Events.Add("Offset"); GroupOffsetBody = offset; }
        internal void SetSpread(double spread) { Events.Add("Spread"); GroupSpread = ForceMath.ClampGroupSpread(spread); }
        internal void BreakApart(KSA.KittenEva owner) { Events.Add("Breakup"); BreakupCalls++; }
        internal void Release(bool thrown, double throwSpeed = ForceMath.ThrowSpeed)
        { Events.Add(thrown ? "Throw" : "Release"); ReleasedOffset = GroupOffsetBody; ReleasedSpread = GroupSpread; ReleasedThrowSpeed = throwSpeed; HasCapture = false; Owner = null; GroupSpread = ForceMath.DefaultGroupSpread; Status = "Released"; StatusSeverity = ForceStatusSeverity.None; }
        internal void Reset() { if (Disposed) throw new InvalidOperationException("Reset after dispose"); HasCapture = false; Owner = null; GroupSpread = ForceMath.DefaultGroupSpread; Status = ""; StatusSeverity = ForceStatusSeverity.None; }
        public void Dispose() => Disposed = true;
    }
}
