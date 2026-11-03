using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Cutting;

internal readonly record struct SliceAttempt(bool Success, string Message);

/// <summary>Copy on the settled game thread, compute once on a worker, stage native assets,
/// then commit only after the runtime authorizes the accepted trigger.</summary>
internal sealed class ArbitrarySlicer : IDisposable
{
    private readonly TaskScheduler? _scheduler;
    private readonly TaskScheduler? _loaderScheduler;
    private long _inputBytes, _outputBytes;
    private int _expectedFragments;
    internal ArbitrarySlicer(TaskScheduler? scheduler = null, TaskScheduler? loaderScheduler = null)
    { _scheduler = scheduler; _loaderScheduler = loaderScheduler; }
    internal bool NeedsCapture => _capture is { Done: false };
    internal bool EstimateDone => _capture?.EstimateDone ?? false;
    internal long EstimatedBytes => _capture?.EstimatedBytes ?? 0;
    internal long RetainedBytes => checked(Math.Max(_inputBytes, _capture?.RetainedBytes ?? 0) + Interlocked.Read(ref _outputBytes));
    internal int ExpectedFragments => Volatile.Read(ref _expectedFragments);
    internal bool OwnsUpload => _upload != null || _retiredUpload != null;
    internal bool CanAcquireUpload => _capture != null && _upload == null
        && (_scheduler == null ? _work is { Finished: true } : _loadWork is { Finished: true });
    internal bool HoldsLoadSlot => _loadWork != null || _retiredLoad != null || _loaded != null || OwnsUpload;
    internal SliceJobTrace? Trace;
    private bool _serialDiagnostics;
    internal void FinishSerialDiagnostic(SliceJobOutcome outcome, double now)
    {
        if (!_serialDiagnostics) return;
        SliceJobDiagnostics.Finish(Trace, outcome, now, "serial"); Trace = null; _serialDiagnostics = false;
    }
    internal bool UsedAuthoredFallback => _loaded?.Data.AuthoredSubpartFallback ?? false;
    internal void AdvanceEstimate()
    {
        Trace?.MarkCapture(); long started = Trace?.Start() ?? 0;
        try { _capture?.AdvanceEstimate(); }
        finally { Trace?.Add(SliceJobStage.Capture, started); }
    }
    internal Action<Part, double3, double3, double>? CutSparks;
    private SliceSourceCapture? _capture;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _work, _retiredWork;
    private BackgroundSliceWork<SlicePlan, CpuSlice>? _geometryWork, _retiredGeometry;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _loadWork, _retiredLoad;
    private CpuSlice? _calculated;
    private SlicePlan? _savedPlan;
    private bool _geometryStarted;
    private RuntimeFragmentAssets.UploadJob? _upload, _retiredUpload;
    private LoadedSlice? _loaded;
    internal IsolatedSliceSource? Isolation { get; private set; }
    private long _workerStarted;
    private double _workerMs, _maxMainMs, _commitMs, _isolationMs;
    private string _isolationPhases = "";
    private long _handoff, _isolatedAtHandoff = -1;
    private string _slowestStep = "none", _commitPhases = "";
    private double _slowestStepMs;
    internal SlicePlan? Plan => _work?.Plan ?? _geometryWork?.Plan ?? _savedPlan;
    internal IReadOnlyList<Part> CreatedParts { get; private set; } = Array.Empty<Part>();
    internal bool Ready => _upload is { Done: true, Error: null } && SliceJitWarmup.Completed;
    internal string? Error { get; private set; }
    internal bool Busy => _retiredWork is { Finished: false } || _retiredGeometry is { Finished: false }
        || _retiredLoad is { Finished: false } || _retiredUpload is { Done: false };
    internal bool HasRetired => _retiredWork != null || _retiredGeometry != null || _retiredLoad != null || _retiredUpload != null;
    internal string Diagnostics => !DebugTimings.Enabled ? "Debug timing collection is off." : $"Background preparation: {_workerMs:F1} ms; largest preparation update: {_maxMainMs:F2} ms"
        + $"; slowest preparation unit: {_slowestStep} {_slowestStepMs:F2} ms; native commit: {_commitMs:F2} ms"
        + $"; earlier-frame isolation: {_isolationMs:F2} ms"
        + (_isolationPhases.Length == 0 ? "" : "; isolation pass [" + _isolationPhases + "]")
        + (_commitPhases.Length == 0 ? "" : "; " + _commitPhases);
    internal string Phase => Isolation != null ? "Finishing the cut..."
        : _capture is { Done: false } ? "Reading nearby part..."
        : _upload != null ? "Preparing the pieces..." : "Preparing the cut...";

    internal void Begin(Part part, ContactSlice? contact = null, SlicePlaneRequest? planeRequest = null, SliceGeometryBudget? geometry = null,
        string? diagnosticDetector = null, double diagnosticTime = double.NaN)
    {
        if (Busy || _capture != null) throw new InvalidOperationException("The previous slice is finishing cleanup; try again shortly.");
        Error = null; _workerMs = _maxMainMs = _commitMs = _isolationMs = 0;
        CreatedParts = Array.Empty<Part>();
        _isolationPhases = ""; _isolatedAtHandoff = -1;
        _slowestStep = "none"; _slowestStepMs = 0; _commitPhases = "";
        SliceJitWarmup.Start(); // Fallback if startup was bypassed; never waits for compilation.
        _capture = new(part, contact, planeRequest, geometry);
        if (_scheduler == null && diagnosticDetector != null)
        {
            _serialDiagnostics = true;
            Trace = SliceJobDiagnostics.Start("vessel", diagnosticDetector, planeRequest?.StrategyId ?? "assisted", diagnosticTime, "serial");
            SliceJobDiagnostics.Count(SliceJobOutcome.SerialStarted, "serial");
        }
        if (_scheduler == null) SliceFrameDiagnostics.Arm();
    }
    internal void Advance(bool permitCapture = true, bool permitUpload = true, bool permitLoad = true)
    {
        // Called once by the active assisted/manual coordinator at the joined physics hook.
        // Player/simulation time can stay unchanged on pause; use hook progress.
        _handoff++;
        long started = DebugTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (_capture == null || Error != null || Ready) return;
            if (!_capture.Done)
            {
                if (permitCapture)
                {
                    Trace?.MarkCapture(); long captureStarted = Trace?.Start() ?? 0;
                    try { _capture.Advance(); } finally { Trace?.Add(SliceJobStage.Capture, captureStarted); }
                }
                return;
            }
            if (_scheduler != null)
            {
                if (!AdvanceQueued(permitUpload, permitLoad, out var queuedError))
                { if (queuedError != null) Error = queuedError; return; }
            }
            else if (_work == null)
            {
                var input = _capture.Input!;
                _inputBytes = _capture.RetainedBytes;
                var context = RuntimeFragmentAssets.CaptureWorkerContext();
                _workerStarted = DebugTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
                var trace = Trace;
                _work = new((cancel, publish) =>
                {
                    long geometryStarted = trace?.Start() ?? 0;
                    CpuSlice calculated;
                    try { calculated = SliceGeometryWork.Calculate(input, cancel, publish); }
                    finally { trace?.Add(SliceJobStage.Geometry, geometryStarted); }
                    Interlocked.Exchange(ref _outputBytes, RetainedOutputBytes(calculated));
                    Volatile.Write(ref _expectedFragments, calculated.Fragments.Length);
                    long loaderStarted = trace?.Start() ?? 0;
                    try
                    {
                        if (_scheduler == null) return RuntimeFragmentAssets.LoadDetached(calculated, context.Directory, context.Mod, cancel);
                        lock (SliceDetachedLoad.Gate)
                        {
                            cancel.ThrowIfCancellationRequested();
                            return RuntimeFragmentAssets.LoadDetached(calculated, context.Directory, context.Mod, cancel);
                        }
                    }
                    finally { trace?.Add(SliceJobStage.Loader, loaderStarted); }
                }, _scheduler);
                return;
            }
            if (_scheduler == null && _loaded == null)
            {
                if (!permitUpload) return;
                if (!_work!.TryTake(out var result, out var error)) return;
                if (_workerStarted != 0 && DebugTimings.Enabled)
                    _workerMs = Stopwatch.GetElapsedTime(_workerStarted).TotalMilliseconds;
                if (error != null) { Error = error; return; }
                _loaded = result!; _upload = new(result!);
            }
            if (!permitUpload) return;
            long uploadStarted = Trace?.Start() ?? 0;
            try { _upload!.Advance(); } finally { Trace?.Add(SliceJobStage.Upload, uploadStarted); }
            if (DebugTimings.Enabled)
            { _slowestStep = _upload.SlowestStep; _slowestStepMs = _upload.SlowestStepMs; }
            if (_upload.Error != null) Error = _upload.Error;
        }
        catch (Exception error) { Error = error.Message; }
        finally
        {
            if (started != 0 && DebugTimings.Enabled)
                _maxMainMs = Math.Max(_maxMainMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private bool AdvanceQueued(bool permitUpload, bool permitLoad, out string? error)
    {
        error = null;
        if (!_geometryStarted)
        {
            _geometryStarted = true;
            var input = _capture!.Input!; _inputBytes = _capture.RetainedBytes;
            _workerStarted = DebugTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
            var trace = Trace;
            _geometryWork = new((cancel, publish) =>
            {
                long started = trace?.Start() ?? 0;
                try
                {
                    var result = SliceGeometryWork.Calculate(input, cancel, publish);
                    Interlocked.Exchange(ref _outputBytes, RetainedOutputBytes(result));
                    Volatile.Write(ref _expectedFragments, result.Fragments.Length);
                    return result;
                }
                finally { trace?.Add(SliceJobStage.Geometry, started); }
            }, _scheduler);
            return false;
        }
        if (_geometryWork != null)
        {
            if (!_geometryWork.TryTake(out var calculated, out error)) return false;
            _savedPlan = _geometryWork.Plan; _geometryWork = null;
            if (_workerStarted != 0 && DebugTimings.Enabled) _workerMs = Stopwatch.GetElapsedTime(_workerStarted).TotalMilliseconds;
            if (error != null) return false;
            _calculated = calculated!;
        }
        if (_calculated != null)
        {
            if (!permitLoad) return false;
            var data = _calculated; _calculated = null;
            var context = RuntimeFragmentAssets.CaptureWorkerContext();
            var trace = Trace;
            _loadWork = new((cancel, _) =>
            {
                long started = trace?.Start() ?? 0;
                try { return RuntimeFragmentAssets.LoadDetached(data, context.Directory, context.Mod, cancel); }
                finally { trace?.Add(SliceJobStage.Loader, started); }
            },
                _loaderScheduler ?? throw new InvalidOperationException("Queued loading lane is unavailable."));
            return false;
        }
        if (_loaded == null)
        {
            if (!permitUpload || _loadWork == null || !_loadWork.TryTake(out var loaded, out error)) return false;
            _loadWork = null;
            if (error != null) return false;
            _loaded = loaded!; _upload = new(loaded!);
        }
        return true;
    }
    internal SliceAttempt Commit(Part part, double now)
    {
        SliceJobOutcome? serialOutcome = null;
        SliceAttempt Failed(string message) { serialOutcome = SliceJobOutcome.Failed; return new(false, message); }
        if (!Ready || _loaded == null || _capture == null)
        { FinishSerialDiagnostic(SliceJobOutcome.Failed, now); return new(false, "Slice preparation is not ready."); }
        SliceFrameDiagnostics.BeginOperation("swap", part.StructuralPart.Template.Id,
            part.Tree?.Count ?? 0, Isolation?.Parts.Length ?? -1, boundaryConnections: 0);
        long started = DebugTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        long traceStarted = Trace?.Start() ?? 0;
        var timings = SliceCommitTimings.Start();
        try
        {
            if (Isolation == null) return Failed("The source must be isolated on an earlier frame.");
            if (_handoff <= _isolatedAtHandoff) return Failed("The fragment swap must wait for the next simulation handoff.");
            if (!_capture.IsCurrent(part, Isolation)) { serialOutcome = SliceJobOutcome.Stale; return new(false, "The isolated source geometry changed before the swap. Try again."); }
            var plane = _loaded.Data.Plane;
            bool replaced = NativeSlicePhysics.TryReplace(Isolation, _upload!.Fragments,
                new(plane.Normal.X, plane.Normal.Y, plane.Normal.Z), out var created, out var reason, timings);
            timings.Stop();
            if (!replaced) return Failed(reason);
            CreatedParts = created.Select(v => v.Parts.Root.StructuralPart).ToArray();
            for (int i = 0; i < created.Count; i++)
            {
                try { RuntimeFragmentAssets.MarkHot(_upload.Fragments[i].Asset, now); }
                catch (Exception effect) { Console.WriteLine("[purr2d2] Slice completed; resource refresh: " + effect.Message); }
                foreach (var spark in _loaded.Fragments[i].Data.Sparks)
                    try { CutSparks?.Invoke(created[i].Parts.Root, spark.Point, spark.Normal, now); }
                    catch (Exception effect) { Console.WriteLine("[purr2d2] Slice completed; spark effect: " + effect.Message); }
            }
            serialOutcome = _loaded.Data.AuthoredSubpartFallback ? SliceJobOutcome.AuthoredFallback : SliceJobOutcome.MeshCut;
            return new(true, reason + (_loaded.Data.AuthoredSubpartFallback
                ? " Used authored subpart boundaries because mesh cutting failed."
                : _loaded.Data.ContactDirectionFallback
                    ? " Adjusted the cut angle to fit the contact geometry. Cut faces are cooling."
                    : " Cut faces are cooling."));
        }
        catch (Exception error) { return Failed("Slice commit failed: " + error.Message); }
        finally
        {
            Trace?.Add(SliceJobStage.Commit, traceStarted);
            timings.Stop();
            if (started != 0 && DebugTimings.Enabled)
            {
                _commitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _commitPhases = timings.ToString();
                Console.WriteLine("[purr2d2] " + Diagnostics);
            }
            if (serialOutcome.HasValue) FinishSerialDiagnostic(serialOutcome.Value, now);
        }
    }
    internal SliceAttempt Isolate(Part part)
    {
        if (!Ready || _loaded == null || _capture == null || Isolation != null)
            return new(false, "Slice preparation is not ready for native isolation.");
        SliceFrameDiagnostics.BeginOperation("isolation", part.StructuralPart.Template.Id, part.Tree?.Count ?? 0);
        var timings = SliceCommitTimings.Start();
        long started = DebugTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        long traceStarted = Trace?.Start() ?? 0;
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
            Trace?.Add(SliceJobStage.Commit, traceStarted);
            timings.Stop();
            if (started != 0 && DebugTimings.Enabled)
            {
                _isolationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _isolationPhases = timings.ToString();
            }
        }
    }
    internal void Cancel()
    {
        FinishSerialDiagnostic(Error != null ? SliceJobOutcome.Failed : SliceJobOutcome.Cancelled, double.NaN);
        if (_scheduler == null) SliceFrameDiagnostics.Disarm(); // Already captured frame windows still finish.
        _capture?.Dispose(); _capture = null;
        if (_work != null) { _work.Cancel(); _retiredWork = _work; _work = null; }
        if (_geometryWork != null) { _geometryWork.Cancel(); _retiredGeometry = _geometryWork; _geometryWork = null; }
        if (_loadWork != null) { _loadWork.Cancel(); _retiredLoad = _loadWork; _loadWork = null; }
        _calculated = null; _savedPlan = null; _geometryStarted = false;
        // Finish already-started asset registration without spawning anything. This avoids
        // corrupt partial IDs and lets a later cut reuse the prepared assets after cancellation.
        if (_upload != null) { _upload.Cancel(); _retiredUpload = _upload; _upload = null; }
        _loaded = null;
        Isolation?.Dispose(); Isolation = null;
    }
    internal void PumpRetired(bool permitUpload = true)
    {
        if (_retiredWork is { Finished: true }) { _retiredWork.TryTake(out _, out _); _retiredWork = null; }
        if (_retiredGeometry is { Finished: true }) { _retiredGeometry.TryTake(out _, out _); _retiredGeometry = null; }
        if (_retiredLoad is { Finished: true }) { _retiredLoad.TryTake(out _, out _); _retiredLoad = null; }
        if (_retiredUpload == null || !permitUpload) return;
        _retiredUpload.Advance();
        if (_retiredUpload.Done) { _retiredUpload.Dispose(); _retiredUpload = null; }
    }
    public void Dispose()
    {
        Cancel(); // Never join a CPU task, including at shutdown.
        _retiredUpload?.Dispose(); _retiredUpload = null; // GPU teardown is allowed to wait at shutdown.
    }
    private static long RetainedOutputBytes(CpuSlice slice)
    {
        long bytes = 0;
        checked { foreach (var fragment in slice.Fragments)
        { bytes += fragment.Glb.LongLength + fragment.Xml.LongLength; foreach (var upload in fragment.Uploads) bytes += upload.Bytes.LongLength; } }
        return bytes;
    }
}
