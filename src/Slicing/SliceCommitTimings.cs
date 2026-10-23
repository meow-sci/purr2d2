using System.Diagnostics;
using System.Globalization;

namespace Purr2D2.Slicing;

internal enum SliceCommitPhase { Validation, Isolation, Drain, Registration, Motion, SourceRemoval }

/// <summary>Measures the indivisible native handoff. This diagnoses real host
/// costs without adding a profiler, a worker join or a log per fragment/frame.</summary>
internal sealed class SliceCommitTimings
{
    private readonly double[] _milliseconds = new double[6];
    private SliceCommitPhase _phase;
    private long _started = Stopwatch.GetTimestamp();
    internal void Next(SliceCommitPhase phase)
    {
        Stop(); _phase = phase; _started = Stopwatch.GetTimestamp();
    }
    internal void Stop()
    {
        if (_started == 0) return;
        _milliseconds[(int)_phase] += Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        _started = 0;
    }
    public override string ToString() => string.Join("; ", Enum.GetValues<SliceCommitPhase>()
        .Select(p => p + " " + _milliseconds[(int)p].ToString("F2", CultureInfo.InvariantCulture) + " ms"));
}
