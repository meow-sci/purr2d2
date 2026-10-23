using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

namespace Purr2D2.Cutting;

internal readonly record struct SliceAttempt(bool Success, string Message);

/// <summary>Copy on the settled game thread, compute once on a worker, stage native assets,
/// then commit only after the runtime confirms that the complete swing was presented.</summary>
internal sealed class ArbitrarySlicer : IDisposable
{
    internal Action<Part, double3, double3, double>? CutSparks;
    private SliceSourceCapture? _capture;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _work, _retiredWork;
    private RuntimeFragmentAssets.UploadJob? _upload, _retiredUpload;
    private LoadedSlice? _loaded;
    private long _workerStarted;
    private double _workerMs, _maxMainMs, _commitMs;
    private string _slowestStep = "none", _commitPhases = "";
    private double _slowestStepMs;
    internal SlicePlan? Plan => _work?.Plan;
    internal bool Ready => _upload is { Done: true, Error: null };
    internal string? Error { get; private set; }
    internal bool Busy => _retiredWork is { Finished: false } || _retiredUpload is { Done: false };
    internal string Diagnostics => $"Background preparation: {_workerMs:F1} ms; largest preparation update: {_maxMainMs:F2} ms"
        + $"; slowest preparation unit: {_slowestStep} {_slowestStepMs:F2} ms; native commit: {_commitMs:F2} ms"
        + (_commitPhases.Length == 0 ? "" : "; " + _commitPhases);
    internal string Phase => _capture is { Done: false } ? "Reading nearby part..."
        : _upload != null ? "Preparing the pieces..." : "Preparing the cut...";

    internal void Begin(Part part)
    {
        if (Busy || _capture != null) throw new InvalidOperationException("The previous slice is finishing cleanup; try again shortly.");
        Error = null; _workerMs = _maxMainMs = _commitMs = 0;
        _slowestStep = "none"; _slowestStepMs = 0; _commitPhases = "";
        _capture = new(part);
    }
    internal void Advance()
    {
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
        long started = Stopwatch.GetTimestamp();
        var timings = new SliceCommitTimings();
        try
        {
            if (!_capture.IsCurrent(part)) return new(false, "The source geometry changed during preparation. Try again.");
            var plane = _loaded.Data.Plane;
            bool replaced = NativeSlicePhysics.TryReplace(part, _upload!.Fragments,
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
            return new(true, reason + " Cut faces are cooling.");
        }
        catch (Exception error) { return new(false, "Slice commit failed: " + error.Message); }
        finally
        {
            _commitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.Stop(); _commitPhases = timings.ToString();
            Console.WriteLine("[purr2d2] " + Diagnostics);
        }
    }
    internal void Cancel()
    {
        _capture?.Dispose(); _capture = null;
        if (_work != null) { _work.Cancel(); _retiredWork = _work; _work = null; }
        // Finish already-started asset registration without spawning anything. This avoids
        // corrupt partial IDs and lets a later cut reuse the prepared assets after cancellation.
        if (_upload != null) { _upload.Cancel(); _retiredUpload = _upload; _upload = null; }
        _loaded = null;
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
