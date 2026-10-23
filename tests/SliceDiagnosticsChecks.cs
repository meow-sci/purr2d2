using System.Diagnostics;
using Purr2D2.Slicing;

internal static class SliceDiagnosticsChecks
{
    internal static void Run()
    {
        var start = Snapshot(1);
        var end = Snapshot(3);
        var delta = SliceCounterDelta.Between(start, end);
        Require(delta.Valid && delta.WallTicks == 2 * Stopwatch.Frequency && delta.JitTicks == 20000
            && delta.Methods == 4 && delta.IlBytes == 400 && delta.AllocatedBytes == 2000
            && delta.PauseTicks == 4000 && delta.Gen0 == 6 && delta.Gen1 == 4 && delta.Gen2 == 2,
            "Snapshot deltas must preserve wall, thread JIT/IL/method/allocation and all global GC counters.");
        var doubled = delta.Plus(delta);
        Require(doubled.WallTicks == delta.WallTicks * 2 && doubled.Methods == 8 && doubled.Gen2 == 4,
            "Repeated phase segments must be summed.");
        Require(!SliceCounterDelta.Between(start, end with { ThreadId = 2 }).Valid,
            "Thread-local counters from different threads cannot be compared.");
        var unavailable = SliceCounterDelta.Between(start, end with { Valid = false });
        Require(!unavailable.Valid && unavailable.WallTicks == delta.WallTicks && unavailable.Methods == 0,
            "Unavailable counters must preserve wall duration and report counter unavailability.");

        var ring = new SliceDiagnosticRing();
        ring.EnterFrame(start);
        long isolation = ring.Begin("isolation", "fixture", 12, -1, -1, start);
        ring.SetSourceDetails(3, 2);
        ring.RecordPhase(isolation, SliceCommitPhase.Isolation, delta);
        ring.RecordPhase(isolation, SliceCommitPhase.Isolation, delta);
        ring.RecordSegment(SliceFrameSegment.DirtyDerived, delta);
        ring.HookReturned(Snapshot(2));
        Require(ring.CurrentOperation == 0 && ring.HasActive && ring.TakeCompleted() == null,
            "Ending the settled hook must stop phase association without closing the frame window.");
        ring.EnterFrame(Snapshot(3));
        long swap = ring.Begin("swap", "fixture", 3, 3, 0, Snapshot(3));
        ring.RecordPhase(swap, SliceCommitPhase.Registration, delta);
        for (int index = 4; index <= 6; index++) ring.EnterFrame(Snapshot(index));
        Require(ring.HasActive && ring.TakeCompleted() == null,
            "Formatting an isolation summary must wait until an overlapping swap finishes its next three frames.");
        ring.EnterFrame(Snapshot(7));
        Require(!ring.HasActive && ring.PendingCount == 2, "Each operation must own exactly four intervals.");
        string first = ring.TakeCompleted()!;
        Require(first.Contains("isolation; source fixture; owner parts 12; source parts including internal 3; isolation boundaries 2")
            && first.Contains("native Isolation: 4000.00 ms") && first.Contains("/8 methods/")
            && first.Contains("operation frame interval: 2000.00 ms") && first.Contains("hook tail: 1000.00 ms")
            && first.Contains("next frame +3 interval:") && !first.Contains("next frame +4")
            && first.Contains("vehicle wait: unavailable"), "Completed summaries must retain source, phase, interval and missing-segment evidence.");
        string second = ring.TakeCompleted()!;
        Require(second.Contains("swap; source fixture") && second.Contains("native Registration:")
            && !second.Contains("native Isolation:"), "Overlapping operations must keep their phases separate.");
        Require(ring.PendingCount == 0 && ring.TakeCompleted() == null, "Completed records must be released exactly once.");

        ring.ForgetFrame();
        ring.Begin("fallback", "fixture", 1, 1, 0, Snapshot(10));
        for (int index = 11; index <= 14; index++) ring.EnterFrame(Snapshot(index));
        Require(ring.TakeCompleted()!.Contains("partial operation-start interval"),
            "An operation without an armed frame baseline must identify its partial first interval.");

        ring.Reset(); ring.EnterFrame(start);
        for (int index = 0; index < SliceDiagnosticRing.Capacity; index++)
            Require(ring.Begin("bounded", "fixture", index, 1, 0, start) != 0, "Ring unexpectedly filled early.");
        Require(ring.Begin("overflow", "fixture", 0, 0, 0, start) == 0 && ring.Dropped == 1
            && ring.PendingCount == SliceDiagnosticRing.Capacity, "Overflow must drop telemetry without overwriting pending windows.");
        ring.Reset();
        Require(!ring.HasActive && ring.PendingCount == 0 && ring.Dropped == 0, "Reset must release all pending telemetry.");

        SliceDiagnosticCounters.Warmup(); SliceFrameDiagnostics.Warmup(); SliceCommitTimings.Warmup();
        SliceFrameDiagnostics.Reset();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            SliceFrameDiagnostics.EnterFrame(); SliceFrameDiagnostics.HookReturned();
            var inactive = SliceFrameDiagnostics.StartSegment();
            SliceFrameDiagnostics.EndSegment(SliceFrameSegment.VehicleWait, inactive);
            SliceFrameDiagnostics.PumpSummaries();
        }
        Require(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "Idle frame diagnostics must not allocate.");
        Console.WriteLine("PASS: slice counter scopes, bounded overlapping four-interval windows, metadata, overflow, reset and allocation-free idle hooks");
    }

    private static SliceCounterSnapshot Snapshot(int unit) => new(unit * Stopwatch.Frequency, unit * 10000L,
        unit * 2L, unit * 200L, unit * 1000L, unit * 2000L, unit * 3, unit * 2, unit, 1);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
