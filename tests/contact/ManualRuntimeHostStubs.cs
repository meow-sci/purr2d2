using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
namespace Purr2D2.Cutting;
internal sealed class ArbitrarySlicer
{
    internal static int Instances, CaptureOperations, UploadOperations, CommitOperations, UploadRetirements;
    internal static List<Part>? NextCreatedParts;
    internal static Part? NextIsolationMovedPart;
    internal static Vehicle? LastIsolationNeighbor;
    internal static ArbitrarySlicer? LastInstance;
    internal static bool LastAdvanceCapture, LastAdvanceUpload, LastAdvanceLoadSlot;
    internal int Begins, Advances, Isolations, Commits;
    internal bool Busy { get; set; }
    internal bool HasRetired => Busy || _retiredUpload;
    internal bool Ready;
    internal string? Error = null;
    internal string Phase => "Preparing";
    internal Purr2D2.Slicing.IsolatedSliceSource? Isolation;
    internal ContactSlice? LastContact;
    internal Part? LastPart;
    internal bool NeedsCapture { get; private set; }
    internal bool EstimateDone { get; private set; }
    internal bool CanAcquireUpload => _captured && !OwnsUpload;
    internal bool OwnsUpload { get; private set; }
    internal bool HoldsLoadSlot { get; private set; }
    internal bool UsedAuthoredFallback => false;
    internal SliceJobTrace? Trace { get; set; }
    internal int ExpectedFragments => 2;
    internal long EstimatedBytes => 1024;
    internal long RetainedBytes => _captured ? 1024 : 0;
    internal IReadOnlyList<Part> CreatedParts { get; private set; } = [];
    private bool _captured, _retiredUpload;

    internal ArbitrarySlicer() { Instances++; LastInstance = this; }
    internal ArbitrarySlicer(TaskScheduler scheduler) { Instances++; LastInstance = this; }
    internal ArbitrarySlicer(TaskScheduler scheduler, TaskScheduler loaderScheduler) { Instances++; LastInstance = this; }
    internal static void ResetQueueFixture()
    {
        Instances = CaptureOperations = UploadOperations = CommitOperations = UploadRetirements = 0;
        NextCreatedParts = null; NextIsolationMovedPart = null; LastIsolationNeighbor = null; LastInstance = null;
        LastAdvanceCapture = LastAdvanceUpload = LastAdvanceLoadSlot = false;
    }
    internal Action<Part, double3, double3, double>? CutSparks;

    internal void Begin(Part part, ContactSlice? contact = null,
        Purr2D2.Slicing.Planes.SlicePlaneRequest? planeRequest = null, SliceGeometryBudget? geometry = null,
        string? diagnosticDetector = null, double diagnosticTime = double.NaN)
    { Begins++; LastPart = part; LastContact = contact; Ready = false; NeedsCapture = true; EstimateDone = false; }
    internal void FinishSerialDiagnostic(SliceJobOutcome outcome, double now) { }
    internal void Advance() => Advances++;
    internal void AdvanceEstimate() => EstimateDone = true;
    internal void Refuse(string reason) { Error = reason; NeedsCapture = false; }
    internal void Advance(bool capture, bool upload) => Advance(capture, upload, true);
    internal void Advance(bool capture, bool upload, bool loadSlot)
    {
        Advances++;
        LastAdvanceCapture = capture; LastAdvanceUpload = upload; LastAdvanceLoadSlot = loadSlot;
        if (capture && NeedsCapture) { NeedsCapture = false; _captured = true; CaptureOperations++; }
        if (_captured && loadSlot) HoldsLoadSlot = true;
        if (upload && CanAcquireUpload) { OwnsUpload = true; Ready = true; UploadOperations++; }
    }
    internal SliceAttempt Isolate(Part part)
    {
        Isolations++;
        var old = part.Tree!.OwningVehicle;
        var vehicle = new Vehicle { Parent = old.Parent };
        old.Parts.Parts.Remove(part); part.Tree = vehicle.Parts; vehicle.Parts.Parts.Add(part);
        Universe.CurrentSystem!.All.Items = [..Universe.CurrentSystem.All.Items, vehicle];
        if (NextIsolationMovedPart is { } moved)
        {
            var neighbor = new Vehicle { Parent = old.Parent }; LastIsolationNeighbor = neighbor;
            old.Parts.Parts.Remove(moved); moved.Tree = neighbor.Parts; neighbor.Parts.Parts.Add(moved);
            Universe.CurrentSystem.All.Items = [..Universe.CurrentSystem.All.Items, neighbor];
            NextIsolationMovedPart = null;
            Isolation = new(part, vehicle, part.MatrixAsmb2VehicleAsmb, double4x4.Identity) { Neighbors = [old, vehicle, neighbor] };
        }
        else Isolation = new(part, vehicle, part.MatrixAsmb2VehicleAsmb, double4x4.Identity) { Neighbors = [old, vehicle] };
        return new(true, "isolated");
    }
    internal SliceAttempt Commit(Part part) => Commit(part, 0);
    internal SliceAttempt Commit(Part part, double now)
    {
        Commits++; CommitOperations++; CreatedParts = NextCreatedParts ?? []; NextCreatedParts = null;
        return new(true, "committed");
    }
    internal void PumpRetired(bool permitUpload)
    {
        if (_retiredUpload && permitUpload) { _retiredUpload = false; OwnsUpload = false; HoldsLoadSlot = false; UploadRetirements++; }
    }
    internal void Cancel() { Ready = false; NeedsCapture = false; if (OwnsUpload) _retiredUpload = true; }
    internal void Dispose() { OwnsUpload = false; _retiredUpload = false; }
}
