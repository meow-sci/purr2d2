using Brutal.Numerics;

namespace HarmonyLib { public sealed class Harmony { } }
namespace Brutal.GlfwApi
{
    public sealed class GlfwWindow { }
    public enum GlfwMouseButton { Left, Right, Middle }
    public enum GlfwKey { Escape }
    public enum GlfwKeyAction { Release, Press, Repeat }
}
namespace Brutal.ImGuiApi
{
    public readonly struct ImColor8(byte r, byte g, byte b, byte a) { public uint Value => (uint)(r | g << 8 | b << 16 | a << 24); }
    public sealed class TestIo { public bool WantCaptureMouse, WantCaptureKeyboard, WantTextInput, AppFocusLost; public float2 MousePos; }
    public sealed class TestViewport { public float2 Pos = float2.Zero, Size = new(640, 480); }
    public static class ImGui
    {
        public static readonly TestIo Io = new();
        public static TestIo GetIO() => Io;
        public static TestViewport GetMainViewport() => new();
        public static float GetFontSize() => 16;
        public static int GetFont() => 0;
        public static float2 CalcTextSize(string text, bool hide = false, float wrap = 0) => new(100, 20);
        public static void SetTooltip(string text) { }
    }
    public readonly struct ImDrawListPtr
    {
        public readonly record struct Circle(float2 Center, float Radius, uint Color, float Thickness, bool Filled);
        public static readonly List<Circle> Circles = [];
        public void AddCircleFilled(float2 point, float radius, ImColor8 color, int segments)
            => Circles.Add(new(point, radius, color.Value, 0, true));
        public void AddCircle(float2 point, float radius, ImColor8 color, int segments, float thickness)
            => Circles.Add(new(point, radius, color.Value, thickness, false));
        public void PushClipRect(float2 a, float2 b, bool intersect) { }
        public void PopClipRect() { }
        public void AddText(int font, float size, float2 position, ImColor8 color, string text, float wrap) { }
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
        public static bool EditorFlag, Modal;
        public static readonly Brutal.GlfwApi.GlfwWindow Window = new();
        public static TestViewport MainViewport = new();
        public static Brutal.GlfwApi.GlfwWindow GetWindow() => Window;
        public static bool IsModalOpen() => Modal;
        public static Camera GetMainCamera() => MainViewport.Camera;
    }
}
namespace Purr2D2.Force
{
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
        internal int CaptureCalls;
        internal int CapturedLimit;
        internal double CapturedRadius;
        internal int CapturedCount => HasCapture ? 1 : 0;
        internal KSA.KittenEva? Owner;
        internal double3 GroupOffsetBody, ReleasedOffset;
        internal string Status = "";
        internal readonly List<string> Events = [];
        internal void Install(HarmonyLib.Harmony harmony) { }
        internal bool Capture(KSA.KittenEva owner, ForcePick pick, bool angry, double angryRadius = 10,
            int captureLimit = Purr2D2.Core.ForceCaptureLimits.Default)
        {
            CaptureCalls++; CapturedRadius = angryRadius; CapturedLimit = captureLimit; Events.Add("Capture");
            if (!pick.Live) { Status = "Stale target"; return false; }
            HasCapture = true; Owner = owner; GroupOffsetBody = double3.Zero; Status = "Holding"; return true;
        }
        internal void Update(KSA.KittenEva? owner)
        {
            Events.Add("Update");
            if (FailUpdate) throw new InvalidOperationException("Test failure");
            if (HasCapture && !ReferenceEquals(owner, Owner)) { Release(false); Status = "Owner changed"; }
        }
        internal void SetOffset(double3 offset) { Events.Add("Offset"); GroupOffsetBody = offset; }
        internal void Release(bool thrown)
        { Events.Add(thrown ? "Throw" : "Release"); ReleasedOffset = GroupOffsetBody; HasCapture = false; Owner = null; Status = "Released"; }
        internal void Reset() { if (Disposed) throw new InvalidOperationException("Reset after dispose"); HasCapture = false; Owner = null; }
        public void Dispose() => Disposed = true;
    }
}
