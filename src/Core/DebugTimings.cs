namespace Purr2D2.Core;

/// <summary>Opt-in telemetry gate shared by the settled thread and diagnostic workers.</summary>
internal static class DebugTimings
{
    internal static volatile bool Enabled = false;
}

internal enum SliceDetectorKind { LegacyRibbon, CachedRibbon, ManualDrain, LegacyRays, AllHitRays, CapsuleSweep }
internal readonly record struct SliceDetectionSnapshot(long Calls, long Confirmed, long Incomplete,
    long Unprocessed, long Overflow, long CoverageUnavailable, long Unresolved);

/// <summary>Settled-thread-only bounded method counters. Detailed events are
/// recorded only when timing is enabled; no live scene object is retained.</summary>
internal static class SliceDetectionDiagnostics
{
    internal const int Capacity = 256;
    private struct Totals
    {
        internal long Calls, Confirmed, Incomplete, Unprocessed, Overflow, CoverageUnavailable, Unresolved;
    }
    private readonly record struct Event(SliceDetectorKind Kind, long Ticks, int Confirmed, bool Incomplete);
    private static readonly Totals[] Counts = new Totals[6];
    private static readonly Event[] Events = new Event[Capacity];
    private static int _next, _count;
    internal static int EventCount => _count;
    internal static SliceDetectionSnapshot Snapshot(SliceDetectorKind kind)
    {
        var t = Counts[(int)kind];
        return new(t.Calls, t.Confirmed, t.Incomplete, t.Unprocessed, t.Overflow, t.CoverageUnavailable, t.Unresolved);
    }
    internal static long Start() => DebugTimings.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    internal static void Record(SliceDetectorKind kind, long started, int confirmed, bool incomplete,
        int unprocessed = 0, int overflow = 0, bool coverageUnavailable = false, int unresolved = 0)
    {
        ref var total = ref Counts[(int)kind];
        total.Calls++; total.Confirmed += Math.Max(0, confirmed);
        if (incomplete) total.Incomplete++;
        total.Unprocessed += Math.Max(0, unprocessed); total.Overflow += Math.Max(0, overflow);
        if (coverageUnavailable) total.CoverageUnavailable++;
        total.Unresolved += Math.Max(0, unresolved);
        if (started == 0 || !DebugTimings.Enabled) return;
        Events[_next] = new(kind, System.Diagnostics.Stopwatch.GetTimestamp() - started, confirmed, incomplete);
        _next = (_next + 1) % Capacity; _count = Math.Min(Capacity, _count + 1);
    }
    internal static string Describe(SliceDetectorKind kind)
    {
        var total = Counts[(int)kind];
        string distribution = "timing unavailable";
        if (DebugTimings.Enabled)
        {
            var times = new List<double>(_count);
            for (int i = 0; i < _count; i++)
                if (Events[i].Kind == kind) times.Add(Events[i].Ticks * (1000d / System.Diagnostics.Stopwatch.Frequency));
            times.Sort();
            if (times.Count > 0) distribution = $"n={times.Count}, median {Percentile(times, .5):F3} / p95 {Percentile(times, .95):F3} / p99 {Percentile(times, .99):F3} ms";
        }
        return $"{kind}: {total.Calls} queries, {total.Confirmed} confirmed, {total.Incomplete} pending or incomplete, "
            + $"{total.Unprocessed} paths unprocessed, {total.Overflow} overflow, {total.CoverageUnavailable} coverage unavailable, "
            + $"{total.Unresolved} unresolved; {distribution}";
    }
    private static double Percentile(List<double> values, double p) => values[Math.Clamp((int)Math.Ceiling(values.Count * p) - 1, 0, values.Count - 1)];
    internal static void Reset() { Array.Clear(Counts); Array.Clear(Events); _next = _count = 0; }
}

internal enum SliceJobOutcome { Queued, Deduplicated, QueueFull, Rejected, Stale, MeshCut, AuthoredFallback, Toppled, Failed, Cancelled, SerialStarted }
internal enum SliceJobStage { Capture, Geometry, Loader, Upload, Commit, Count }
internal readonly record struct SliceJobSample(long Id, string Domain, string Detector, string Plane, string Mode,
    SliceJobOutcome Outcome, double PlayerLatencyMs, double WallMs, double QueueMs,
    double CaptureMs, double GeometryMs, double LoaderMs, double UploadMs, double CommitMs, double LongestStepMs);

/// <summary>Per-job tracing is opt-in; aggregate outcome counts remain active.
/// Worker threads only update their own trace's primitive timing counters.</summary>
internal sealed class SliceJobTrace
{
    private readonly long[] _ticks = new long[(int)SliceJobStage.Count];
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _firstCapture, _longest;
    internal long Id { get; }
    internal string Domain { get; }
    internal string Detector { get; }
    internal string Plane { get; }
    internal string Mode { get; }
    internal double DetectionTime { get; }
    internal SliceJobTrace(long id, string domain, string detector, string plane, double detectionTime, string mode)
    { Id = id; Domain = domain; Detector = detector; Plane = plane; DetectionTime = detectionTime; Mode = mode; }
    internal long Start() => DebugTimings.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    internal void MarkCapture() { if (DebugTimings.Enabled && _firstCapture == 0) _firstCapture = System.Diagnostics.Stopwatch.GetTimestamp(); }
    internal void Add(SliceJobStage stage, long start)
    {
        if (start == 0) return;
        long elapsed = Math.Max(0, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _ticks[(int)stage], elapsed);
        if (stage is SliceJobStage.Geometry or SliceJobStage.Loader) return;
        long old;
        while (elapsed > (old = Interlocked.Read(ref _longest)) && Interlocked.CompareExchange(ref _longest, elapsed, old) != old) { }
    }
    internal SliceJobSample Finish(SliceJobOutcome outcome, double now)
    {
        double ms = 1000d / System.Diagnostics.Stopwatch.Frequency;
        double Stage(SliceJobStage stage) => Interlocked.Read(ref _ticks[(int)stage]) * ms;
        return new(Id, Domain, Detector, Plane, Mode, outcome,
            double.IsFinite(now) && double.IsFinite(DetectionTime) && now >= DetectionTime ? (now - DetectionTime) * 1000 : double.NaN,
            (System.Diagnostics.Stopwatch.GetTimestamp() - _started) * ms,
            _firstCapture == 0 ? double.NaN : (_firstCapture - _started) * ms,
            Stage(SliceJobStage.Capture), Stage(SliceJobStage.Geometry), Stage(SliceJobStage.Loader),
            Stage(SliceJobStage.Upload), Stage(SliceJobStage.Commit), Interlocked.Read(ref _longest) * ms);
    }
}

internal static class SliceJobDiagnostics
{
    private static readonly long[] Counts = new long[11];
    private static readonly long[] SerialCounts = new long[11], QueuedCounts = new long[11];
    private static readonly SliceJobSample[] Samples = new SliceJobSample[256];
    private static int _next, _count;
    private static long _nextId;
    internal static int SampleCount => _count;
    internal static long CountFor(SliceJobOutcome outcome) => Counts[(int)outcome];
    internal static long CountForMode(SliceJobOutcome outcome, string mode)
        => (mode == "serial" ? SerialCounts : QueuedCounts)[(int)outcome];
    internal static SliceJobSample? Latest => _count == 0 ? null : Samples[(_next + Samples.Length - 1) % Samples.Length];
    internal static void Count(SliceJobOutcome outcome, string mode = "queued")
    {
        Counts[(int)outcome]++;
        (mode == "serial" ? SerialCounts : QueuedCounts)[(int)outcome]++;
    }
    internal static SliceJobTrace? Start(string domain, string detector, string plane, double detectionTime, string mode = "queued")
        => DebugTimings.Enabled ? new(++_nextId, domain, detector, plane, detectionTime, mode) : null;
    internal static void Finish(SliceJobTrace? trace, SliceJobOutcome outcome, double now, string mode = "queued")
    {
        Count(outcome, trace?.Mode ?? mode);
        if (trace == null || !DebugTimings.Enabled) return;
        Samples[_next] = trace.Finish(outcome, now); _next = (_next + 1) % Samples.Length; _count = Math.Min(Samples.Length, _count + 1);
    }
    internal static string Describe()
    {
        string text = $"Jobs: {Counts[10]} serial started, {Counts[0]} queued, {Counts[1]} deduplicated, {Counts[2]} queue-full, {Counts[3]} rejected, {Counts[4]} stale, "
            + $"{Counts[5]} mesh cuts, {Counts[6]} authored-boundary fallback, {Counts[7]} toppled, {Counts[8]} failed, {Counts[9]} cancelled.";
        text += $"\nSerial outcomes: {SerialCounts[5]} mesh, {SerialCounts[6]} authored, {SerialCounts[7]} toppled, {SerialCounts[8]} failed, {SerialCounts[4]} stale, {SerialCounts[3]} rejected, {SerialCounts[9]} cancelled; "
            + $"queued outcomes: {QueuedCounts[5]} mesh, {QueuedCounts[6]} authored, {QueuedCounts[7]} toppled, {QueuedCounts[8]} failed, {QueuedCounts[4]} stale, {QueuedCounts[3]} rejected, {QueuedCounts[9]} cancelled.";
        if (!DebugTimings.Enabled || _count == 0) return text;
        foreach (string mode in new[] { "serial", "queued" })
        {
            var success = new List<double>(_count);
            var stages = new List<SliceJobSample>(_count);
            for (int i = 0; i < _count; i++)
                if (Samples[i].Mode == mode && Samples[i].Outcome == SliceJobOutcome.MeshCut)
                {
                    stages.Add(Samples[i]);
                    if (double.IsFinite(Samples[i].PlayerLatencyMs)) success.Add(Samples[i].PlayerLatencyMs);
                }
            success.Sort();
            if (success.Count > 0)
            {
                double P(double p) => success[Math.Clamp((int)Math.Ceiling(success.Count * p) - 1, 0, success.Count - 1)];
                text += $"\n{mode} detection to mesh commit (player clock): n={success.Count}, median {P(.5):F1} / p95 {P(.95):F1} / p99 {P(.99):F1} ms.";
            }
            if (stages.Count == 0) continue;
            double Median(Func<SliceJobSample, double> select)
            {
                var values = stages.Select(select).Where(double.IsFinite).Order().ToArray();
                return values.Length == 0 ? double.NaN : values[(values.Length - 1) / 2];
            }
            text += $"\n{mode} mesh-cut stage medians (n={stages.Count}, ms): queue {Median(s => s.QueueMs):F1}, "
                + $"capture {Median(s => s.CaptureMs):F1}, geometry {Median(s => s.GeometryMs):F1}, "
                + $"loader {Median(s => s.LoaderMs):F1}, upload {Median(s => s.UploadMs):F1}, commit {Median(s => s.CommitMs):F1}.";
        }
        var last = Samples[(_next + Samples.Length - 1) % Samples.Length];
        return text + $"\nLast job #{last.Id} {last.Mode}/{last.Domain}/{last.Detector}/{last.Plane}: {last.Outcome}; "
            + $"wall {last.WallMs:F1} ms, queue {last.QueueMs:F1}, capture {last.CaptureMs:F1}, geometry {last.GeometryMs:F1}, "
            + $"loader {last.LoaderMs:F1}, upload {last.UploadMs:F1}, commit {last.CommitMs:F1}, longest step {last.LongestStepMs:F1} ms.";
    }
    internal static void Reset() { Array.Clear(Counts); Array.Clear(SerialCounts); Array.Clear(QueuedCounts); Array.Clear(Samples); _next = _count = 0; }
}
