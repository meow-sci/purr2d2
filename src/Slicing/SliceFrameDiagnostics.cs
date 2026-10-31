using System.Text;
using Purr2D2.Core;

namespace Purr2D2.Slicing;

internal enum SliceFrameSegment { VehicleWait, DirtyDerived, ResourceFlush, VehiclePreparationAndDispatch }

internal readonly record struct SliceFrameSample(SliceCounterDelta Interval, SliceCounterDelta HookTail,
    bool HasHookTail, SliceCounterDelta VehicleWait, SliceCounterDelta DirtyDerived,
    SliceCounterDelta ResourceFlush, SliceCounterDelta VehiclePreparationAndDispatch,
    SliceWorkerSample? Worker = null);

/// <summary>A separately bounded ring of primitive snapshots. Never retains a vehicle, part,
/// tree, profiler frame or uploaded asset. Main-thread use only.</summary>
internal sealed class SliceDiagnosticRing
{
    internal const int Capacity = 8;
    internal const int FramesPerOperation = 4;
    private sealed class Window
    {
        internal long Id, FirstFrame;
        internal string? Kind, Template;
        internal int OwnerParts, SourceParts, Boundaries, Count;
        internal bool Active, PartialFirst;
        internal readonly SliceFrameSample[] Frames = new SliceFrameSample[FramesPerOperation];
        internal readonly SliceCounterDelta[] Phases = new SliceCounterDelta[6];
        internal readonly int[] PhaseCounts = new int[6];
    }
    private readonly Window[] _windows = Enumerable.Range(0, Capacity).Select(_ => new Window()).ToArray();
    private readonly SliceCounterDelta[] _segments = new SliceCounterDelta[4];
    private readonly int[] _segmentCounts = new int[4];
    private SliceCounterSnapshot _entry, _hookReturn;
    private long _frame, _nextId;
    private int _activeCount, _pendingCount;
    private bool _hasEntry, _hasHookReturn, _partialFrame;
    internal long CurrentOperation { get; private set; }
    internal int Dropped { get; private set; }
    internal bool HasActive => _activeCount != 0;
    internal int PendingCount => _pendingCount;
    internal long CurrentFrame => _frame;

    internal void EnterFrame(in SliceCounterSnapshot snapshot, bool partial = false)
    {
        if (_hasEntry)
        {
            var sample = new SliceFrameSample(SliceCounterDelta.Between(_entry, snapshot),
                _hasHookReturn ? SliceCounterDelta.Between(_hookReturn, snapshot) : default, _hasHookReturn,
                _segments[0], _segments[1], _segments[2], _segments[3]);
            foreach (var window in _windows)
            {
                if (!window.Active || _frame < window.FirstFrame) continue;
                window.Frames[window.Count++] = sample;
                if (window.Count == FramesPerOperation) { window.Active = false; _activeCount--; }
            }
        }
        _entry = snapshot; _hasEntry = true; _hasHookReturn = false; _partialFrame = partial;
        _frame++; CurrentOperation = 0;
        Array.Clear(_segments); Array.Clear(_segmentCounts);
    }

    internal long Begin(string kind, string template, int ownerParts, int sourceParts, int boundaries,
        in SliceCounterSnapshot fallbackStart)
    {
        if (!_hasEntry) EnterFrame(fallbackStart, partial: true);
        CurrentOperation = 0;
        foreach (var window in _windows)
        {
            if (window.Kind != null) continue;
            window.Id = ++_nextId; window.FirstFrame = _frame;
            window.Kind = kind; window.Template = template;
            window.OwnerParts = ownerParts; window.SourceParts = sourceParts; window.Boundaries = boundaries;
            window.Active = true; window.PartialFirst = _partialFrame; window.Count = 0;
            _activeCount++; _pendingCount++;
            Array.Clear(window.Frames); Array.Clear(window.Phases); Array.Clear(window.PhaseCounts);
            return CurrentOperation = window.Id;
        }
        Dropped++;
        return 0;
    }

    internal void SetSourceDetails(int sourceParts, int boundaries)
    {
        foreach (var window in _windows)
            if (window.Id == CurrentOperation && window.Active)
            { window.SourceParts = sourceParts; window.Boundaries = boundaries; return; }
    }

    internal void HookReturned(in SliceCounterSnapshot snapshot)
    { _hookReturn = snapshot; _hasHookReturn = true; CurrentOperation = 0; }

    internal void RecordSegment(SliceFrameSegment segment, in SliceCounterDelta value)
    {
        int index = (int)segment;
        _segments[index] = _segmentCounts[index]++ == 0 ? value : _segments[index].Plus(value);
    }

    internal void RecordPhase(long id, SliceCommitPhase phase, in SliceCounterDelta value)
    {
        if (id == 0) return;
        foreach (var window in _windows)
        {
            if (window.Id != id || !window.Active) continue;
            int index = (int)phase;
            window.Phases[index] = window.PhaseCounts[index]++ == 0 ? value : window.Phases[index].Plus(value);
            return;
        }
    }

    internal void RecordWorker(SliceWorkerSample sample)
    {
        // Dispatch belongs to its originating interval. Its existing native wait happens
        // AFTER next EnterFrame has closed that interval, including the final interval.
        foreach (var window in _windows)
        {
            if (window.Kind == null) continue;
            long index = sample.Frame - window.FirstFrame;
            if (index >= 0 && index < window.Count)
                window.Frames[index] = window.Frames[index] with { Worker = sample };
        }
    }

    internal string? TakeCompleted()
    {
        // Do not let isolation's delayed formatting contaminate the overlapping swap window.
        if (HasActive) return null;
        foreach (var window in _windows)
        {
            if (window.Kind == null) continue;
            var text = new StringBuilder(4096);
            text.Append("[purr2d2] Slice frame diagnostics: ").Append(window.Kind)
                .Append("; source ").Append(window.Template).Append("; owner parts ").Append(window.OwnerParts)
                .Append("; source parts including internal ").Append(window.SourceParts)
                .Append("; isolation boundaries ").Append(window.Boundaries)
                .Append("; frame intervals are PrepareFrame entry to next entry (include pacing); hook tail is settled-hook return to next entry");
            for (int phase = 0; phase < window.Phases.Length; phase++)
                if (window.PhaseCounts[phase] != 0)
                    text.Append("\n  native ").Append((SliceCommitPhase)phase).Append(": ").Append(window.Phases[phase].Format());
            for (int index = 0; index < window.Count; index++)
            {
                var sample = window.Frames[index];
                text.Append("\n  ").Append(index == 0 ? "operation frame" : "next frame +" + index)
                    .Append(index == 0 && window.PartialFirst ? " (partial operation-start interval): " : " interval: ")
                    .Append(sample.Interval.Format());
                text.Append("\n    hook tail: ").Append(sample.HasHookTail ? sample.HookTail.Format() : "unavailable")
                    .Append("; vehicle wait: ").Append(FormatSegment(sample.VehicleWait))
                    .Append("; dirty derived: ").Append(FormatSegment(sample.DirtyDerived))
                    .Append("; resource flush: ").Append(FormatSegment(sample.ResourceFlush))
                    .Append("; vehicle preparation + ground clutter + dispatch: ").Append(FormatSegment(sample.VehiclePreparationAndDispatch));
                text.Append("\n    physics worker dispatched in this interval: ")
                    .Append(sample.Worker?.Format() ?? (SliceWorkerDiagnostics.Supported
                        ? "unavailable (no joined instrumented dispatch)" : "unavailable (worker hooks unsupported)"));
            }
            if (Dropped != 0) text.Append("\n  diagnostic ring full: ").Append(Dropped).Append(" operations dropped");
            window.Kind = window.Template = null;
            Array.Clear(window.Frames);
            _pendingCount--;
            return text.ToString();
        }
        return null;

        static string FormatSegment(in SliceCounterDelta value) => value == default ? "unavailable" : value.FormatBrief();
    }

    internal void ForgetFrame() { _hasEntry = _hasHookReturn = false; CurrentOperation = 0; }
    internal void Reset()
    {
        foreach (var window in _windows)
        { window.Kind = window.Template = null; window.Active = false; Array.Clear(window.Frames); }
        _activeCount = _pendingCount = Dropped = 0; ForgetFrame();
    }
}

/// <summary>Optional telemetry must never interfere with native work, exceptions or synchronization.</summary>
internal static class SliceFrameDiagnostics
{
    private static readonly SliceDiagnosticRing Ring = new();
    private static bool _armed;
    internal static bool Sampling => DebugTimings.Enabled && (_armed || Ring.HasActive);
    internal static long CurrentOperation => Ring.CurrentOperation;

    internal static void Arm()
    {
        if (!DebugTimings.Enabled) return;
        if (!_armed && !Ring.HasActive) Ring.ForgetFrame();
        _armed = true;
    }
    internal static void Disarm() { _armed = false; if (!Ring.HasActive) Ring.ForgetFrame(); }
    internal static void Reset() { _armed = false; Ring.Reset(); SliceWorkerDiagnostics.Reset(); }
    internal static void BeginOperation(string kind, string sourceTemplateId, int ownerPartCount,
        int sourcePartCount = -1, int boundaryConnections = -1)
    {
        if (!DebugTimings.Enabled) return;
        try { Ring.Begin(kind, sourceTemplateId, ownerPartCount, sourcePartCount, boundaryConnections, SliceCounterSnapshot.Capture()); }
        catch { Reset(); }
    }
    internal static void SetSourceDetails(int sourcePartCount, int boundaryConnections)
    {
        if (!DebugTimings.Enabled) return;
        try { Ring.SetSourceDetails(sourcePartCount, boundaryConnections); }
        catch { Reset(); }
    }
    internal static void EnterFrame()
    {
        if (!Sampling) return;
        try { Ring.EnterFrame(SliceCounterSnapshot.Capture()); }
        catch { Reset(); }
    }
    internal static void HookReturned()
    {
        if (!Sampling) return;
        try { Ring.HookReturned(SliceCounterSnapshot.Capture()); }
        catch { Reset(); }
    }
    internal static void DispatchWorker()
    {
        if (DebugTimings.Enabled && Ring.HasActive && SliceWorkerDiagnostics.Supported) SliceWorkerDiagnostics.Dispatch(Ring.CurrentFrame);
    }
    internal static void WorkerJoined(long waitStart, long waitEnd)
    {
        // Do not gate on Sampling: EnterFrame may just have closed the fourth interval.
        if (!SliceWorkerDiagnostics.HasPending) return;
        try
        {
            var sample = SliceWorkerDiagnostics.Joined(waitStart, waitEnd);
            if (DebugTimings.Enabled && sample != null) Ring.RecordWorker(sample);
        }
        catch { Reset(); }
    }
    internal static SliceCounterSnapshot StartSegment() => Sampling ? SliceCounterSnapshot.Capture() : default;
    internal static void EndSegment(SliceFrameSegment segment, in SliceCounterSnapshot start)
    {
        if (!DebugTimings.Enabled || start.Timestamp == 0) return;
        try { Ring.RecordSegment(segment, SliceCounterDelta.Between(start, SliceCounterSnapshot.Capture())); }
        catch { Reset(); }
    }
    internal static void RecordPhase(long id, SliceCommitPhase phase, in SliceCounterDelta value)
    {
        if (!DebugTimings.Enabled) return;
        try { Ring.RecordPhase(id, phase, value); }
        catch { Reset(); }
    }
    internal static void PumpSummaries()
    {
        if (!DebugTimings.Enabled || Ring.PendingCount == 0 || Ring.HasActive) return;
        try { while (Ring.TakeCompleted() is { } summary) Console.WriteLine(summary); }
        catch { Reset(); }
    }
    internal static void Warmup()
    {
        // Local exercise only: warmup may run concurrently on a worker.
        var ring = new SliceDiagnosticRing();
        var stamp = SliceCounterSnapshot.Capture();
        ring.EnterFrame(stamp); long id = ring.Begin("warmup", "warmup", 1, 1, 0, stamp);
        ring.SetSourceDetails(1, 0); ring.HookReturned(stamp);
        var delta = SliceCounterDelta.Between(stamp, SliceCounterSnapshot.Capture());
        ring.RecordPhase(id, SliceCommitPhase.Validation, delta);
        for (int segment = 0; segment < 4; segment++) ring.RecordSegment((SliceFrameSegment)segment, delta);
        for (int frame = 0; frame < SliceDiagnosticRing.FramesPerOperation; frame++) ring.EnterFrame(stamp);
        _ = ring.TakeCompleted(); ring.Reset();
    }
}
