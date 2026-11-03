using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Force;
using Purr2D2.Rendering;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Slicing;

/// <summary>One ground cut at a time, using the shared durable mesh arena and its existing retirement discipline.</summary>
internal sealed class ClutterSlicer : IDisposable
{
    private readonly TaskScheduler? _scheduler;
    private readonly TaskScheduler? _loaderScheduler;
    private ClutterSliceSourceCapture? _capture;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _work, _retiredWork;
    private BackgroundSliceWork<SlicePlan, CpuSlice>? _geometryWork, _retiredGeometry;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _loadWork, _retiredLoad;
    private CpuSlice? _calculated;
    private bool _geometryStarted;
    private RuntimeFragmentAssets.UploadJob? _upload, _retiredUpload;
    private LoadedSlice? _loaded;
    private ClutterSliceDescription? _source;
    private KittenEva? _owner;
    private string? _failure;
    private double3 _motion;
    private bool _allowLegacyFallback;
    private long _inputBytes, _outputBytes;
    private int _expectedFragments;
    internal int ExpectedFragments => Volatile.Read(ref _expectedFragments);
    internal bool EstimateDone => _capture?.EstimateDone ?? false;
    internal long EstimatedBytes => _capture?.EstimatedBytes ?? 0;
    internal long RetainedBytes => checked(Math.Max(_inputBytes, _capture?.RetainedBytes ?? 0) + Interlocked.Read(ref _outputBytes));
    internal SliceJobTrace? Trace;
    private bool _queuedDiagnostics;
    private double _diagnosticNow = double.NaN;
    internal void SetQueuedTrace(SliceJobTrace? trace) { Trace = trace; _queuedDiagnostics = true; }
    private void FinishDiagnostic(SliceJobOutcome outcome)
    {
        if (!_queuedDiagnostics) return;
        SliceJobDiagnostics.Finish(Trace, outcome, _diagnosticNow, _scheduler == null ? "serial" : "queued"); Trace = null; _queuedDiagnostics = false;
    }
    internal void AdvanceEstimate()
    {
        Trace?.MarkCapture(); long started = Trace?.Start() ?? 0;
        try { _capture?.AdvanceEstimate(); }
        finally { Trace?.Add(SliceJobStage.Capture, started); }
    }
    internal void Refuse(string reason) { Status = reason; FinishDiagnostic(SliceJobOutcome.Rejected); Cancel(); }
    internal bool Active => _source != null;
    internal ClutterForceTarget? CoverageTarget => _source?.Target;
    internal bool Busy => _retiredWork is { Finished: false } || _retiredGeometry is { Finished: false }
        || _retiredLoad is { Finished: false } || _retiredUpload is { Done: false };
    internal bool HasRetired => _retiredWork != null || _retiredGeometry != null || _retiredLoad != null || _retiredUpload != null;
    internal bool NeedsCapture => _source != null && _capture is { Done: false };
    internal bool HasCapturedInput => _source != null && _capture is { Done: true };
    internal bool OwnsUpload => _upload != null || _retiredUpload != null;
    internal bool CanAcquireUpload => _source != null && (_scheduler == null ? _work is { Finished: true } : _loadWork is { Finished: true });
    internal bool HoldsLoadSlot => _loadWork != null || _retiredLoad != null || _loaded != null || OwnsUpload;
    internal Action<Part, double3, double3, double>? CutSparks;
    internal Action<Part, double3, double3, double, double>? TreeFire;
    internal Action<Part>? FragmentCreated;
    internal string Status { get; private set; } = "";

    internal ClutterSlicer(TaskScheduler? scheduler = null, TaskScheduler? loaderScheduler = null)
    { _scheduler = scheduler; _loaderScheduler = loaderScheduler; }

    internal bool Begin(KittenEva owner, ClutterForceTarget target, double3 contactCci, double3 bladeCci, double3 motionCci,
        string? planeStrategy = null, string trigger = "ManualContact", SliceSweepSamples? sweepCci = null,
        string? diagnosticDetector = null, double diagnosticTime = double.NaN)
    {
        if (Active || Busy || !target.IsLocalTo(owner) || !ClutterForceNative.TryDescribeSlice(target, out var source)
            || !ClutterForceNative.TrySliceFrame(source!, out var frame))
        { if (_scheduler == null && diagnosticDetector != null) SliceJobDiagnostics.Count(SliceJobOutcome.Rejected, "serial"); return false; }
        var inverse = frame.Object2Cci.Inverse();
        var point = (contactCci - frame.PositionCci).Transform(inverse);
        var blade = Point(bladeCci.Transform(inverse)); var motion = Point(motionCci.Transform(inverse));
        if (!ContactSliceGeometry.TryCreate(Point(point), blade, motion,
            out var contact, out _))
        { if (_scheduler == null && diagnosticDetector != null) SliceJobDiagnostics.Count(SliceJobOutcome.Rejected, "serial"); return false; }
        SliceSweepSamples? sweepLocal = sweepCci?.Map(p => Point((new double3(p.X, p.Y, p.Z) - frame.PositionCci).Transform(inverse)));
        SlicePlaneRequest? request = planeStrategy == null ? null : new(planeStrategy, trigger,
            source!.Tree ? "Tree" : "Rock", Point(point), blade, motion, Samples: sweepLocal);
        _allowLegacyFallback = request?.AllowsLegacyFallbacks ?? true;
        _source = source; _owner = owner; _motion = motionCci;
        _capture = new(source!, contact, request); Status = "Preparing ground-object cut...";
        if (_scheduler == null && diagnosticDetector != null)
        {
            _queuedDiagnostics = true;
            Trace = SliceJobDiagnostics.Start("clutter", diagnosticDetector, planeStrategy ?? "legacy-contact", diagnosticTime, "serial");
            SliceJobDiagnostics.Count(SliceJobOutcome.SerialStarted, "serial");
        }
        return true;
    }

    internal void Update(KittenEva? owner, bool permitCommit, double now, double fireSeconds,
        bool permitCapture = true, bool permitUpload = true, bool permitLoad = true)
    {
        _diagnosticNow = now;
        PumpRetired(permitUpload);
        if (_source == null) return;
        if (!ReferenceEquals(owner, _owner) || owner == null || !_source.Target.IsLocalTo(owner) || !_source.Target.IsValid)
        { Status = "Ground-object cut cancelled: its owner or scene changed."; FinishDiagnostic(SliceJobOutcome.Stale); Cancel(); return; }
        bool committed = false;
        try
        {
            if (_failure != null) { Fallback(_failure, permitCommit); return; }
            if (!_capture!.Done)
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
                { if (queuedError != null) Fallback(queuedError, permitCommit); return; }
            }
            else if (_work == null)
            {
                var input = _capture.Input!;
                _inputBytes = _capture.RetainedBytes;
                bool tree = _source.Tree;
                var context = RuntimeFragmentAssets.CaptureWorkerContext();
                var trace = Trace;
                _work = new((cancel, publish) =>
                {
                    long geometryStarted = trace?.Start() ?? 0;
                    CpuSlice calculated;
                    try { calculated = ClutterSliceGeometry.Calculate(input, tree, cancel, publish); }
                    finally { trace?.Add(SliceJobStage.Geometry, geometryStarted); }
                    Interlocked.Exchange(ref _outputBytes, RetainedOutputBytes(calculated));
                    Volatile.Write(ref _expectedFragments, calculated.Fragments.Length);
                    LoadedSlice result;
                    long loaderStarted = trace?.Start() ?? 0;
                    try
                    {
                        if (_scheduler == null) result = RuntimeFragmentAssets.LoadDetached(calculated, context.Directory, context.Mod, cancel);
                        else lock (SliceDetachedLoad.Gate)
                        {
                            cancel.ThrowIfCancellationRequested();
                            result = RuntimeFragmentAssets.LoadDetached(calculated, context.Directory, context.Mod, cancel);
                        }
                    }
                    finally { trace?.Add(SliceJobStage.Loader, loaderStarted); }
                    Interlocked.Exchange(ref _outputBytes, RetainedOutputBytes(result.Data));
                    return result;
                }, _scheduler);
                return;
            }
            if (_scheduler == null && _loaded == null)
            {
                if (!permitUpload) return;
                if (!_work!.TryTake(out var loaded, out var error)) return;
                if (error != null) { Fallback(error, permitCommit); return; }
                _loaded = loaded!; _upload = new(loaded!);
            }
            Status = "Preparing ground fragments...";
            if (!permitUpload) return;
            long uploadStarted = Trace?.Start() ?? 0;
            try { _upload!.Advance(); } finally { Trace?.Add(SliceJobStage.Upload, uploadStarted); }
            if (_upload.Error != null) { Fallback(_upload.Error, permitCommit); return; }
            if (!_upload.Done || !permitCommit) return;
            long commitStarted = Trace?.Start() ?? 0;
            bool replaced; IReadOnlyList<Vehicle> created; string reason;
            try { replaced = NativeSlicePhysics.TryReplaceClutter(owner, _source, _upload.Fragments, _loaded!.Data.Plane, out created, out reason); }
            finally { Trace?.Add(SliceJobStage.Commit, commitStarted); }
            if (!replaced) { Fallback(reason, permitCommit); return; }
            committed = true;
            FinishDiagnostic(SliceJobOutcome.MeshCut);
            Status = (_source.Tree ? "Tree" : "Rock") + " cut into " + created.Count + " separating fragments. " + reason;
            for (int i = 0; i < created.Count; i++)
            {
                try { FragmentCreated?.Invoke(created[i].Parts.Root); }
                catch (Exception effect) { Console.WriteLine("[purr2d2] Ground cut provenance: " + effect.Message); }
                // The original is gone and the replacement vehicles own these
                // fragments. Optional bookkeeping/effects cannot undo that cut
                // or send the removed original through whole-object fallback.
                try { RuntimeFragmentAssets.MarkHot(_upload.Fragments[i].Asset, now); }
                catch (Exception effect) { Console.WriteLine("[purr2d2] Ground cut resource refresh: " + effect.Message); }
                // Both ends are attached to their own moving fragments. Bound each
                // fragment to four fire anchors even on dense cap triangulations.
                var sparks = _loaded.Fragments[i].Data.Sparks;
                for (int s = 0; s < Math.Min(4, sparks.Length); s++)
                {
                    try { CutSparks?.Invoke(created[i].Parts.Root, sparks[s].Point, sparks[s].Normal, now); }
                    catch (Exception effect) { Console.WriteLine("[purr2d2] Ground cut effect: " + effect.Message); }
                    try
                    { if (_source.Tree && fireSeconds > 0) TreeFire?.Invoke(created[i].Parts.Root, sparks[s].Point, sparks[s].Normal, now, fireSeconds); }
                    catch (Exception effect) { Console.WriteLine("[purr2d2] Ground cut fire: " + effect.Message); }
                }
            }
            Cancel();
        }
        catch (Exception error)
        {
            if (!committed) { Fallback(error.Message, permitCommit); return; }
            Console.WriteLine("[purr2d2] Ground cut completed; finalization reported: " + error.Message);
            Cancel();
        }
    }

    private bool AdvanceQueued(bool permitUpload, bool permitLoad, out string? error)
    {
        error = null;
        if (!_geometryStarted)
        {
            _geometryStarted = true;
            var input = _capture!.Input!; _inputBytes = _capture.RetainedBytes; bool tree = _source!.Tree;
            var trace = Trace;
            _geometryWork = new((cancel, publish) =>
            {
                long started = trace?.Start() ?? 0;
                try
                {
                    var result = ClutterSliceGeometry.Calculate(input, tree, cancel, publish);
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
            _geometryWork = null;
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

    private void Fallback(string reason, bool permitCommit)
    {
        if (!_allowLegacyFallback)
        {
            Status = "Ground mesh cut failed: " + reason;
            FinishDiagnostic(SliceJobOutcome.Failed);
            Cancel();
            return;
        }
        // Pause can still drain CPU/GPU preparation, but cannot wake/change native bodies.
        if (!permitCommit) { Status = "Waiting to finish the ground-object interaction..."; _failure = reason; return; }
        Console.WriteLine("[purr2d2] Ground mesh cut unavailable; trying native displacement: " + reason);
        try
        {
            if (_source == null) return;
            long started = Trace?.Start() ?? 0;
            bool displaced; string fallback;
            try { displaced = ClutterForceNative.ToppleClutter(_source, _motion, out fallback); }
            finally { Trace?.Add(SliceJobStage.Commit, started); }
            Status = displaced ? fallback : "Ground-object displacement failed: " + fallback;
            FinishDiagnostic(displaced ? SliceJobOutcome.Toppled : SliceJobOutcome.Failed);
        }
        catch (Exception error) { Status = "Ground-object displacement failed: " + error.Message; FinishDiagnostic(SliceJobOutcome.Failed); }
        Cancel();
    }

    internal void Cancel()
    {
        FinishDiagnostic(SliceJobOutcome.Cancelled);
        _capture?.Dispose(); _capture = null;
        if (_work != null) { _work.Cancel(); _retiredWork = _work; _work = null; }
        if (_geometryWork != null) { _geometryWork.Cancel(); _retiredGeometry = _geometryWork; _geometryWork = null; }
        if (_loadWork != null) { _loadWork.Cancel(); _retiredLoad = _loadWork; _loadWork = null; }
        _calculated = null; _geometryStarted = false;
        if (_upload != null) { _upload.Cancel(); _retiredUpload = _upload; _upload = null; }
        _source = null; _owner = null; _loaded = null; _failure = null;
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
    public void Dispose() { Cancel(); _retiredUpload?.Dispose(); _retiredUpload = null; }
    private static long RetainedOutputBytes(CpuSlice slice)
    {
        long bytes = 0;
        checked
        {
            foreach (var fragment in slice.Fragments)
            {
                bytes += fragment.Glb.LongLength + fragment.Xml.LongLength;
                foreach (var upload in fragment.Uploads) bytes += upload.Bytes.LongLength;
            }
        }
        return bytes;
    }
    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
}
