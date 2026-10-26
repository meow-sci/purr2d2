#pragma warning disable CS0649, CS0414

using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;

namespace Brutal.ImGuiApi
{
    internal enum ImGuiMouseButton { Left, Right }
    internal sealed class ImGuiIO { internal bool WantCaptureMouse, WantTextInput; }
    internal static class ImGui
    {
        internal static bool RightClick;
        internal static ImGuiIO GetIO() => new();
        internal static bool IsMouseClicked(ImGuiMouseButton button) => button == ImGuiMouseButton.Right && RightClick;
    }
}

namespace Brutal.Numerics
{
    internal static class TestNumericsExtensions
    {
        internal static double3 NormalizeOrZero(this double3 value) => double3.NormalizeOrZero(value);
        internal static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
    }
}

namespace KSA
{
    internal sealed class KittenEva { internal bool IsDisposed; internal doubleQuat Body2Ego = doubleQuat.Identity; }
    internal sealed class Vehicle
    {
        internal bool IsDisposed;
        internal double4x4 GetMatrixAsmb2Ego(Camera camera) => double4x4.Identity;
    }
    internal sealed class Part { internal string DisplayName = "test part"; }
    internal sealed class Camera { internal double3 GetPositionEgo(KittenEva owner) => double3.Zero; }
    internal struct Ray(double3 origin, double3 direction)
    { internal double3 Origin { get; } = origin; internal double3 Direction { get; set; } = direction; }
    internal sealed class PartPicker { internal Part? Part; internal double Distance; }
    internal sealed class Viewport { internal readonly PartPicker PartPicker = new(); }
    internal static class CursorTarget { internal static bool IsHitTestViewport(Viewport viewport) => true; }
    internal static class Cursor { internal static Ray GetEgoRay(Viewport viewport) => new(double3.Zero, double3.UnitZ); }
    internal static class Program
    {
        internal static bool DrawUI = false, EditorFlag = false;
        internal static Viewport MainViewport = new();
        internal static Camera? GetMainCamera() => null;
    }
    internal static class Universe { internal static double GetSimulationSpeed() => 1; }
}

namespace Purr2D2.Cutting
{
    using KSA;

    internal sealed record SliceTarget(Part Part, Vehicle Vehicle, double3 HitAssembly, double4x4 PartTransform);
    internal readonly record struct SlicePlane(double3 Normal, double Distance);
    internal readonly record struct SliceAttempt(bool Success, string Message);
    internal sealed partial class PartSliceTargeting
    {
        internal sealed class Selection : IDisposable
        {
            internal SliceTarget? Target;
            internal string Reason = "No target.";
            internal bool Done;
            internal int AdvanceCount;
            internal void Advance() => AdvanceCount++;
            public void Dispose() { }
        }
        internal Selection? BeginSelection(KittenEva owner, double3 origin, double3 direction, double3 baseBody, double reach) => new();
        internal bool CheckSliceTarget(KittenEva owner, SliceTarget target, double3 baseBody, double reach, out string reason)
        { reason = ""; return true; }
        internal bool CheckPendingSliceTarget(KittenEva owner, SliceTarget target, out string reason)
        { reason = ""; return true; }
        internal SliceTarget? PreviewPointTarget(KittenEva owner, Part part, double3 hit, double3 baseBody, double reach,
            out bool allowed, out string reason)
        { allowed = false; reason = ""; return null; }
        internal SliceAttempt CommitAcceptedSlice(KittenEva owner, SliceTarget target, SlicePlane unused)
        { CommitCount++; return new(true, "committed"); }
        internal int CommitCount;
    }
}

namespace Purr2D2.Pose
{
    using KSA;
    internal sealed class SaberPoseController
    {
        internal bool ClashHeld;
        internal bool TryGetBladeSegment(double length, out double3 baseBody, out double3 tipBody)
        { baseBody = double3.Zero; tipBody = new(0, length, 0); return true; }
        internal void SetSliceSwing(float3 direction, float3 normal, double progress) { }
        internal void ClearSliceSwing() { }
    }
    internal static class SaberPoseMath { internal static double4x4 ModelToBody(float scale, float3 center) => double4x4.Identity; }
}

namespace Purr2D2.Runtime
{
    using KSA;
    using Purr2D2.Pose;

    internal sealed partial class SaberRuntime
    {
        private KittenEva? _owner = new();
        private readonly SaberPoseController _pose = new();
        private readonly TestBlade _blade = new();
        private readonly TestSlicer _slicer = new();
        private readonly PartSliceTargeting _targeting = new();
        private readonly TestRenderer _renderer = new();
        private readonly TestSettings _settings = new();
        private readonly Queue<Request> _commands = new();
        private bool SomersaultActive, _manualSliceActive, _manualIsolated, _freshPoseThisTick = true;
        private bool OverlayCapturesPointer;
        private string? _fault;
        private double _playerTime;
        private float Length => 1;
        private bool SliceWorkActive => _sliceSequence.Active || _manualSliceActive;
        private readonly TestManualGesture _manualGesture = new();

        private enum Command { CancelSlice, CutPicked }
        private readonly record struct Request(KittenEva Owner, Command Value, SliceTarget? PartTarget = null, Ray? PickRay = null);
        private sealed class TestSettings { internal float SliceReachMultiplier = 1; }
        private sealed class TestManualGesture { internal bool InFlight; }
        private sealed class TestBlade
        {
            internal bool Equipped = true, TargetOn = true;
            internal float Deployment = 1;
            internal void SetIgnited(bool on) => TargetOn = on;
        }
        private sealed class TestRenderer { internal void AddCutSparks(Part part, double3 point, double3 normal, double now) { } }
        private sealed class TestIsolation { internal Vehicle Owner = new(); internal bool ContentsReleased; }
        private sealed class TestSlicer
        {
            internal int BeginCount, AdvanceCount, CancelCount;
            internal bool Busy, Ready;
            internal string? Error;
            internal string Phase = "Preparing";
            internal TestIsolation? Isolation;
            internal void Begin(Part part) => BeginCount++;
            internal void Advance() => AdvanceCount++;
            internal void Cancel() => CancelCount++;
        }

        private void CancelForcePicker() { }
        private void EnsureBlade() { }
        private void FinishManualContactAttempt(bool attempted) { }
        private void Queue(Command command) { if (_owner is { } owner) _commands.Enqueue(new(owner, command)); }
        private SliceAttempt IsolateSliceTarget() => new(true, "isolated");

        internal void BeginSelectionForTest() => SelectPartToSlice();
        internal void BeginSliceForTest(SliceTarget target) => BeginAssistedSlice(target, null);
        internal void PumpSliceForTest(double dt) => UpdateAssistedSlice(dt);
        internal bool SelectingForTest => _selectingSlice;
        internal int QueuedCommandsForTest => _commands.Count;
        internal void HandleSlicePointerForTest() => HandleSlicePointer();
        internal bool SliceSequenceActiveForTest => _sliceSequence.Active;
        internal bool SliceStartedForTest => _sliceStarted;
        internal int SliceBeginCountForTest => _slicer.BeginCount;
        internal int SliceAdvanceCountForTest => _slicer.AdvanceCount;
        internal int SliceCommitCountForTest => _targeting.CommitCount;
        internal void HoldClashForTest(bool held) => _pose.ClashHeld = held;
        internal void AcceptSliceBeforeClashForTest(SliceTarget target)
        { BeginAssistedSlice(target, null); _pose.ClashHeld = true; }
        internal void MarkSliceAlreadyStartedForTest(SliceTarget target)
        { _sliceTarget = target; _sliceStarted = true; _sliceSequence.Start(); }
        internal void SetSliceReadyForTest(bool ready) => _slicer.Ready = ready;
        internal static SliceTarget NewTargetForTest() => new(new Part(), new Vehicle(), double3.Zero, double4x4.Identity);
    }
}
