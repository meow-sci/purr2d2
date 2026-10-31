using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using Purr2D2.Core;

namespace Purr2D2.Slicing;

internal enum SliceWorkerStage
{
    Setup, TrimBubbles, AssignOrphans, MergeBubbles, PrepareBubbles, BuildIslands,
    IslandBatch, CommitClearances, ReassignVehicles, SplitBubbles, PublishResults,
    BubbleBegin, BubbleStep, BubbleEnd, BubblePostWork, VehiclePostWork, Cluster,
    Terrain, Origin, ConstrainedStep, UnconstrainedStep, CollisionDetection, ConstraintSimulation, BepuWorker,
    Count
}

/// <summary>Worker counters deliberately exclude process-wide GC/JIT counters.</summary>
internal readonly record struct SliceWorkerStamp(long Timestamp, long JitTicks, long Methods,
    long IlBytes, long AllocatedBytes, int ThreadId, bool Valid = true)
{
    internal static SliceWorkerStamp Capture()
    {
        if (!DebugTimings.Enabled) return default;
        long timestamp = Stopwatch.GetTimestamp();
        try
        {
            return new(timestamp, JitInfo.GetCompilationTime(currentThread: true).Ticks,
                JitInfo.GetCompiledMethodCount(currentThread: true), JitInfo.GetCompiledILBytes(currentThread: true),
                GC.GetAllocatedBytesForCurrentThread(), Environment.CurrentManagedThreadId);
        }
        catch { return new(timestamp, 0, 0, 0, 0, Environment.CurrentManagedThreadId, false); }
    }
}

internal readonly record struct SliceWorkerDelta(long WallTicks, long JitTicks, long Methods,
    long IlBytes, long AllocatedBytes, bool Valid)
{
    internal static SliceWorkerDelta Between(in SliceWorkerStamp start, in SliceWorkerStamp end)
    {
        bool valid = start.Valid && end.Valid && start.ThreadId == end.ThreadId;
        return new(Math.Max(0, end.Timestamp - start.Timestamp), valid ? end.JitTicks - start.JitTicks : 0,
            valid ? end.Methods - start.Methods : 0, valid ? end.IlBytes - start.IlBytes : 0,
            valid ? end.AllocatedBytes - start.AllocatedBytes : 0, valid);
    }
    internal string Format() => string.Create(CultureInfo.InvariantCulture,
        $"{WallTicks * (1000d / Stopwatch.Frequency):F2} ms")
        + (Valid ? string.Create(CultureInfo.InvariantCulture,
            $", thread JIT {JitTicks / 10000d:F2} ms/{Methods} methods/{IlBytes} IL bytes, thread alloc {AllocatedBytes} B")
            : ", thread counters unavailable");
}

internal struct SliceWorkerMetric
{
    internal long WallTicks, JitTicks, Methods, IlBytes, AllocatedBytes, Count, Invalid;
    internal void Add(in SliceWorkerDelta value)
    {
        Interlocked.Add(ref WallTicks, value.WallTicks);
        Interlocked.Add(ref JitTicks, value.JitTicks);
        Interlocked.Add(ref Methods, value.Methods);
        Interlocked.Add(ref IlBytes, value.IlBytes);
        Interlocked.Add(ref AllocatedBytes, value.AllocatedBytes);
        if (!value.Valid) Interlocked.Increment(ref Invalid);
        Interlocked.Increment(ref Count);
    }
    internal readonly SliceWorkerDelta Value => new(WallTicks, JitTicks, Methods, IlBytes, AllocatedBytes, Invalid == 0);
}

/// <summary>One fixed-size primitive record per sampled dispatch. No engine objects are retained.
/// It becomes read-only after the existing native scheduler wait; the frame ring bounds retention.</summary>
internal sealed class SliceWorkerSample(long generation, long frame, long dispatched)
{
    internal readonly long Generation = generation, Frame = frame, Dispatched = dispatched;
    internal readonly SliceWorkerMetric[] Stages = new SliceWorkerMetric[(int)SliceWorkerStage.Count];
    internal readonly SliceWorkerMetric[] InlineChildren = new SliceWorkerMetric[(int)SliceWorkerStage.Count];
    internal readonly SliceWorkerMetric[] HelperChildren = new SliceWorkerMetric[(int)SliceWorkerStage.Count];
    internal SliceWorkerStamp Start;
    internal SliceWorkerDelta Total;
    internal long Ended, WaitStart, WaitEnd;
    internal int Cancelled, Completed, Failed;

    internal string Format()
    {
        if (Volatile.Read(ref Cancelled) != 0) return "cancelled by world reset";
        if (Volatile.Read(ref Completed) == 0) return "unavailable (queued job did not enter/finish the instrumented Run)";
        var text = new StringBuilder(2048);
        text.Append("dispatch frame ").Append(Frame).Append("; dispatch-to-Run ").Append(Ms(Start.Timestamp - Dispatched))
            .Append(" ms; Run ").Append(Total.Format()).Append(Failed != 0 ? "; native Run threw" : "")
            .Append("; next native wait ").Append(Ms(WaitEnd - WaitStart)).Append(" ms")
            .Append(" (queue overlap ").Append(Ms(Overlap(Dispatched, Start.Timestamp, WaitStart, WaitEnd)))
            .Append(" ms, Run overlap ").Append(Ms(Overlap(Start.Timestamp, Ended, WaitStart, WaitEnd)))
            .Append(" ms, after-Run tail ").Append(Ms(Math.Max(0, WaitEnd - Math.Max(WaitStart, Ended)))).Append(" ms)");
        Append(Stages, "root stage");
        Append(InlineChildren, "inline child detail (included in root)");
        Append(HelperChildren, "helper exclusive scope");
        long helperJit = 0, helperMethods = 0;
        bool valid = Total.Valid;
        foreach (var metric in HelperChildren)
        { helperJit += metric.JitTicks; helperMethods += metric.Methods; valid &= metric.Invalid == 0; }
        text.Append("\n      measured thread JIT total (root + helper exclusive scopes): ")
            .Append(((Total.JitTicks + helperJit) / 10000d).ToString("F2", CultureInfo.InvariantCulture))
            .Append(" ms/").Append(Total.Methods + helperMethods).Append(" methods")
            .Append(valid ? "" : " (partial; some thread counters unavailable)")
            .Append("; scoped wall times include waits and overlap across threads; uncovered helper code and callback entry/exit JIT are excluded");
        return text.ToString();

        void Append(SliceWorkerMetric[] metrics, string label)
        {
            for (int i = 0; i < metrics.Length; i++)
                if (metrics[i].Count != 0)
                    text.Append("\n      ").Append(label).Append(' ').Append((SliceWorkerStage)i)
                        .Append(": ").Append(metrics[i].Value.Format());
        }
        static string Ms(long ticks) => (Math.Max(0, ticks) * (1000d / Stopwatch.Frequency)).ToString("F2", CultureInfo.InvariantCulture);
        static long Overlap(long a, long b, long c, long d) => Math.Max(0, Math.Min(b, d) - Math.Max(a, c));
    }
}

internal readonly record struct SliceWorkerRunToken(SliceWorkerSample? Sample);
internal readonly record struct SliceWorkerScopeToken(SliceWorkerSample? Sample, SliceWorkerStage PreviousStage,
    bool HadParent, bool Inline);

/// <summary>Single native VehicleSolver job, with concurrently running joined children. Reset
/// invalidates records rather than reusing their storage. A cancelled queued/running job keeps
/// the gate until its own Run/finalizer or native wait, so late old callbacks cannot adopt a new epoch.
/// Dispatch/Joined run on the main thread; Run/stage callbacks on its one solver worker;
/// child callbacks may run concurrently. Reset uses atomics and may also cancel from a
/// failing optional callback; it never clears another thread's TLS.</summary>
internal static class SliceWorkerDiagnostics
{
    private static long _generation;
    private static SliceWorkerSample? _pending, _queued, _active;
    [ThreadStatic] private static SliceWorkerSample? _run;
    [ThreadStatic] private static SliceWorkerStamp _stageStart;
    [ThreadStatic] private static SliceWorkerStage _stage;
    [ThreadStatic] private static SliceWorkerSample? _child;
    [ThreadStatic] private static SliceWorkerStamp _childStart;
    [ThreadStatic] private static SliceWorkerStage _childStage;
    internal static bool Supported { get; set; }
    internal static bool HasPending => Volatile.Read(ref _pending) != null;

    internal static void Dispatch(long frame)
    {
        if (!DebugTimings.Enabled || Volatile.Read(ref _pending) != null || Volatile.Read(ref _active) != null) return;
        var sample = new SliceWorkerSample(Volatile.Read(ref _generation), frame, Stopwatch.GetTimestamp());
        if (Interlocked.CompareExchange(ref _pending, sample, null) != null) return;
        Volatile.Write(ref _queued, sample);
    }

    internal static SliceWorkerRunToken BeginRun()
    {
        var sample = Interlocked.Exchange(ref _queued, null);
        if (sample == null || Interlocked.CompareExchange(ref _active, sample, null) != null) return default;
        _run = sample;
        if (!Valid(sample)) return new(sample);
        sample.Start = SliceWorkerStamp.Capture(); _stageStart = sample.Start; _stage = SliceWorkerStage.Setup;
        return new(sample);
    }

    internal static void EnterStage(SliceWorkerStage stage)
    {
        var sample = _run;
        if (sample == null || !Valid(sample) || _stage == stage) return;
        var now = SliceWorkerStamp.Capture();
        sample.Stages[(int)_stage].Add(SliceWorkerDelta.Between(_stageStart, now));
        _stage = stage; _stageStart = now;
    }

    internal static void EndRun(in SliceWorkerRunToken token, bool failed)
    {
        var sample = token.Sample;
        if (sample == null) return;
        try
        {
            if (!Valid(sample)) return;
            var now = SliceWorkerStamp.Capture();
            sample.Stages[(int)_stage].Add(SliceWorkerDelta.Between(_stageStart, now));
            sample.Total = SliceWorkerDelta.Between(sample.Start, now); sample.Ended = now.Timestamp;
            sample.Failed = failed ? 1 : 0;
            Volatile.Write(ref sample.Completed, 1);
        }
        finally
        {
            if (ReferenceEquals(_run, sample)) _run = null;
            Interlocked.CompareExchange(ref _active, null, sample);
        }
    }

    internal static SliceWorkerScopeToken BeginChild(SliceWorkerStage stage)
    {
        var sample = _child ?? Volatile.Read(ref _active);
        if (sample == null || !Valid(sample)) return default;
        var now = SliceWorkerStamp.Capture();
        bool parent = ReferenceEquals(_child, sample), inline = now.ThreadId == sample.Start.ThreadId;
        if (parent) AddChild(sample, _childStage, _childStart, now, inline);
        var token = new SliceWorkerScopeToken(sample, _childStage, parent, inline);
        _child = sample; _childStage = stage; _childStart = now;
        return token;
    }

    internal static void EndChild(in SliceWorkerScopeToken token)
    {
        var sample = token.Sample;
        if (sample == null) return;
        try
        {
            if (Valid(sample))
            {
                var now = SliceWorkerStamp.Capture();
                AddChild(sample, _childStage, _childStart, now, token.Inline);
                _childStart = now;
            }
        }
        finally
        {
            // Restore even after Reset invalidates the sample.
            _child = token.HadParent ? sample : null; _childStage = token.PreviousStage;
        }
    }

    private static void AddChild(SliceWorkerSample sample, SliceWorkerStage stage,
        in SliceWorkerStamp start, in SliceWorkerStamp end, bool inline) =>
        (inline ? sample.InlineChildren : sample.HelperChildren)[(int)stage].Add(SliceWorkerDelta.Between(start, end));

    internal static SliceWorkerSample? Joined(long waitStart, long waitEnd)
    {
        var sample = Interlocked.Exchange(ref _pending, null);
        if (sample == null) return null;
        // Only called AFTER the game's existing wait, including the final monitored dispatch.
        Interlocked.CompareExchange(ref _queued, null, sample);
        sample.WaitStart = waitStart; sample.WaitEnd = waitEnd;
        return Valid(sample) ? sample : null;
    }

    internal static void Reset()
    {
        Interlocked.Increment(ref _generation);
        var pending = Volatile.Read(ref _pending); if (pending != null) Volatile.Write(ref pending.Cancelled, 1);
        var active = Volatile.Read(ref _active); if (active != null) Volatile.Write(ref active.Cancelled, 1);
    }
    private static bool Valid(SliceWorkerSample sample) => Volatile.Read(ref sample.Cancelled) == 0
        && DebugTimings.Enabled && sample.Generation == Volatile.Read(ref _generation);

    internal static void Warmup()
    {
        // Local record only: warmup can run on a worker without changing live epochs/TLS.
        var sample = new SliceWorkerSample(0, 0, Stopwatch.GetTimestamp());
        sample.Start = SliceWorkerStamp.Capture();
        var delta = SliceWorkerDelta.Between(sample.Start, SliceWorkerStamp.Capture());
        sample.Total = delta; sample.Completed = 1;
        sample.Stages[0].Add(delta); sample.InlineChildren[0].Add(delta); sample.HelperChildren[0].Add(delta);
        _ = sample.Format(); _ = (delta with { Valid = false }).Format();
    }
}
