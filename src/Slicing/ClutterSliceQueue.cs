using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Slicing;

/// <summary>Bounded ground-cut jobs. All methods except worker computation run in the settled handoff.</summary>
internal sealed class ClutterSliceQueue : IDisposable
{
    private sealed class Entry(ClutterForceIdentity key, ClutterSlicer slicer)
    {
        internal ClutterForceIdentity Key { get; } = key;
        internal ClutterSlicer Slicer { get; } = slicer;
        internal long Reservation;
        internal int NativeReservation;
    }
    private readonly List<Entry> _entries = [];
    private readonly HashSet<ClutterForceIdentity> _pending = [];
    private readonly SliceWorkerPool _pool;
    private readonly SliceWorkerPool _loader;
    private readonly int _limit;
    private readonly long _memoryLimit;
    internal long EstimatedBytes => _entries.Sum(e => e.Reservation);
    internal long RetainedBytes => _entries.Sum(e => e.Slicer.RetainedBytes);
    internal int ReservedVehicles => _entries.Sum(e => e.NativeReservation);
    internal long ChargedBytes => _entries.Sum(e => Math.Max(e.Reservation, e.Slicer.RetainedBytes));
    internal int ReservationCount => _entries.Count(e => e.Reservation != 0);
    internal int LoadSlots => _entries.Count(e => e.Slicer.HoldsLoadSlot);
    internal Action<Part, double3, double3, double>? CutSparks;
    internal Action<Part, double3, double3, double, double>? TreeFire;
    internal Action<Part, ulong>? FragmentCreated;
    internal int Count => _entries.Count;
    internal int OtherCount { get; set; }
    internal bool Full => _entries.Count + OtherCount >= _limit;
    internal bool Active => _entries.Count != 0;
    internal bool HasUpload => _entries.Any(e => e.Slicer.OwnsUpload);
    internal TaskScheduler Scheduler => _pool;
    internal TaskScheduler LoaderScheduler => _loader;
    internal int WorkerCount => _pool.WorkerCount;
    internal HashSet<ClutterForceIdentity> Pending => _pending;
    internal IEnumerable<ClutterForceTarget> CoverageTargets => _entries.Select(entry => entry.Slicer.CoverageTarget).OfType<ClutterForceTarget>();
    internal string Status { get; private set; } = "";

    internal ClutterSliceQueue(int workers, int limit, int memoryMiB = 256)
    {
        _pool = new SliceWorkerPool(Math.Clamp(workers, 1, Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2))));
        _loader = new SliceWorkerPool(1);
        _limit = Math.Clamp(limit, 16, 512);
        _memoryLimit = checked((long)Math.Clamp(memoryMiB, 64, 1024) * 1024 * 1024);
    }

    internal bool Begin(KittenEva owner, ClutterForceTarget target, double3 point, double3 blade, double3 motion,
        string? planeStrategy = null, string trigger = "ManualContact", SliceSweepSamples? sweepCci = null, ulong stroke = 0,
        string detector = "unknown", double detectionTime = double.NaN)
    {
        var key = target.StableKey;
        if (_pending.Contains(key)) { SliceJobDiagnostics.Count(SliceJobOutcome.Deduplicated); return false; }
        if (Full) { SliceJobDiagnostics.Count(SliceJobOutcome.QueueFull); return false; }
        var slicer = new ClutterSlicer(_pool, _loader) { CutSparks = CutSparks, TreeFire = TreeFire,
            FragmentCreated = part => FragmentCreated?.Invoke(part, stroke) };
        if (!slicer.Begin(owner, target, point, blade, motion, planeStrategy, trigger, sweepCci))
        { slicer.Dispose(); SliceJobDiagnostics.Count(SliceJobOutcome.Rejected); return false; }
        slicer.SetQueuedTrace(SliceJobDiagnostics.Start("clutter", detector, planeStrategy ?? "legacy-contact", detectionTime));
        SliceJobDiagnostics.Count(SliceJobOutcome.Queued);
        _pending.Add(key);
        _entries.Add(new(key, slicer));
        return true;
    }

    internal void Update(KittenEva? owner, bool permitCommit, double now, double fireSeconds,
        bool permitCapture = true, bool permitUpload = true, long otherChargedBytes = 0, int otherReservedVehicles = 0,
        int otherReservations = 0, int otherLoadSlots = 0)
    {
        // One capture and one upload can consume their existing per-frame budgets.
        // Retiring workers and uploads may still own copied inputs or outputs.
        // Preflight one source incrementally per update. It reads mesh metadata only;
        // oversized entries are refused, allowing smaller later entries to proceed.
        var preflight = !permitCapture || _entries.Any(e => e.Reservation != 0 && e.Slicer.NeedsCapture) ? null
            : _entries.FirstOrDefault(e => e.Slicer.NeedsCapture && !e.Slicer.EstimateDone);
        if (preflight != null)
        {
            try { preflight.Slicer.AdvanceEstimate(); }
            catch (Exception error) { preflight.Slicer.Refuse("Ground-object estimate failed: " + error.Message); }
        }
        foreach (var entry in _entries)
        {
            if (entry.Reservation != 0 || !entry.Slicer.NeedsCapture || !entry.Slicer.EstimateDone) continue;
            long estimate = entry.Slicer.EstimatedBytes;
            if (estimate > _memoryLimit)
            {
                entry.Slicer.Refuse($"Ground-object cut requires an estimated {estimate / (1024 * 1024)} MiB; raise SliceWorkingMemoryMiB to allow it.");
                continue;
            }
            if (ReservationCount + otherReservations >= _pool.WorkerCount) continue;
            if (estimate > _memoryLimit - Math.Min(_memoryLimit, ChargedBytes + otherChargedBytes)) continue;
            entry.Reservation = estimate;
        }
        ClutterSlicer? capture = permitCapture && preflight == null
            ? _entries.FirstOrDefault(e => e.Reservation != 0 && e.Slicer.NeedsCapture)?.Slicer : null;
        // Reserve native vehicle slots before loading results enter upload. Other
        // queued results cannot all rely on the same currently available slots.
        foreach (var entry in _entries)
        {
            if (!entry.Slicer.Active) { entry.NativeReservation = 0; continue; }
            int fragments = entry.Slicer.ExpectedFragments;
            if (owner == null || fragments == 0 || entry.NativeReservation != 0) continue;
            if ((long)owner.BubbleVehicleCount + fragments > NativeSlicePhysics.VehicleLimit)
            {
                entry.Slicer.Refuse("Ground cut exceeds the configured local physics-vehicle budget.");
                continue;
            }
            if ((long)owner.BubbleVehicleCount + ReservedVehicles + otherReservedVehicles + fragments <= NativeSlicePhysics.VehicleLimit)
                entry.NativeReservation = fragments;
        }
        ClutterSlicer? upload = !permitUpload ? null : _entries.Select(e => e.Slicer).FirstOrDefault(s => s.OwnsUpload)
            ?? _entries.FirstOrDefault(e => e.Slicer.CanAcquireUpload
                && (e.Slicer.ExpectedFragments == 0 || e.NativeReservation != 0))?.Slicer;
        // Failures without an upload may need one native fallback; allow only one
        // source to perform any native transaction during this update.
        ClutterSlicer? commit = upload ?? _entries.Select(e => e.Slicer).FirstOrDefault(s => s.Active);
        foreach (var entry in _entries)
        {
            var slicer = entry.Slicer;
            slicer.Update(owner, permitCommit && ReferenceEquals(slicer, commit), now, fireSeconds,
                permitCapture: ReferenceEquals(slicer, capture), permitUpload: ReferenceEquals(slicer, upload),
                permitLoad: LoadSlots + otherLoadSlots < 2);
            if (!slicer.Active && slicer.Status.Length != 0) Status = slicer.Status;
        }
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Slicer.Active || entry.Slicer.HasRetired) continue;
            _pending.Remove(entry.Key);
            entry.Slicer.Dispose();
            _entries.RemoveAt(i);
        }
    }

    internal void Cancel()
    {
        foreach (var entry in _entries) entry.Slicer.Cancel();
    }

    internal void PumpRetired()
    {
        bool uploadBudgetAvailable = true;
        foreach (var entry in _entries)
        {
            bool ownsUpload = entry.Slicer.OwnsUpload;
            entry.Slicer.PumpRetired(uploadBudgetAvailable && ownsUpload);
            if (ownsUpload && uploadBudgetAvailable) uploadBudgetAvailable = false;
        }
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Slicer.Active || entry.Slicer.HasRetired) continue;
            _pending.Remove(entry.Key); entry.Slicer.Dispose(); _entries.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        Cancel();
        foreach (var entry in _entries) entry.Slicer.Dispose();
        _entries.Clear(); _pending.Clear(); _pool.Dispose(); _loader.Dispose();
    }
}
