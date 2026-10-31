using System.Globalization;
using Purr2D2.Core;

namespace Purr2D2.Slicing;

internal enum SliceCommitPhase { Validation, Isolation, Drain, Registration, Motion, SourceRemoval }

/// <summary>Measures one native isolation or swap handoff. This diagnoses real host
/// costs without adding a profiler, a worker join or a log per fragment/frame.</summary>
internal sealed class SliceCommitTimings
{
    private static readonly SliceCommitTimings Disabled = new(0, false);
    private readonly double[] _milliseconds;
    private readonly bool _enabled;
    private readonly long _operation;
    private SliceCommitPhase _phase;
    private SliceCounterSnapshot _started;
    internal SliceCommitTimings() : this(SliceFrameDiagnostics.CurrentOperation, DebugTimings.Enabled) { }
    private SliceCommitTimings(long operation, bool enabled)
    {
        _operation = operation; _enabled = enabled;
        _milliseconds = enabled ? new double[6] : Array.Empty<double>();
        _started = enabled ? SliceCounterSnapshot.Capture() : default;
    }
    internal static SliceCommitTimings Start() => DebugTimings.Enabled ? new() : Disabled;
    internal void Next(SliceCommitPhase phase)
    {
        if (!_enabled || !DebugTimings.Enabled) return;
        Stop(); _phase = phase; _started = SliceCounterSnapshot.Capture();
    }
    internal void Stop()
    {
        if (!_enabled || !DebugTimings.Enabled || _started.Timestamp == 0) return;
        var delta = SliceCounterDelta.Between(_started, SliceCounterSnapshot.Capture());
        _milliseconds[(int)_phase] += delta.Milliseconds;
        _started = default;
        SliceFrameDiagnostics.RecordPhase(_operation, _phase, delta);
    }
    public override string ToString() => !_enabled || !DebugTimings.Enabled ? "" : string.Join("; ", Enum.GetValues<SliceCommitPhase>()
        .Select(p => p + " " + _milliseconds[(int)p].ToString("F2", CultureInfo.InvariantCulture) + " ms"));

    internal static void Warmup()
    {
        var timing = new SliceCommitTimings(0, DebugTimings.Enabled);
        timing.Next(SliceCommitPhase.Isolation); timing.Stop(); timing.Stop();
        _ = timing.ToString();
    }
}
