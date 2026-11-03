using Purr2D2.Core;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
SliceDetectionDiagnostics.Reset(); DebugTimings.Enabled = false;
SliceDetectionDiagnostics.Record(SliceDetectorKind.AllHitRays, SliceDetectionDiagnostics.Start(), 2, true, 4, 1, true, 3);
var snapshot = SliceDetectionDiagnostics.Snapshot(SliceDetectorKind.AllHitRays);
Check(snapshot.Calls == 1 && snapshot.Confirmed == 2 && snapshot.Incomplete == 1 && snapshot.Unprocessed == 4
    && snapshot.Overflow == 1 && snapshot.CoverageUnavailable == 1 && snapshot.Unresolved == 3,
    "counters remain enabled without detailed timings");
Check(SliceDetectionDiagnostics.EventCount == 0, "disabled timing retains no detailed events");
DebugTimings.Enabled = true;
for (int i = 0; i < 300; i++)
    SliceDetectionDiagnostics.Record(SliceDetectorKind.CachedRibbon, SliceDetectionDiagnostics.Start(), 1, false);
Check(SliceDetectionDiagnostics.EventCount == 256 && SliceDetectionDiagnostics.Snapshot(SliceDetectorKind.CachedRibbon).Calls == 300,
    "timed events overwrite a bounded ring without losing cumulative counters");
Check(SliceDetectionDiagnostics.Describe(SliceDetectorKind.CachedRibbon).Contains("n=256"), "summary reports its bounded sample count");
SliceDetectionDiagnostics.Reset(); DebugTimings.Enabled = false;
Check(SliceDetectionDiagnostics.EventCount == 0 && SliceDetectionDiagnostics.Snapshot(SliceDetectorKind.AllHitRays).Calls == 0,
    "reset clears events and counters");
SliceJobDiagnostics.Reset();
SliceJobDiagnostics.Count(SliceJobOutcome.Queued);
SliceJobDiagnostics.Finish(null, SliceJobOutcome.Toppled, 1);
Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.Queued) == 1 && SliceJobDiagnostics.CountFor(SliceJobOutcome.Toppled) == 1
    && SliceJobDiagnostics.CountFor(SliceJobOutcome.MeshCut) == 0 && SliceJobDiagnostics.SampleCount == 0,
    "uninstrumented outcomes distinguish toppling from a completed mesh cut");
DebugTimings.Enabled = true;
for (int i = 0; i < 300; i++)
{
    var trace = SliceJobDiagnostics.Start("vessel", "cached-ribbon", "blade-strict", 1)!;
    trace.MarkCapture(); long start = trace.Start(); trace.Add(SliceJobStage.Capture, start);
    SliceJobDiagnostics.Finish(trace, SliceJobOutcome.MeshCut, 1.1);
}
var latest = SliceJobDiagnostics.Latest;
Check(SliceJobDiagnostics.SampleCount == 256 && SliceJobDiagnostics.CountFor(SliceJobOutcome.MeshCut) == 300
    && latest is { Outcome: SliceJobOutcome.MeshCut } && Math.Abs(latest.Value.PlayerLatencyMs - 100) < .001
    && latest.Value.CaptureMs >= 0 && latest.Value.QueueMs >= 0,
    "job traces keep bounded stage samples, cumulative outcomes and separate player-clock latency");
var serial = SliceJobDiagnostics.Start("clutter", "legacy-rays", "legacy-contact", 2, "serial")!;
serial.MarkCapture(); SliceJobDiagnostics.Finish(serial, SliceJobOutcome.MeshCut, 2.2, "serial");
string summary = SliceJobDiagnostics.Describe();
Check(SliceJobDiagnostics.CountForMode(SliceJobOutcome.MeshCut, "serial") == 1
    && SliceJobDiagnostics.CountForMode(SliceJobOutcome.MeshCut, "queued") == 300
    && summary.Contains("serial detection to mesh commit") && summary.Contains("queued detection to mesh commit")
    && summary.Contains("serial mesh-cut stage medians") && summary.Contains("queued mesh-cut stage medians"),
    "serial and queued outcomes, latency and stage summaries stay separate");
SliceJobDiagnostics.Reset(); DebugTimings.Enabled = false;
Console.WriteLine("Slice detection diagnostics checks passed.");
