using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Slicing;

/// <summary>Game-thread-owned vessel intents and transactions; all worker input is copied.</summary>
internal sealed class VesselSliceQueue : IDisposable
{
    private sealed record Node(Part Part, double4x4 LocalTransform, bool IsPart,
        object? Mesh, object? Positions, object? Material, bool Glass);
    private sealed class Entry
    {
        internal required KittenEva Actor;
        internal required SliceTarget Target;
        internal required Vehicle Group;
        internal readonly List<Node> Signature = [];
        internal IEnumerator<Node>? Scan;
        internal int ScanIndex;
        internal bool SignatureReady, Verifying, Verified, HitMeshFound;
        internal required object SourceTemplate;
        internal required int SubpartCount;
        internal required object HitMesh;
        internal required object HitPrimitives;
        internal required Func<bool> EvidenceCurrent;
        internal required SlicePlaneRequest LocalRequest;
        internal required SliceGeometryBudget Geometry;
        internal ulong Stroke;
        internal ArbitrarySlicer? Slicer;
        internal long Reservation;
        internal int NativeReservation;
        internal bool Isolated;
        internal bool Retiring;
        internal SliceJobTrace? Trace;
        internal bool DiagnosticFinished;
    }
    private readonly List<Entry> _entries = [];
    private readonly SliceStrokeProvenance<Part> _generated;
    private readonly Dictionary<Part, double> _rejectedUntil = [];
    private double _now;
    private long _otherChargedBytes;
    private readonly TaskScheduler _scheduler;
    private readonly TaskScheduler _loaderScheduler;
    private readonly int _workers;
    private readonly int _limit;
    private readonly long _memoryLimit;
    internal Action<Part, double3, double3, double>? CutSparks;
    internal int Count => _entries.Count;
    internal bool Active => _entries.Count != 0;
    internal bool HasUpload => _entries.Any(e => e.Slicer?.OwnsUpload == true);
    internal bool WantsUpload => HasUpload || _entries.Any(e => !e.Retiring && e.Slicer?.CanAcquireUpload == true
        && (e.Slicer.ExpectedFragments == 0 || e.NativeReservation != 0));
    internal bool WantsCapture => _entries.Any(e => !e.Retiring && (e.Slicer == null || e.Slicer.NeedsCapture
        || e.Slicer.Ready && !e.Verified));
    internal bool WantsCommit => _entries.Any(e => !e.Retiring && e.Verified && e.Slicer?.Ready == true);
    internal long ChargedBytes => _entries.Sum(e => checked(Math.Max(e.Reservation, e.Slicer?.RetainedBytes ?? 0) + 128L * e.Signature.Count));
    internal int ReservationCount => _entries.Count(e => e.Reservation != 0);
    internal int LoadSlots => _entries.Count(e => e.Slicer?.HoldsLoadSlot == true);
    internal int ReservedVehicles => _entries.Sum(e => e.NativeReservation);
    internal string Status { get; private set; } = "";
    internal VesselSliceQueue(TaskScheduler scheduler, TaskScheduler loaderScheduler, int workers, int limit, int memoryMiB,
        SliceStrokeProvenance<Part>? provenance = null)
    { _scheduler = scheduler; _loaderScheduler = loaderScheduler; _workers = workers; _limit = Math.Clamp(limit, 16, 512); _memoryLimit = checked((long)Math.Clamp(memoryMiB, 64, 1024) * 1024 * 1024); _generated = provenance ?? new(); }

    internal bool Begin(KittenEva actor, SliceTarget target, SlicePlaneRequest request, int otherCount, ulong stroke, double now,
        object? hitMesh, object? hitPrimitives, Func<bool> evidenceCurrent, string detector = "unknown", double detectionTime = double.NaN)
    {
        if (_entries.Any(e => ReferenceEquals(e.Target.Part, target.Part)) || _generated.Suppress(target.Part, stroke))
        { SliceJobDiagnostics.Count(SliceJobOutcome.Deduplicated); return false; }
        if (_entries.Count + otherCount >= _limit) { SliceJobDiagnostics.Count(SliceJobOutcome.QueueFull); return false; }
        if ((_rejectedUntil.TryGetValue(target.Part, out var until) && now < until)
            || hitMesh == null || hitPrimitives == null || !evidenceCurrent()
            || !PartSliceTargeting.CheckQueuedSliceTarget(actor, target, out _)
            || !double4x4.Invert(target.PartTransform, out var toLocal))
        { SliceJobDiagnostics.Count(SliceJobOutcome.Rejected); return false; }
        if (target.Part.SubParts.Length > 32768) { SliceJobDiagnostics.Count(SliceJobOutcome.Rejected); return false; }
        _entries.Add(new() { Actor = actor, Target = target, Group = target.Vehicle,
            SourceTemplate = target.Part.Template, SubpartCount = target.Part.SubParts.Length,
            HitMesh = hitMesh, HitPrimitives = hitPrimitives, EvidenceCurrent = evidenceCurrent,
            LocalRequest = TransformRequest(request, toLocal), Geometry = SliceGeometryBudget.Current, Stroke = stroke,
            Trace = SliceJobDiagnostics.Start("vessel", detector, request.StrategyId, detectionTime) });
        SliceJobDiagnostics.Count(SliceJobOutcome.Queued);
        return true;
    }

    // The caller grants at most one cross-domain capture, upload and native step.
    internal void Update(KittenEva? actor, bool permitCommit, double now, bool permitCapture, bool permitUpload,
        long otherChargedBytes, int otherReservedVehicles, int otherReservations, int otherLoadSlots)
    {
        _now = now;
        _otherChargedBytes = otherChargedBytes;
        foreach (var e in _entries)
        {
            if (e.Retiring)
            {
                bool ownsUpload = e.Slicer?.OwnsUpload == true;
                e.Slicer?.PumpRetired(permitUpload && ownsUpload);
                // Retirement consumed this handoff's shared upload lane even if
                // it finished and released the job during the poll.
                if (ownsUpload && permitUpload) permitUpload = false;
                continue;
            }
            if (!ReferenceEquals(actor, e.Actor) || actor == null || actor.IsDisposed)
            { Reject(e, "Vessel cut cancelled: its owner or scene changed.", SliceJobOutcome.Stale); continue; }
            if (!ReferenceEquals(e.Target.Part.Tree?.OwningVehicle, e.Target.Vehicle)
                || !e.Target.Part.MatrixAsmb2VehicleAsmb.Equals(e.Target.PartTransform))
            { Reject(e, "Vessel cut became stale: source ownership changed.", SliceJobOutcome.Stale); continue; }
        }
        // Finish validation of ready jobs before starting more source captures;
        // otherwise a long stream of newly admitted intents can starve commits.
        bool verifyReady = permitCapture && _entries.Any(e => !e.Retiring && !e.Verified && e.Slicer?.Ready == true && e.NativeReservation != 0);
        // Only one job per original vessel group may capture or transact at once.
        var start = verifyReady || _entries.Any(e => !e.Retiring && e.Slicer is { NeedsCapture: true } && (!e.Slicer.EstimateDone || e.Reservation != 0)) ? null
            : _entries.FirstOrDefault(e => !e.Retiring && e.Slicer == null
            && !_entries.Any(other => !ReferenceEquals(e, other) && other.Slicer != null
                && (ReferenceEquals(other.Group, e.Group) || ReferenceEquals(other.Target.Vehicle, e.Target.Vehicle))));
        if (permitCapture && start != null)
        {
            try
            {
                if (!start.SignatureReady)
                {
                    start.Trace?.MarkCapture(); long started = start.Trace?.Start() ?? 0;
                    try { AdvanceSignature(start, verify: false); } finally { start.Trace?.Add(SliceJobStage.Capture, started); }
                    permitCapture = false;
                }
                else if (start.Verifying)
                {
                    start.Trace?.MarkCapture(); long started = start.Trace?.Start() ?? 0;
                    try { AdvanceSignature(start, verify: true); } finally { start.Trace?.Add(SliceJobStage.Capture, started); }
                    permitCapture = false;
                }
                else
                {
                if (!BasicCurrent(start) || !start.EvidenceCurrent()) throw new InvalidDataException("Source geometry changed before capture.");
                var request = TransformRequest(start.LocalRequest, start.Target.Part.MatrixAsmb2VehicleAsmb);
                if (!ContactSliceGeometry.TryCreate(request.Anchor, request.BladeAtHit, request.MotionAtHit, out var contact, out var reason))
                    throw new InvalidDataException(reason);
                start.Slicer = new ArbitrarySlicer(_scheduler, _loaderScheduler) { CutSparks = CutSparks, Trace = start.Trace };
                start.Slicer.Begin(start.Target.Part, contact, request, start.Geometry);
                }
            }
            catch (Exception error) { Reject(start, "Vessel cut rejected: " + error.Message); }
            permitCapture = false;
        }
        var preflight = permitCapture && !verifyReady ? _entries.FirstOrDefault(e => !e.Retiring && e.Slicer is { NeedsCapture: true, EstimateDone: false }) : null;
        if (preflight != null)
        {
            try { preflight.Slicer!.AdvanceEstimate(); }
            catch (Exception error) { Reject(preflight, "Vessel estimate failed: " + error.Message); }
            permitCapture = false;
        }
        foreach (var e in _entries)
        {
            var s = e.Slicer;
            if (e.Retiring || s == null || !s.NeedsCapture || !s.EstimateDone || e.Reservation != 0) continue;
            if (s.EstimatedBytes > _memoryLimit) { Reject(e, "Vessel cut exceeds SliceWorkingMemoryMiB."); continue; }
            if (ReservationCount + otherReservations >= _workers) continue;
            if (s.EstimatedBytes <= _memoryLimit - Math.Min(_memoryLimit, ChargedBytes + otherChargedBytes)) e.Reservation = s.EstimatedBytes;
        }
        var capture = permitCapture && !verifyReady ? _entries.FirstOrDefault(e => !e.Retiring && e.Reservation > 0 && e.Slicer?.NeedsCapture == true) : null;
        if (capture != null) permitCapture = false;
        var upload = permitUpload ? _entries.FirstOrDefault(e => e.Slicer?.OwnsUpload == true)
            ?? _entries.FirstOrDefault(e => !e.Retiring && e.Slicer?.CanAcquireUpload == true) : null;
        foreach (var e in _entries)
        {
            var s = e.Slicer;
            if (e.Retiring || s == null) continue;
            if (s.ExpectedFragments > 0 && e.NativeReservation == 0)
            {
                if ((long)e.Actor.BubbleVehicleCount + s.ExpectedFragments > NativeSlicePhysics.VehicleLimit)
                { Reject(e, "Vessel cut exceeds the local physics-vehicle limit."); continue; }
                if ((long)e.Actor.BubbleVehicleCount + ReservedVehicles + otherReservedVehicles + s.ExpectedFragments <= NativeSlicePhysics.VehicleLimit)
                    e.NativeReservation = s.ExpectedFragments;
            }
            bool uploadAllowed = ReferenceEquals(e, upload) && (s.ExpectedFragments == 0 || e.NativeReservation != 0);
            s.Advance(ReferenceEquals(e, capture), uploadAllowed, LoadSlots + otherLoadSlots < 2);
            if (s.Error != null) { Reject(e, "Vessel cut failed: " + s.Error); continue; }
            if (!s.Ready || e.NativeReservation == 0) continue;
            if (!e.Verified)
            {
                if (!permitCapture) continue;
                try
                {
                    e.Trace?.MarkCapture(); long started = e.Trace?.Start() ?? 0;
                    try { AdvanceSignature(e, verify: true); } finally { e.Trace?.Add(SliceJobStage.Capture, started); }
                }
                catch (Exception error) { Reject(e, "Vessel geometry validation failed: " + error.Message); }
                permitCapture = false;
                if (!e.Verified || e.Retiring) continue;
            }
            if (!BasicCurrent(e) || !e.EvidenceCurrent()) { Reject(e, "Vessel cut became stale: source geometry changed.", SliceJobOutcome.Stale); continue; }
            if (!permitCommit) continue;
            if (!PartSliceTargeting.CheckQueuedSliceTarget(e.Actor, e.Target, out var reason)) { Reject(e, "Vessel cut became stale: " + reason, SliceJobOutcome.Stale); continue; }
            if (!e.Isolated)
            {
                var result = s.Isolate(e.Target.Part); permitCommit = false;
                if (!result.Success) { Reject(e, "Vessel isolation failed: " + result.Message); continue; }
                e.Isolated = true;
                e.Verified = false;
                var isolation = s.Isolation!;
                e.Target = e.Target with { Vehicle = isolation.Owner, PartTransform = isolation.PartTransform,
                    HitAssembly = e.Target.HitAssembly.Transform(isolation.SourceToIsolated) };
                RefreshOwnedNeighbors(e, isolation);
                continue;
            }
            var committed = s.Commit(e.Target.Part, now); permitCommit = false;
            FinishDiagnostic(e, committed.Success ? (s.UsedAuthoredFallback ? SliceJobOutcome.AuthoredFallback : SliceJobOutcome.MeshCut) : SliceJobOutcome.Failed);
            // Replacement can succeed before optional post-commit effects report an error.
            foreach (var part in s.CreatedParts) MarkGenerated(part, e.Stroke);
            Status = (committed.Success ? "Vessel cut complete: " : "Vessel cut failed: ") + committed.Message
                + (!committed.Success && s.CreatedParts.Count == 0 ? " The source may remain detached and empty." : "");
            Retire(e);
        }
        foreach (var e in _entries.Where(e => e.Retiring).ToArray())
        {
            if (e.Slicer is { HasRetired: true }) continue;
            e.Slicer?.Dispose(); _entries.Remove(e);
        }
    }

    private void RefreshOwnedNeighbors(Entry completed, IsolatedSliceSource isolation)
    {
        foreach (var e in _entries.Where(e => !ReferenceEquals(e, completed) && !e.Retiring && e.Slicer == null && ReferenceEquals(e.Group, completed.Group)))
        {
            var owner = e.Target.Part.Tree?.OwningVehicle;
            if (owner == null || (!ReferenceEquals(owner, e.Target.Vehicle) && !isolation.Neighbors.Contains(owner)))
            { Reject(e, "Vessel cut became stale after isolation.", SliceJobOutcome.Stale); continue; }
            var matrix = e.Target.Part.MatrixAsmb2VehicleAsmb;
            e.Target = e.Target with { Vehicle = owner, PartTransform = matrix,
                HitAssembly = ToGame(e.LocalRequest.Anchor).Transform(matrix) };
            // Revalidation consumes future capture-budget slices, never the native transaction frame.
            e.Scan?.Dispose(); e.Scan = null; e.ScanIndex = 0;
            if (e.SignatureReady) e.Verifying = true;
            else { e.Signature.Clear(); e.HitMeshFound = false; }
        }
    }
    private static bool BasicCurrent(Entry e) => ReferenceEquals(e.Target.Part.Tree?.OwningVehicle, e.Target.Vehicle)
        && e.Target.Part.MatrixAsmb2VehicleAsmb.Equals(e.Target.PartTransform)
        && ReferenceEquals(e.Target.Part.Template, e.SourceTemplate) && e.Target.Part.SubParts.Length == e.SubpartCount;
    private static IEnumerable<Node> EnumerateSignature(Entry e)
    {
        var full = e.Target.Part;
        if (!double4x4.Invert(full.MatrixAsmb2VehicleAsmb, out var inverse)) throw new InvalidDataException("Source transform is singular.");
        for (int i = -1; i < e.SubpartCount; i++)
        {
            if (!BasicCurrent(e) || !e.EvidenceCurrent()) throw new InvalidDataException("Source changed during validation.");
            var part = i < 0 ? full : full.SubParts[i];
            var local = part.MatrixAsmb2VehicleAsmb * inverse;
            yield return new(part, local, true, null, null, null, false);
            foreach (var model in SliceSourceCapture.EnumerateModels(part))
            {
                if (!BasicCurrent(e) || !e.EvidenceCurrent()) throw new InvalidDataException("Source changed during validation.");
                var mesh = model.Mesh?.Get();
                yield return new(part, local, false, mesh, mesh?.PositionsCompare, model.Material?.Get(), model.Glass);
            }
        }
    }
    private void AdvanceSignature(Entry e, bool verify)
    {
        e.Scan ??= EnumerateSignature(e).GetEnumerator();
        long started = System.Diagnostics.Stopwatch.GetTimestamp(); int units = 0;
        do
        {
            if (!e.Scan.MoveNext())
            {
                e.Scan.Dispose(); e.Scan = null;
                if (verify && e.ScanIndex != e.Signature.Count) throw new InvalidDataException("Source geometry changed.");
                if (!verify && !e.HitMeshFound) throw new InvalidDataException("The contacted mesh is no longer part of this source.");
                e.ScanIndex = 0; e.SignatureReady = true; e.Verifying = false; e.Verified = verify;
                return;
            }
            var node = e.Scan.Current;
            if (!verify)
            {
                if (ChargedBytes + _otherChargedBytes > _memoryLimit - 128)
                    throw new InvalidDataException("Source validation exceeds SliceWorkingMemoryMiB.");
                e.Signature.Add(node);
                if (ReferenceEquals(node.Mesh, e.HitMesh) && ReferenceEquals(node.Positions, e.HitPrimitives)) e.HitMeshFound = true;
            }
            else
            {
                if (e.ScanIndex >= e.Signature.Count) throw new InvalidDataException("Source geometry changed.");
                var old = e.Signature[e.ScanIndex++];
                if (!ReferenceEquals(node.Part, old.Part) || !SliceSourceFrame.SameTransform(node.LocalTransform, old.LocalTransform)
                    || node.IsPart != old.IsPart || !ReferenceEquals(node.Mesh, old.Mesh) || !ReferenceEquals(node.Positions, old.Positions)
                    || !ReferenceEquals(node.Material, old.Material) || node.Glass != old.Glass)
                    throw new InvalidDataException("Source geometry changed.");
            }
        } while (++units < 8 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1.5);
    }
    private static SlicePlaneRequest TransformRequest(SlicePlaneRequest request, double4x4 matrix)
    {
        CutPoint Point(CutPoint p) => ToPoint(ToGame(p).Transform(matrix));
        CutPoint Direction(CutPoint p) => ToPoint(double3.TransformNormal(ToGame(p), matrix));
        SliceRibbonMotion? ribbon = request.Ribbon is { } r ? new(Point(r.Base0), Point(r.Tip0), Point(r.Base1), Point(r.Tip1), r.HitTime01, r.AlongBlade01) : null;
        return request with { Anchor = Point(request.Anchor), BladeAtHit = Direction(request.BladeAtHit), MotionAtHit = Direction(request.MotionAtHit),
            Ribbon = ribbon, Samples = request.Samples?.Map(Point) };
    }
    private static double3 ToGame(CutPoint p) => new(p.X, p.Y, p.Z);
    private static CutPoint ToPoint(double3 p) => new(p.X, p.Y, p.Z);
    internal void MarkGenerated(Part part, ulong stroke)
    {
        _generated.Mark(part.StructuralPart, stroke);
    }
    internal void ObserveStroke(ulong stroke)
    {
        _generated.Observe(stroke);
    }
    internal void ClearProvenance() { _generated.Clear(); _rejectedUntil.Clear(); }
    private void Reject(Entry e, string reason, SliceJobOutcome outcome = SliceJobOutcome.Failed)
    {
        Status = reason + (e.Isolated ? " The source may remain detached and empty." : "");
        if (double.IsFinite(_now))
        {
            if (_rejectedUntil.Count >= 512)
                foreach (var key in _rejectedUntil.Where(p => p.Value <= _now).Select(p => p.Key).ToArray()) _rejectedUntil.Remove(key);
            if (_rejectedUntil.Count < 512) _rejectedUntil[e.Target.Part] = _now + .25;
        }
        FinishDiagnostic(e, outcome); Retire(e);
    }
    private void FinishDiagnostic(Entry e, SliceJobOutcome outcome)
    {
        if (e.DiagnosticFinished) return;
        e.DiagnosticFinished = true; SliceJobDiagnostics.Finish(e.Trace, outcome, _now); e.Trace = null;
    }
    private static void Retire(Entry e) { e.Scan?.Dispose(); e.Scan = null; e.Slicer?.Cancel(); e.Retiring = true; e.NativeReservation = 0; }
    internal void Cancel() { foreach (var e in _entries) if (!e.Retiring) Reject(e, "Vessel cut cancelled.", SliceJobOutcome.Cancelled); }
    internal void PumpRetired(bool permitUpload)
    {
        bool lane = permitUpload;
        foreach (var e in _entries)
        {
            if (!e.Retiring || e.Slicer == null) continue;
            bool owns = e.Slicer.OwnsUpload;
            e.Slicer.PumpRetired(lane && owns);
            if (owns) lane = false;
        }
        foreach (var e in _entries.Where(e => e.Retiring).ToArray())
        {
            if (e.Slicer is { HasRetired: true }) continue;
            e.Slicer?.Dispose(); _entries.Remove(e);
        }
    }
    public void Dispose() { Cancel(); foreach (var e in _entries) e.Slicer?.Dispose(); _entries.Clear(); _rejectedUntil.Clear(); }
}
