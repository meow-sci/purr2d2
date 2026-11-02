using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Force;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

/// <summary>One ground cut at a time, using the shared durable mesh arena and its existing retirement discipline.</summary>
internal sealed class ClutterSlicer : IDisposable
{
    private ClutterSliceSourceCapture? _capture;
    private BackgroundSliceWork<SlicePlan, LoadedSlice>? _work, _retiredWork;
    private RuntimeFragmentAssets.UploadJob? _upload, _retiredUpload;
    private LoadedSlice? _loaded;
    private ClutterSliceDescription? _source;
    private KittenEva? _owner;
    private string? _failure;
    private double3 _motion;
    internal bool Active => _source != null;
    internal bool Busy => _retiredWork is { Finished: false } || _retiredUpload is { Done: false };
    internal Action<Part, double3, double3, double>? CutSparks;
    internal Action<Part, double3, double3, double, double>? TreeFire;
    internal string Status { get; private set; } = "";

    internal bool Begin(KittenEva owner, ClutterForceTarget target, double3 contactCci, double3 bladeCci, double3 motionCci)
    {
        if (Active || Busy || !target.IsLocalTo(owner) || !ClutterForceNative.TryDescribeSlice(target, out var source)
            || !ClutterForceNative.TrySliceFrame(source!, out var frame)) return false;
        var inverse = frame.Object2Cci.Inverse();
        var point = (contactCci - frame.PositionCci).Transform(inverse);
        if (!ContactSliceGeometry.TryCreate(Point(point), Point(bladeCci.Transform(inverse)), Point(motionCci.Transform(inverse)),
            out var contact, out _)) return false;
        _source = source; _owner = owner; _motion = motionCci;
        _capture = new(source!, contact); Status = "Preparing ground-object cut...";
        return true;
    }

    internal void Update(KittenEva? owner, bool permitCommit, double now, double fireSeconds)
    {
        PumpRetired();
        if (_source == null) return;
        if (!ReferenceEquals(owner, _owner) || owner == null || !_source.Target.IsLocalTo(owner) || !_source.Target.IsValid)
        { Status = "Ground-object cut cancelled: its owner or scene changed."; Cancel(); return; }
        bool committed = false;
        try
        {
            if (_failure != null) { Fallback(_failure, permitCommit); return; }
            if (!_capture!.Done) { _capture.Advance(); return; }
            if (_work == null)
            {
                var input = _capture.Input!;
                bool tree = _source.Tree;
                var context = RuntimeFragmentAssets.CaptureWorkerContext();
                _work = new((cancel, publish) =>
                {
                    var calculated = ClutterSliceGeometry.Calculate(input, tree, cancel, publish);
                    return RuntimeFragmentAssets.LoadDetached(calculated, context.Directory, context.Mod, cancel);
                });
                return;
            }
            if (_loaded == null)
            {
                if (!_work.TryTake(out var loaded, out var error)) return;
                if (error != null) { Fallback(error, permitCommit); return; }
                _loaded = loaded!; _upload = new(loaded!);
            }
            Status = "Preparing ground fragments...";
            _upload!.Advance();
            if (_upload.Error != null) { Fallback(_upload.Error, permitCommit); return; }
            if (!_upload.Done || !permitCommit) return;
            if (!NativeSlicePhysics.TryReplaceClutter(owner, _source, _upload.Fragments, _loaded.Data.Plane,
                out var created, out var reason)) { Fallback(reason, permitCommit); return; }
            committed = true;
            Status = (_source.Tree ? "Tree" : "Rock") + " cut into " + created.Count + " separating fragments. " + reason;
            for (int i = 0; i < created.Count; i++)
            {
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

    private void Fallback(string reason, bool permitCommit)
    {
        // Pause can still drain CPU/GPU preparation, but cannot wake/change native bodies.
        if (!permitCommit) { Status = "Waiting to finish the ground-object interaction..."; _failure = reason; return; }
        Console.WriteLine("[purr2d2] Ground mesh cut unavailable; trying native displacement: " + reason);
        try
        {
            if (_source == null) return;
            bool displaced = ClutterForceNative.ToppleClutter(_source, _motion, out var fallback);
            Status = displaced ? fallback : "Ground-object displacement failed: " + fallback;
        }
        catch (Exception error) { Status = "Ground-object displacement failed: " + error.Message; }
        Cancel();
    }

    internal void Cancel()
    {
        _capture?.Dispose(); _capture = null;
        if (_work != null) { _work.Cancel(); _retiredWork = _work; _work = null; }
        if (_upload != null) { _upload.Cancel(); _retiredUpload = _upload; _upload = null; }
        _source = null; _owner = null; _loaded = null; _failure = null;
    }
    internal void PumpRetired()
    {
        if (_retiredWork is { Finished: true }) { _retiredWork.TryTake(out _, out _); _retiredWork = null; }
        if (_retiredUpload == null) return;
        _retiredUpload.Advance();
        if (_retiredUpload.Done) { _retiredUpload.Dispose(); _retiredUpload = null; }
    }
    public void Dispose() { Cancel(); _retiredUpload?.Dispose(); _retiredUpload = null; }
    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
}
