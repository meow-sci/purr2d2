using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace Purr2D2.Slicing;

/// <summary>Primitive counters only: no game objects, profiler state, synchronization or logging.</summary>
internal readonly record struct SliceCounterSnapshot(long Timestamp, long JitTicks, long Methods, long IlBytes,
    long AllocatedBytes, long PauseTicks, int Gen0, int Gen1, int Gen2, int ThreadId, bool Valid = true)
{
    internal static SliceCounterSnapshot Capture()
    {
        long timestamp = Stopwatch.GetTimestamp();
        try
        {
            return new(timestamp, JitInfo.GetCompilationTime(currentThread: true).Ticks,
                JitInfo.GetCompiledMethodCount(currentThread: true), JitInfo.GetCompiledILBytes(currentThread: true),
                GC.GetAllocatedBytesForCurrentThread(), GC.GetTotalPauseDuration().Ticks,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Environment.CurrentManagedThreadId);
        }
        catch { return new(timestamp, 0, 0, 0, 0, 0, 0, 0, 0, 0, false); }
    }
}

internal readonly record struct SliceCounterDelta(long WallTicks, long JitTicks, long Methods, long IlBytes,
    long AllocatedBytes, long PauseTicks, long Gen0, long Gen1, long Gen2, bool Valid)
{
    internal static SliceCounterDelta Between(in SliceCounterSnapshot start, in SliceCounterSnapshot end)
    {
        bool valid = start.Valid && end.Valid && start.ThreadId == end.ThreadId;
        return new(end.Timestamp - start.Timestamp, valid ? end.JitTicks - start.JitTicks : 0,
            valid ? end.Methods - start.Methods : 0, valid ? end.IlBytes - start.IlBytes : 0,
            valid ? end.AllocatedBytes - start.AllocatedBytes : 0, valid ? end.PauseTicks - start.PauseTicks : 0,
            valid ? end.Gen0 - start.Gen0 : 0, valid ? end.Gen1 - start.Gen1 : 0,
            valid ? end.Gen2 - start.Gen2 : 0, valid);
    }

    internal SliceCounterDelta Plus(in SliceCounterDelta other) => new(WallTicks + other.WallTicks,
        JitTicks + other.JitTicks, Methods + other.Methods, IlBytes + other.IlBytes,
        AllocatedBytes + other.AllocatedBytes, PauseTicks + other.PauseTicks,
        Gen0 + other.Gen0, Gen1 + other.Gen1, Gen2 + other.Gen2, Valid && other.Valid);

    internal double Milliseconds => WallTicks * (1000d / Stopwatch.Frequency);
    internal string Format() => Milliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms"
        + (!Valid ? " (counters unavailable)" : string.Create(CultureInfo.InvariantCulture,
            $", thread JIT {JitTicks / 10000d:F2} ms/{Methods} methods/{IlBytes} IL bytes, thread alloc {AllocatedBytes} B, global GC {Gen0}/{Gen1}/{Gen2}, global pause {PauseTicks / 10000d:F2} ms"));

    internal string FormatBrief() => Milliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms"
        + (!Valid ? " (counters unavailable)" : string.Create(CultureInfo.InvariantCulture, $"/{AllocatedBytes} B")
            + (JitTicks == 0 && Methods == 0 && IlBytes == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
                $" JIT {JitTicks / 10000d:F2} ms/{Methods} methods/{IlBytes} IL bytes"))
            + (Gen0 == 0 && Gen1 == 0 && Gen2 == 0 && PauseTicks == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
                $" global GC {Gen0}/{Gen1}/{Gen2}, pause {PauseTicks / 10000d:F2} ms")));
}

internal static class SliceDiagnosticCounters
{
    // Safe on the warmup worker: no static ring or main-thread state is changed.
    internal static void Warmup()
    {
        var first = SliceCounterSnapshot.Capture();
        var delta = SliceCounterDelta.Between(first, SliceCounterSnapshot.Capture());
        _ = delta.Plus(delta).Format();
        _ = delta.FormatBrief();
        _ = new SliceCounterDelta(1, 1, 1, 1, 1, 1, 1, 1, 1, true).FormatBrief();
        _ = (delta with { Valid = false }).Format();
    }
}
