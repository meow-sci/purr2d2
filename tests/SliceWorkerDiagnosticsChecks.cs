using System.Diagnostics;
using Purr2D2.Core;
using Purr2D2.Slicing;

internal static class SliceWorkerDiagnosticsChecks
{
    internal static void Run()
    {
        DebugTimings.Enabled = true;
        var a = new SliceWorkerStamp(Stopwatch.Frequency, 100, 2, 3, 4, 7);
        var b = new SliceWorkerStamp(2 * Stopwatch.Frequency, 300, 5, 9, 15, 7);
        var delta = SliceWorkerDelta.Between(a, b);
        Require(delta.Valid && delta.WallTicks == Stopwatch.Frequency && delta.JitTicks == 200
            && delta.Methods == 3 && delta.IlBytes == 6 && delta.AllocatedBytes == 11,
            "Worker deltas must use current-thread counters only.");
        Require(!SliceWorkerDelta.Between(a, b with { ThreadId = 8 }).Valid,
            "Worker counters cannot cross thread boundaries.");
        var metric = new SliceWorkerMetric[1];
        Parallel.For(0, 4, _ => { for (int i = 0; i < 1000; i++) metric[0].Add(delta); });
        Require(metric[0].Count == 4000 && metric[0].JitTicks == 800000 && metric[0].IlBytes == 24000,
            "Concurrent helper metrics must not lose updates.");

        SliceWorkerDiagnostics.Reset(); SliceWorkerDiagnostics.Joined(0, 0);
        SliceWorkerDiagnostics.Warmup();
        SliceWorkerDiagnostics.BeginRun(); SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BepuWorker);
        SliceWorkerDiagnostics.EnterStage(SliceWorkerStage.TrimBubbles);
        SliceWorkerDiagnostics.EndChild(default); SliceWorkerDiagnostics.EndRun(default, false);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            var noRun = SliceWorkerDiagnostics.BeginRun();
            var noChild = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BepuWorker);
            SliceWorkerDiagnostics.EnterStage(SliceWorkerStage.TrimBubbles);
            SliceWorkerDiagnostics.EndChild(noChild); SliceWorkerDiagnostics.EndRun(noRun, false);
        }
        Require(GC.GetAllocatedBytesForCurrentThread() == allocated, "Idle worker hooks must allocate nothing.");

        allocated = GC.GetAllocatedBytesForCurrentThread();
        SliceWorkerDiagnostics.Dispatch(42);
        long sampleBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Require(sampleBytes > 0 && sampleBytes <= 8192,
            "An active dispatch must allocate one bounded record with fixed metric arrays (at most 8 KiB).");
        var run = SliceWorkerDiagnostics.BeginRun();
        Require(run.Sample != null, "A sampled dispatch must be claimed by its Run.");
        SliceWorkerDiagnostics.EnterStage(SliceWorkerStage.MergeBubbles);
        var inline = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BubbleBegin);
        SliceWorkerDiagnostics.EndChild(inline);
        Task.Run(() =>
        {
            var outer = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BubbleStep);
            var inner = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BepuWorker);
            SliceWorkerDiagnostics.EndChild(inner); SliceWorkerDiagnostics.EndChild(outer);
        }).GetAwaiter().GetResult();
        SliceWorkerDiagnostics.EndRun(run, false);
        var sample = SliceWorkerDiagnostics.Joined(Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp())!;
        Require(sample.Frame == 42 && sample.Completed == 1 && sample.Stages[(int)SliceWorkerStage.Setup].Count == 1
            && sample.InlineChildren[(int)SliceWorkerStage.BubbleBegin].Count == 1
            && sample.HelperChildren[(int)SliceWorkerStage.BubbleStep].Count == 2
            && sample.HelperChildren[(int)SliceWorkerStage.BepuWorker].Count == 1,
            "Root stages and same-thread/remote nested child segments must remain distinct.");
        Require(sample.HelperChildren.Sum(x => x.WallTicks) <= sample.Total.WallTicks,
            "A single helper's nested exclusive scopes cannot exceed their enclosing root interval.");
        string summary = sample.Format();
        Require(summary.Contains("dispatch-to-Run") && summary.Contains("queue overlap")
            && summary.Contains("inline child detail (included in root)")
            && summary.Contains("uncovered helper code") && !summary.Contains("global GC"),
            "Worker output must distinguish queue/work/wait, nesting and coverage without summing global counters.");

        // Reset while a child is inside a nested scope. The old sample keeps the active
        // gate; its finalizers must restore TLS and must not publish into a new epoch.
        SliceWorkerDiagnostics.Dispatch(50);
        var oldRun = SliceWorkerDiagnostics.BeginRun();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var child = Task.Run(() =>
        {
            var outer = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BubbleStep);
            var inner = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.BepuWorker);
            entered.Set(); release.Wait();
            SliceWorkerDiagnostics.EndChild(inner); SliceWorkerDiagnostics.EndChild(outer);
            var inactive = SliceWorkerDiagnostics.BeginChild(SliceWorkerStage.Cluster);
            Require(inactive.Sample == null, "Cancelled parent TLS must not adopt a later sample.");
            SliceWorkerDiagnostics.EndChild(inactive);
        });
        entered.Wait(); SliceWorkerDiagnostics.Reset(); SliceWorkerDiagnostics.Dispatch(51);
        release.Set(); child.GetAwaiter().GetResult(); SliceWorkerDiagnostics.EndRun(oldRun, true);
        Require(SliceWorkerDiagnostics.Joined(0, 0) == null, "Reset samples must not publish.");
        SliceWorkerDiagnostics.Dispatch(52);
        var freshRun = SliceWorkerDiagnostics.BeginRun();
        Require(freshRun.Sample?.Frame == 52, "Old in-flight callbacks must not steal or corrupt a new window.");
        SliceWorkerDiagnostics.EndRun(freshRun, false);
        Require(SliceWorkerDiagnostics.Joined(0, 0)?.Frame == 52, "Fresh generation must publish normally.");

        SliceWorkerDiagnostics.Dispatch(60); SliceWorkerDiagnostics.Reset(); SliceWorkerDiagnostics.Dispatch(61);
        var queuedOld = SliceWorkerDiagnostics.BeginRun();
        Require(queuedOld.Sample?.Frame == 60, "A late queued Run must consume its cancelled token, not a new dispatch.");
        SliceWorkerDiagnostics.EndRun(queuedOld, false); Require(SliceWorkerDiagnostics.Joined(0, 0) == null,
            "A cancelled queued job must be released only by native join.");

        // Dispatch in the last monitored interval joins after EnterFrame has closed it.
        var ring = new SliceDiagnosticRing();
        var stamp = SliceCounterSnapshot.Capture(); ring.EnterFrame(stamp);
        ring.Begin("worker", "fixture", 1, 1, 0, stamp);
        for (int i = 0; i < SliceDiagnosticRing.FramesPerOperation; i++) ring.EnterFrame(stamp);
        var final = new SliceWorkerSample(0, 4, 0) { Completed = 1 };
        ring.RecordWorker(final);
        string finalSummary = ring.TakeCompleted()!;
        Require(finalSummary.Contains("next frame +3 interval:") && !ring.HasActive
            && finalSummary.Contains("physics worker dispatched in this interval: dispatch frame 4"),
            "Last dispatched worker must attach to the already closed interval before delayed formatting.");
        Require(sample.Stages.Length == (int)SliceWorkerStage.Count && sample.InlineChildren.Length == sample.Stages.Length
            && sample.HelperChildren.Length == sample.Stages.Length, "Each dispatch must retain only fixed-size metric arrays.");
        DebugTimings.Enabled = false;
        SliceWorkerDiagnostics.Reset();
        Console.WriteLine("PASS: worker current-thread metrics, bounded arrays, concurrent aggregation, nested scopes, idle allocations, reset/in-flight generations and final-frame joins");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
