using Brutal.Numerics;
using KSA;
namespace Purr2D2.Cutting;
internal sealed class ArbitrarySlicer
{
    internal int Begins, Advances, Isolations, Commits;
    internal bool Busy = false, Ready;
    internal string? Error = null;
    internal string Phase => "Preparing";
    internal Isolated? Isolation;
    internal sealed record Isolated(Vehicle Owner, double4x4 PartTransform, double4x4 SourceToIsolated);
    internal void Begin(Part part) { Begins++; Ready = false; }
    internal void Advance() => Advances++;
    internal SliceAttempt Isolate(Part part)
    {
        Isolations++;
        var old = part.Tree!.OwningVehicle;
        var vehicle = new Vehicle { Parent = old.Parent };
        old.Parts.Parts.Remove(part); part.Tree = vehicle.Parts; vehicle.Parts.Parts.Add(part);
        Universe.CurrentSystem!.All.Items = [..Universe.CurrentSystem.All.Items, vehicle];
        Isolation = new(vehicle, part.MatrixAsmb2VehicleAsmb, double4x4.Identity);
        return new(true, "isolated");
    }
    internal SliceAttempt Commit(Part part) { Commits++; return new(true, "committed"); }
    internal void Cancel() { Ready = false; Isolation = null; }
}
