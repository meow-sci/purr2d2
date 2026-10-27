using Brutal.Numerics;
using Purr2D2.Core;

namespace Purr2D2.Core { public enum SaberType { Light, Dark } }

namespace Purr2D2.Runtime
{
    internal sealed class ManualForceBreakupClock : TimeProvider
    {
        private const long Frequency = 1_000_000_000;
        private long _timestamp;
        public override long TimestampFrequency => Frequency;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(double seconds)
        {
            if (double.IsFinite(seconds) && seconds > 0)
                _timestamp = checked(_timestamp + (long)Math.Round(seconds * Frequency));
        }
    }
}

namespace Purr2D2.Audio
{
    using KSA;
    internal sealed class ForceBreakupAudio : IDisposable
    {
        internal readonly List<string> Events = [];
        internal double3 LastEmitter;
        private int _groanUpdatesRemaining, _breakUpdatesRemaining;
        internal int Groans, Breaks, Resets, Updates;
        internal bool HasActiveChannels => _groanUpdatesRemaining > 0 || _breakUpdatesRemaining > 0;
        internal bool LastPaused, PausedAtGroan, PausedAtBreak;
        internal void Initialize() { }
        internal void PlayGroan(KittenEva owner, double3 emitterBody, float volume)
        { Groans++; LastEmitter = emitterBody; PausedAtGroan = LastPaused; _groanUpdatesRemaining = 3; Events.Add("Groan"); }
        internal void PlayBreak(KittenEva owner, double3 emitterBody, float volume)
        { Breaks++; LastEmitter = emitterBody; PausedAtBreak = LastPaused; _breakUpdatesRemaining = 3; Events.Add("Break"); }
        internal void Update(KittenEva owner, double3 emitterBody, float volume, bool paused = false)
        {
            Updates++; LastEmitter = emitterBody; LastPaused = paused;
            if (_groanUpdatesRemaining > 0) _groanUpdatesRemaining--;
            if (_breakUpdatesRemaining > 0) _breakUpdatesRemaining--;
        }
        internal void Stop() => Reset();
        internal void Reset()
        { Resets++; _groanUpdatesRemaining = _breakUpdatesRemaining = 0; Events.Add("Reset"); }
        internal void Shutdown() => Reset();
        public void Dispose() => Reset();
    }
}

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
    public readonly record struct ImFontPtr(uint FontId)
    {
        public static implicit operator int(ImFontPtr font) => unchecked((int)font.FontId);
    }
    public static class ImGui
    {
        public static readonly TestIo Io = new();
        public static string Tooltip = "";
        public static int CalcTextSizeCalls;
        public static TestIo GetIO() => Io;
        public static TestViewport GetMainViewport() => new();
        public static float GetFontSize() => 16;
        public static ImFontPtr GetFont() => new(1);
        public static float2 CalcTextSize(string text, bool hide = false, float wrap = 0)
        {
            CalcTextSizeCalls++;
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
        public readonly record struct Line(float2 Start, float2 End, uint Color, float Thickness);
        public static readonly List<Circle> Circles = [];
        public static readonly List<Rect> Rects = [];
        public static readonly List<Text> Texts = [];
        public static readonly List<DrawEvent> Events = [];
        public static readonly List<Line> Lines = [];
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
        public void AddLine(float2 start, float2 end, ImColor8 color, float thickness)
        {
            Lines.Add(new(start, end, color.Value, thickness));
            Events.Add(new("line", (start + end) * .5f, ""));
        }
    }
}
namespace KSA
{
    public interface IParentBody { }
    public sealed class CelestialSystem { }
    public class Vehicle
    {
        public string Id = "Target";
        public bool IsDisposed, IsEditedVehicle;
        public CelestialSystem System = new();
        public IParentBody Parent = new TestParentBody();
    }
    public sealed class KittenEva : Vehicle
    {
        public doubleQuat Body2Ego = doubleQuat.Identity;
    }
    internal sealed class TestParentBody : IParentBody { }
    public sealed class PartTree { public Vehicle? OwningVehicle; }
    public sealed class Part
    {
        private Part? _structuralPart;
        public bool IsAttachedInternal;
        public PartTree? Tree;
        public Part StructuralPart => _structuralPart ?? this;
        public void SetStructuralPart(Part? structuralPart) => _structuralPart = structuralPart;
    }
    public sealed class ViewportPartPicker
    {
        public Part? Part;
        public double Distance = double.MaxValue;
        public void Offer(Part? part, double distance) { Part = part; Distance = distance; }
        public void Reset() { Part = null; Distance = double.MaxValue; }
    }
    internal static class Double3Ex
    {
        internal static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
    public static class Universe
    {
        public static double SimulationSpeed = 1;
        public static double GetSimulationSpeed() => SimulationSpeed;
    }
    public struct Ray { public double3 Origin, Direction; }
    public sealed class TestViewport
    {
        public float2 Position = new(20, 30);
        public int Width => 640;
        public int Height => 480;
        public Camera Camera = new();
        public ViewportPartPicker PartPicker = new();
        public Camera GetCamera() => Camera;
    }
    public sealed class Camera
    {
        public int Generation;
        public double NearPlane = .1;
        public bool SphereOutsideFrustum;
        public double3 LastSphereCenter;
        public double LastSphereRadius;
        public int SphereFrustumChecks;
        public int2 FramebufferSize => new(640, 480);
        public double3 GetForwardEcl() => -double3.UnitZ;
        public double3 GetRightEcl() => double3.UnitX;
        public double3 GetUpEcl() => double3.UnitY;
        public float2 EgoToScreen(double3 point) => new((float)point.X, (float)point.Y);
        public double2 EgoToScreenDouble(double3 point, bool ignoreBehind = true) => new(point.X + 320, point.Y + 240);
        public double3 GetPositionEgo(KittenEva position) => double3.Zero;
        public bool IsSphereOutsideFrustum(double3 center, double radius)
        { LastSphereCenter = center; LastSphereRadius = radius; SphereFrustumChecks++; return SphereOutsideFrustum; }
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
    using Purr2D2.Core;
    internal enum ForceStatusSeverity { None, Warning, Error }

    internal sealed class ClutterForceTarget
    {
        internal string DisplayName = "Rock";
        internal bool IsValid = true;
    }
    internal sealed record ForcePick(KSA.Vehicle? Vehicle, KSA.Part? Part = null,
        ClutterForceTarget? Clutter = null, KSA.IParentBody? Parent = null, KSA.CelestialSystem? System = null)
    {
        internal bool Live = true;
        internal double3 HitEgo { get; init; } = new(0, 0, -5);
        internal string DisplayName => Vehicle?.Id ?? Clutter?.DisplayName ?? "Target";
    }
    internal readonly record struct ForceHoverPreview(KSA.Vehicle Vehicle, KSA.Part Part, double3 HitEgo);
    internal readonly record struct ForcePickRay(KSA.Ray Cci);
    internal static class ForceTargeting
    {
        internal static int Picks;
        internal static int PreviewCalls;
        internal static int ValidCalls;
        internal static KSA.Ray LastRay;
        internal static double LastTolerance;
        internal static ForcePick? Next;
        internal static bool FailPick;
        internal static bool TrySnapshotRay(KSA.KittenEva owner, KSA.Ray ray, out ForcePickRay snapshot)
        { snapshot = new(ray); return true; }
        internal static bool IsLive(KSA.Vehicle vehicle) => !vehicle.IsDisposed && !vehicle.IsEditedVehicle;
        internal static bool Valid(KSA.KittenEva owner, ForcePick pick)
        {
            ValidCalls++;
            if (!pick.Live || !IsLive(owner) || !ReferenceEquals(owner, KSA.Program.ControlledVehicle)) return false;
            if (pick.Vehicle is { } vehicle && pick.Clutter == null && pick.Part is { } part)
                return IsLive(vehicle) && !ReferenceEquals(vehicle, owner)
                    && ReferenceEquals(vehicle.System, owner.System) && ReferenceEquals(vehicle.Parent, owner.Parent)
                    && ReferenceEquals(pick.System, owner.System) && ReferenceEquals(pick.Parent, owner.Parent)
                    && ReferenceEquals(part.StructuralPart.Tree?.OwningVehicle, vehicle);
            return pick.Vehicle == null && pick.Part == null && pick.Clutter is { IsValid: true }
                && ReferenceEquals(pick.System, owner.System) && ReferenceEquals(pick.Parent, owner.Parent);
        }
        internal static bool TryPreviewNativeHit(KSA.KittenEva owner, KSA.Part part, double distance,
            KSA.Ray egoRay, KSA.Camera camera, out ForceHoverPreview preview)
        {
            PreviewCalls++;
            preview = default;
            var structural = part.StructuralPart;
            if (part.IsAttachedInternal || structural.IsAttachedInternal || !double.IsFinite(distance) || distance < 0
                || !IsLive(owner) || !ReferenceEquals(owner, KSA.Program.ControlledVehicle)
                || structural.Tree?.OwningVehicle is not { } vehicle || !IsLive(vehicle)
                || vehicle is KSA.KittenEva || ReferenceEquals(vehicle, owner)
                || !ReferenceEquals(vehicle.System, owner.System) || !ReferenceEquals(vehicle.Parent, owner.Parent)) return false;
            var direction = double3.NormalizeOrZero(egoRay.Direction);
            var point = egoRay.Origin + direction * distance;
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) return false;
            preview = new(vehicle, structural, point);
            return true;
        }
        internal static bool TryPick(KSA.KittenEva owner, ForcePickRay ray, out ForcePick? pick, out string reason,
            double clutterTolerance = .2)
        {
            if (FailPick) throw new InvalidOperationException("Test picker read failure");
            Picks++; LastRay = ray.Cci; LastTolerance = clutterTolerance;
            pick = Next; reason = "No target"; return pick != null;
        }
        internal static bool TryHitEgo(ForcePick pick, KSA.Camera camera, out double3 point) { point = pick.HitEgo; return true; }
    }
    internal sealed class ForceController : IDisposable
    {
        internal bool HasCapture, Disposed, FailUpdate;
        internal int CaptureCalls, BreakupCalls, BreakupCancels, BreakupPreparationCalls, BreakupCommitCalls;
        internal readonly List<int> BreakupPieceLimits = [];
        internal bool CanBreakup, BreakupActive, BreakupPrepared = true, BreakupCompletesOnCommit = true;
        internal int CapturedLimit;
        internal double CapturedRadius;
        internal double GroupSpread = ForceMath.DefaultGroupSpread;
        internal int SimulatedCapturedCount = 1;
        internal int CapturedCount => HasCapture ? SimulatedCapturedCount : 0;
        internal KSA.KittenEva? Owner;
        internal double3 GroupOffsetBody, ReleasedOffset;
        internal double ReleasedSpread = ForceMath.DefaultGroupSpread;
        internal double ReleasedThrowSpeed = ForceMath.ThrowSpeed;
        internal string Status = "";
        internal ForceStatusSeverity StatusSeverity;
        internal readonly List<string> Events = [];
        internal bool TryGetPoseTargetBody(KSA.KittenEva owner, out double3 target)
        { target = GroupOffsetBody; return HasCapture && ReferenceEquals(Owner, owner); }
        internal void Install(HarmonyLib.Harmony harmony) { Status = ""; StatusSeverity = ForceStatusSeverity.None; }
        internal bool Capture(KSA.KittenEva owner, ForcePick pick, bool angry, double angryRadius = ForceMath.DefaultAngryRadius,
            int captureLimit = Purr2D2.Core.ForceCaptureLimits.Default)
        {
            CaptureCalls++; CapturedRadius = angryRadius; CapturedLimit = captureLimit; Events.Add("Capture");
            if (HasCapture) { Status = "Already holding"; StatusSeverity = ForceStatusSeverity.Warning; return false; }
            if (!pick.Live) { Status = "Stale target"; StatusSeverity = ForceStatusSeverity.Warning; return false; }
            HasCapture = true; Owner = owner; GroupOffsetBody = double3.Zero; GroupSpread = ForceMath.DefaultGroupSpread;
            Status = "Holding"; StatusSeverity = ForceStatusSeverity.None; return true;
        }
        internal void Update(KSA.KittenEva? owner)
        {
            Events.Add("Update");
            if (FailUpdate) throw new InvalidOperationException("Test failure");
            if (HasCapture && !ReferenceEquals(owner, Owner)) { Release(false); Status = "Owner changed"; StatusSeverity = ForceStatusSeverity.Warning; }
        }
        internal void SetOffset(double3 offset) { Events.Add("Offset"); GroupOffsetBody = offset; }
        internal void SetSpread(double spread) { Events.Add("Spread"); GroupSpread = ForceMath.ClampGroupSpread(spread); }
        internal bool BeginBreakup(KSA.KittenEva owner, int maximumPieces = ForceBreakupLimits.Default)
        {
            if (!CanBreakup || !HasCapture || !ReferenceEquals(Owner, owner) || BreakupActive) return false;
            Events.Add("BreakupBegin"); BreakupPieceLimits.Add(maximumPieces); BreakupCalls++; BreakupActive = true; BreakupPrepared = true; return true;
        }
        internal ForceBreakupStepResult AdvanceBreakup(KSA.KittenEva owner, bool splitRequested, bool delayElapsed)
        {
            Events.Add("BreakupAdvance");
            if (!BreakupActive || !HasCapture || !ReferenceEquals(Owner, owner)) return default;
            BreakupPreparationCalls++;
            if (!BreakupPrepared) return new(true, false, false, false);
            if (!splitRequested || !delayElapsed) return new(true, false, false, false);
            BreakupCommitCalls++;
            if (BreakupCompletesOnCommit) { BreakupActive = false; return new(false, false, true, true); }
            return new(true, false, true, false);
        }
        internal void CancelBreakup()
        { if (!BreakupActive) return; Events.Add("BreakupCancel"); BreakupCancels++; BreakupActive = false; }
        internal void Release(bool thrown, double throwSpeed = ForceMath.ThrowSpeed)
        { Events.Add(thrown ? "Throw" : "Release"); ReleasedOffset = GroupOffsetBody; ReleasedSpread = GroupSpread; ReleasedThrowSpeed = throwSpeed; HasCapture = false; Owner = null; GroupSpread = ForceMath.DefaultGroupSpread; Status = "Released"; StatusSeverity = ForceStatusSeverity.None; CanBreakup = false; BreakupActive = false; }
        internal void Reset() { if (Disposed) throw new InvalidOperationException("Reset after dispose"); HasCapture = false; Owner = null; GroupSpread = ForceMath.DefaultGroupSpread; Status = ""; StatusSeverity = ForceStatusSeverity.None; CanBreakup = false; BreakupActive = false; }
        public void Dispose() => Disposed = true;
    }
}
