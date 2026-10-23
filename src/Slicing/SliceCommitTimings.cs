using System.Globalization;

namespace Purr2D2.Slicing;

internal enum SliceCommitPhase { Validation, Isolation, Drain, Registration, Motion, SourceRemoval }

/// <summary>Measures one native isolation or swap handoff. This diagnoses real host
/// costs without adding a profiler, a worker join or a log per fragment/frame.</summary>
internal sealed class SliceCommitTimings
{
    private readonly double[] _milliseconds = new double[6];
    private readonly long _operation;
    private SliceCommitPhase _phase;
    private SliceCounterSnapshot _started = SliceCounterSnapshot.Capture();
    internal SliceCommitTimings() : this(SliceFrameDiagnostics.CurrentOperation) { }
    private SliceCommitTimings(long operation) => _operation = operation;
    internal void Next(SliceCommitPhase phase)
    {
        Stop(); _phase = phase; _started = SliceCounterSnapshot.Capture();
    }
    internal void Stop()
    {
        if (_started.Timestamp == 0) return;
        var delta = SliceCounterDelta.Between(_started, SliceCounterSnapshot.Capture());
        _milliseconds[(int)_phase] += delta.Milliseconds;
        _started = default;
        SliceFrameDiagnostics.RecordPhase(_operation, _phase, delta);
    }
    public override string ToString() => string.Join("; ", Enum.GetValues<SliceCommitPhase>()
        .Select(p => p + " " + _milliseconds[(int)p].ToString("F2", CultureInfo.InvariantCulture) + " ms"));

    internal static void Warmup()
    {
        var timing = new SliceCommitTimings(0);
        timing.Next(SliceCommitPhase.Isolation); timing.Stop(); timing.Stop();
        _ = timing.ToString();
    }
}
