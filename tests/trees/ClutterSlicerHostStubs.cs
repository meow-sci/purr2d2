using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Force;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

// Only the external capture/render/native boundaries are controlled. The
// production ClutterSlicer and actual asynchronous worker run unchanged.
internal static class ClutterScenario
{
    internal static string? CaptureError, WorkerError, UploadError;
    internal static bool ToppleSucceeds = true, ReplaceSucceeds = true, MarkHotThrows, SparkDataThrows;
    internal static string ToppleReason = "The tree entered native physics and is toppling.";
    internal static string ReplaceReason = "";
    internal static int Topples, Replacements, HotMarks, CaptureDisposals, UploadDisposals, UploadAdvances;
    internal static int MainThread, CaptureThread, CommitThread, WorkerThread;
    internal static Action? WorkerEntered;
    internal static ManualResetEventSlim? WorkerRelease;
    internal static void Reset()
    {
        CaptureError = WorkerError = UploadError = null;
        ToppleSucceeds = ReplaceSucceeds = true; MarkHotThrows = SparkDataThrows = false;
        ToppleReason = "The tree entered native physics and is toppling."; ReplaceReason = "";
        Topples = Replacements = HotMarks = CaptureDisposals = UploadDisposals = UploadAdvances = 0;
        MainThread = Environment.CurrentManagedThreadId; CaptureThread = CommitThread = WorkerThread = 0;
        WorkerEntered = null; WorkerRelease = null;
    }
}

namespace KSA
{
    internal sealed class KittenEva;
    internal sealed class Part;
    internal sealed class Vehicle { internal PartTree Parts { get; } = new(); }
    internal sealed class PartTree { internal Part Root { get; } = new(); }
    internal static class BodyTransformExtensions
    {
        internal static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
        internal static doubleQuat Inverse(this doubleQuat value) => doubleQuat.Inverse(value);
    }
}

namespace Purr2D2.Force
{
    internal sealed class ClutterForceTarget
    {
        internal bool IsValid = true, Local = true;
        internal bool IsLocalTo(KSA.KittenEva owner) => Local;
    }
    internal sealed record ClutterSliceDescription(ClutterForceTarget Target, bool Tree = true);
    internal readonly record struct ClutterSliceFrame(double3 PositionCci, doubleQuat Object2Cci);
    internal static class ClutterForceNative
    {
        internal static bool TryDescribeSlice(ClutterForceTarget target, out ClutterSliceDescription? source)
        { source = new(target); return target.IsValid; }
        internal static bool TrySliceFrame(ClutterSliceDescription source, out ClutterSliceFrame frame)
        { frame = new(default, doubleQuat.Identity); return source.Target.IsValid; }
        internal static bool ToppleClutter(ClutterSliceDescription source, double3 motion, out string reason)
        {
            ClutterScenario.Topples++; ClutterScenario.CommitThread = Environment.CurrentManagedThreadId;
            reason = ClutterScenario.ToppleReason; return ClutterScenario.ToppleSucceeds;
        }
    }
}

namespace Purr2D2.Cutting
{
    internal readonly record struct CutPoint(double X, double Y, double Z);
    internal sealed class ContactSlice;
    internal readonly record struct SlicePlane;
    internal static class ContactSliceGeometry
    {
        internal static bool TryCreate(CutPoint point, CutPoint blade, CutPoint motion,
            out ContactSlice contact, out string reason)
        { contact = new(); reason = ""; return true; }
    }
    internal sealed class ClutterSliceSourceCapture : IDisposable
    {
        internal bool Done { get; private set; }
        internal SliceInput? Input { get; private set; }
        internal ClutterSliceSourceCapture(ClutterSliceDescription source, ContactSlice contact) { }
        internal void Advance()
        {
            ClutterScenario.CaptureThread = Environment.CurrentManagedThreadId;
            if (ClutterScenario.CaptureError is { } error) throw new InvalidDataException(error);
            Done = true; Input = new();
        }
        public void Dispose() => ClutterScenario.CaptureDisposals++;
    }
    internal static class ClutterSliceGeometry
    {
        internal static CpuSlice Calculate(SliceInput input, bool tree, CancellationToken cancel, Action<SlicePlan> publish)
        {
            ClutterScenario.WorkerThread = Environment.CurrentManagedThreadId;
            ClutterScenario.WorkerEntered?.Invoke(); ClutterScenario.WorkerRelease?.Wait();
            cancel.ThrowIfCancellationRequested();
            if (ClutterScenario.WorkerError is { } error) throw new InvalidDataException(error);
            return new(default, [new(), new()]);
        }
    }
}

namespace Purr2D2.Slicing
{
    internal sealed class SliceInput;
    internal sealed class SlicePlan;
    internal sealed record CpuSlice(SlicePlane Plane, CpuFragmentAsset[] Fragments);
    internal sealed class CpuFragmentAsset
    {
        internal CutSpark[] Sparks => ClutterScenario.SparkDataThrows
            ? throw new InvalidOperationException("post-commit fixture data fault")
            : Enumerable.Range(0, 6).Select(_ => new CutSpark(default, default)).ToArray();
    }
    internal readonly record struct CutSpark(double3 Point, double3 Normal);
    internal sealed record PreparedPhysicsFragment(object Asset);
    internal static class NativeSlicePhysics
    {
        internal static bool TryReplaceClutter(KSA.KittenEva owner, ClutterSliceDescription source,
            IReadOnlyList<PreparedPhysicsFragment> pieces, SlicePlane plane,
            out IReadOnlyList<KSA.Vehicle> created, out string reason)
        {
            ClutterScenario.Replacements++; ClutterScenario.CommitThread = Environment.CurrentManagedThreadId;
            created = ClutterScenario.ReplaceSucceeds ? [new(), new()] : [];
            reason = ClutterScenario.ReplaceReason; return ClutterScenario.ReplaceSucceeds;
        }
    }
}

namespace Purr2D2.Rendering
{
    internal sealed record LoadedFragment(CpuFragmentAsset Data);
    internal sealed record LoadedSlice(CpuSlice Data, LoadedFragment[] Fragments);
    internal static class RuntimeFragmentAssets
    {
        internal static (string Directory, object Mod) CaptureWorkerContext() => ("", new());
        internal static LoadedSlice LoadDetached(CpuSlice calculated, string directory, object mod, CancellationToken cancel) =>
            new(calculated, calculated.Fragments.Select(fragment => new LoadedFragment(fragment)).ToArray());
        internal static void MarkHot(object asset, double now)
        {
            ClutterScenario.HotMarks++;
            if (ClutterScenario.MarkHotThrows) throw new InvalidOperationException("hot-mark fixture fault");
        }
        internal sealed class UploadJob : IDisposable
        {
            internal readonly List<PreparedPhysicsFragment> Fragments = [new(new()), new(new())];
            internal bool Done { get; private set; }
            internal string? Error { get; private set; }
            internal UploadJob(LoadedSlice source) { }
            internal void Advance() { ClutterScenario.UploadAdvances++; Done = true; Error = ClutterScenario.UploadError; }
            internal void Cancel() { }
            public void Dispose() => ClutterScenario.UploadDisposals++;
        }
    }
}
