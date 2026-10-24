using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

namespace Purr2D2.Cutting;

internal readonly record struct SliceAttempt(bool Success, string Message);

/// <summary>Copy on the settled game thread, compute once on a worker, stage native assets,
/// then commit only after the runtime authorizes the accepted trigger.</summary>
internal sealed class ArbitrarySlicer : IDisposable
{
    internal Action<Part, double3, double3, double>? CutSparks;
    private SliceSourceCapture? _capture;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _work, _retiredWork;
    private RuntimeFragmentAssets.UploadJob? _upload, _retiredUpload;
    private LoadedSlice? _loaded;
    internal IsolatedSliceSource? Isolation { get; private set; }
    private long _workerStarted;
    private double _workerMs, _maxMainMs, _commitMs, _isolationMs;
    private string _isolationPhases = "";
    private long _handoff, _isolatedAtHandoff = -1;
    private string _slowestStep = "none", _commitPhases = "";
    private double _slowestStepMs;
    internal SlicePlan? Plan => _work?.Plan;
    internal bool Ready => _upload is { Done: true, Error: null } && SliceJitWarmup.Completed;
    internal string? Error { get; private set; }
    internal bool Busy => _retiredWork is { Finished: false } || _retiredUpload is { Done: false };
    internal string Diagnostics => $"Background preparation: {_workerMs:F1} ms; largest preparation update: {_maxMainMs:F2} ms"
        + $"; slowest preparation unit: {_slowestStep} {_slowestStepMs:F2} ms; native commit: {_commitMs:F2} ms"
        + $"; earlier-frame isolation: {_isolationMs:F2} ms"
        + (_isolationPhases.Length == 0 ? "" : "; isolation pass [" + _isolationPhases + "]")
        + (_commitPhases.Length == 0 ? "" : "; " + _commitPhases);
    internal string Phase => Isolation != null ? "Finishing the cut..."
        : _capture is { Done: false } ? "Reading nearby part..."
        : _upload != null ? "Preparing the pieces..." : "Preparing the cut...";

    internal void Begin(Part part)
    {
        if (Busy || _capture != null) throw new InvalidOperationException("The previous slice is finishing cleanup; try again shortly.");
        Error = null; _workerMs = _maxMainMs = _commitMs = _isolationMs = 0;
        _isolationPhases = ""; _isolatedAtHandoff = -1;
        _slowestStep = "none"; _slowestStepMs = 0; _commitPhases = "";
        SliceJitWarmup.Start(); // Fallback if startup was bypassed; never waits for compilation.
        _capture = new(part);
        SliceFrameDiagnostics.Arm();
    }
    internal void Advance()
    {
        // Called once by the active assisted/manual coordinator at the joined physics hook.
        // Player/simulation time can stay unchanged on pause; use hook progress.
        _handoff++;
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (_capture == null || Error != null || Ready) return;
            if (!_capture.Done) { _capture.Advance(); return; }
            if (_work == null)
            {
                var input = _capture.Input!;
                var context = RuntimeFragmentAssets.CaptureWorkerContext();
                _workerStarted = Stopwatch.GetTimestamp();
                _work = new((cancel, publish) => RuntimeFragmentAssets.LoadDetached(
                    SliceGeometryWork.Calculate(input, cancel, publish), context.Directory, context.Mod, cancel));
                return;
            }
            if (_loaded == null)
            {
                if (!_work.TryTake(out var result, out var error)) return;
                _workerMs = Stopwatch.GetElapsedTime(_workerStarted).TotalMilliseconds;
                if (error != null) { Error = error; return; }
                _loaded = result!; _upload = new(result!);
            }
            _upload!.Advance();
            _slowestStep = _upload.SlowestStep; _slowestStepMs = _upload.SlowestStepMs;
            if (_upload.Error != null) Error = _upload.Error;
        }
        catch (Exception error) { Error = error.Message; }
        finally { _maxMainMs = Math.Max(_maxMainMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
    internal SliceAttempt Commit(Part part, double now)
    {
        if (!Ready || _loaded == null || _capture == null) return new(false, "Slice preparation is not ready.");
        SliceFrameDiagnostics.BeginOperation("swap", part.StructuralPart.Template.Id,
            part.Tree?.Count ?? 0, Isolation?.Parts.Length ?? -1, boundaryConnections: 0);
        long started = Stopwatch.GetTimestamp();
        var timings = new SliceCommitTimings();
        try
        {
            if (Isolation == null) return new(false, "The source must be isolated on an earlier frame.");
            if (_handoff <= _isolatedAtHandoff) return new(false, "The fragment swap must wait for the next simulation handoff.");
            if (!_capture.IsCurrent(part, Isolation)) return new(false, "The isolated source geometry changed before the swap. Try again.");
            var plane = _loaded.Data.Plane;
            bool replaced = NativeSlicePhysics.TryReplace(Isolation, _upload!.Fragments,
                new(plane.Normal.X, plane.Normal.Y, plane.Normal.Z), out var created, out var reason, timings);
            timings.Stop();
            if (!replaced) return new(false, reason);
            for (int i = 0; i < created.Count; i++)
            {
                RuntimeFragmentAssets.MarkHot(_upload.Fragments[i].Asset, now);
                foreach (var spark in _loaded.Fragments[i].Data.Sparks)
                    try { CutSparks?.Invoke(created[i].Parts.Root, spark.Point, spark.Normal, now); }
                    catch (Exception effect) { Console.WriteLine("[purr2d2] Slice completed; spark effect: " + effect.Message); }
            }
            return new(true, reason + (_loaded.Data.AuthoredSubpartFallback
                ? " Used authored subpart boundaries because mesh cutting failed." : " Cut faces are cooling."));
        }
        catch (Exception error) { return new(false, "Slice commit failed: " + error.Message); }
        finally
        {
            _commitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.Stop(); _commitPhases = timings.ToString();
            Console.WriteLine("[purr2d2] " + Diagnostics);
        }
    }
    internal SliceAttempt Isolate(Part part)
    {
        if (!Ready || _loaded == null || _capture == null || Isolation != null)
            return new(false, "Slice preparation is not ready for native isolation.");
        SliceFrameDiagnostics.BeginOperation("isolation", part.StructuralPart.Template.Id, part.Tree?.Count ?? 0);
        var timings = new SliceCommitTimings();
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!_capture.IsCurrent(part)) return new(false, "The source geometry changed during preparation. Try again.");
            bool success = NativeSlicePhysics.TryIsolate(part, _upload!.Fragments, out var isolated, out string reason, timings);
            Isolation = isolated;
            if (isolated != null) _isolatedAtHandoff = _handoff;
            if (success && !_capture.IsCurrent(part, isolated))
                return new(false, "Native isolation changed the captured geometry; the separated source is retained.");
            return new(success, reason);
        }
        catch (Exception error) { return new(false, "Slice isolation failed; any completed native separations remain: " + error.Message); }
        finally
        {
            _isolationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.Stop(); _isolationPhases = timings.ToString();
        }
    }
    internal void Cancel()
    {
        SliceFrameDiagnostics.Disarm(); // Already captured frame windows still finish.
        _capture?.Dispose(); _capture = null;
        if (_work != null) { _work.Cancel(); _retiredWork = _work; _work = null; }
        // Finish already-started asset registration without spawning anything. This avoids
        // corrupt partial IDs and lets a later cut reuse the prepared assets after cancellation.
        if (_upload != null) { _upload.Cancel(); _retiredUpload = _upload; _upload = null; }
        _loaded = null;
        Isolation?.Dispose(); Isolation = null;
    }
    internal void PumpRetired()
    {
        if (_retiredWork is { Finished: true }) { _retiredWork.TryTake(out _, out _); _retiredWork = null; }
        if (_retiredUpload == null) return;
        _retiredUpload.Advance();
        if (_retiredUpload.Done) { _retiredUpload.Dispose(); _retiredUpload = null; }
    }
    public void Dispose()
    {
        Cancel(); // Never join a CPU task, including at shutdown.
        _retiredUpload?.Dispose(); _retiredUpload = null; // GPU teardown is allowed to wait at shutdown.
    }
}
