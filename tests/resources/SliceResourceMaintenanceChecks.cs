using KSA;
using Purr2D2.Slicing;

internal static class SliceResourceMaintenanceChecks
{
    public static void Main()
    {
        EligibilityAndManagedCounts();
        CameraAndRegistrationProtection();
        SnapshotDestruction();
        MutationRevalidation();
        WorldChanges();
        FailureSummaries();
        Console.WriteLine("Slice resource maintenance checks passed (generated-only metadata filtering, crew/control/cameras, joined snapshot destruction, revalidation and world changes).");
    }

    private static void EligibilityAndManagedCounts()
    {
        var f = new Fixture();
        _ = f.Generated("eligible", 2);
        var emptySeats = f.Generated("empty seats");
        emptySeats.Seats.Add(new() { AssignedKittenHash = KeyHash.Zero });
        _ = f.Vehicle("stock", "Stock_Tank");
        _ = f.Vehicle("mixed", "Purr2D2_Fragment_half", "Stock_AttachedVessel");
        _ = f.Vehicle("wrong case", "purr2d2_fragment_not_generated");
        f.Generated("not debris").IsDebris = false;
        KSA.Program.ControlledVehicle = f.Generated("controlled");
        f.Generated("crewed").Seats.Add(new() { AssignedKittenHash = new(7) });
        var kitten = new KittenEva(f.System, "kitten");
        AddParts(kitten, "Purr2D2_Fragment_kitten");
        f.Generated("disposed").IsDisposed = true;
        f.Generated("edited").IsEditedVehicle = true;
        f.Generated("disposed tree").Parts.IsDisposed = true;
        var empty = f.Generated("empty"); empty.Parts.Items.Clear();
        var noTemplate = f.Generated("missing template"); noTemplate.Parts.Items[0].Template = null;
        var foreignTree = f.Generated("foreign tree"); foreignTree.Parts.Items[0].Tree = emptySeats.Parts;
        var wrongOwner = f.Generated("wrong owner"); wrongOwner.Parts.OwningVehicle = emptySeats;
        var wrongSystem = f.Generated("foreign system"); wrongSystem.System = new();
        Check(SliceResourceMaintenance.CountEligible(f.System) == 2,
            "only uncrewed generated debris with every full part generated is eligible; empty seats are safe");
        Check(SliceResourceMaintenance.CountEligible(null) == 0
            && SliceResourceMaintenance.CountEligible(new CelestialSystem()) == 0, "missing/stale systems have no eligible count");
        Check(JobSystems.NearestOrbitAndPerformanceWorker.Waits == 0 && JobSystems.OrbitSolvers.Waits == 0
            && JobSystems.VehicleSolver.Waits == 0 && Universe.Destroyed.Count == 0,
            "UI counting only reads managed metadata and cannot join workers or destroy native objects");
    }

    private static void CameraAndRegistrationProtection()
    {
        var f = new Fixture();
        var safe = f.Generated("safe");
        var baseFollowed = f.Generated("base camera");
        var mapFollowed = f.Generated("secondary map camera");
        var first = new GameViewport(); first.BaseCamera.Following = baseFollowed;
        var second = new GameViewport(); second.MapCamera.Following = mapFollowed;
        ViewportRegistry.Items = [first, second];
        var oldIdentity = f.Generated("replaced identity");
        var registered = f.Generated("replaced identity");
        // Keep the stale object in the enumeration but register the exact new identity first.
        f.System.All.Items.Remove(registered); f.System.All.Items.Insert(0, registered);
        Check(SliceResourceMaintenance.CountEligible(f.System) == 2,
            "both cameras in every viewport protect followed debris and a stale duplicate identity is ignored");
        string summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary.StartsWith("Removed 2 ", StringComparison.Ordinal)
            && Universe.Destroyed.Contains(safe) && Universe.Destroyed.Contains(registered)
            && !Universe.Destroyed.Contains(oldIdentity) && !baseFollowed.IsDisposed && !mapFollowed.IsDisposed,
            "cleanup preserves followed vessels and destroys only exact registered identities");
    }

    private static void SnapshotDestruction()
    {
        var f = new Fixture();
        var first = f.Generated("first"); var second = f.Generated("second"); var third = f.Generated("third");
        var stock = f.Vehicle("stock", "Stock_Payload");
        string summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary == "Removed 3 generated debris vessels." && Universe.Destroyed.SequenceEqual([first, second, third]),
            "all snapshotted candidates are removed despite the registry shifting after every native destruction");
        Check(f.System.All.Items.SequenceEqual([stock]) && !stock.IsDisposed,
            "native cleanup leaves unrelated stock objects registered");
        Check(JobSystems.NearestOrbitAndPerformanceWorker.Waits == 1 && JobSystems.OrbitSolvers.Waits == 1
            && JobSystems.VehicleSolver.Waits == 0,
            "independent native readers join once; the caller owns the settled vehicle-worker handoff");
        Check(SliceResourceMaintenance.CountEligible(f.System) == 0, "removed debris no longer contributes to counts");
    }

    private static void MutationRevalidation()
    {
        var f = new Fixture();
        var first = f.Generated("first");
        var noLongerDebris = f.Generated("no longer debris");
        var crewed = f.Generated("new crew"); var controlled = f.Generated("new control");
        var followed = f.Generated("new camera"); var mixed = f.Generated("new stock part");
        var replaced = f.Generated("replaced");
        Vehicle? replacement = null;
        Universe.OnDestroyed = vehicle =>
        {
            if (!ReferenceEquals(vehicle, first)) return;
            noLongerDebris.IsDebris = false;
            crewed.Seats.Add(new() { AssignedKittenHash = new(3) });
            KSA.Program.ControlledVehicle = controlled;
            var viewport = new GameViewport(); viewport.MapCamera.Following = followed;
            ViewportRegistry.Items = [viewport];
            AddParts(mixed, "Stock_DockedVehicle");
            f.System.All.Items.Remove(replaced);
            replacement = f.Generated("replaced");
        };
        string summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary == "Removed 1 generated debris vessel." && Universe.Destroyed.SequenceEqual([first]),
            "every candidate revalidates debris, crew, control, cameras, attached stock and exact registration before mutation");
        Check(replacement is { IsDisposed: false } && SliceResourceMaintenance.CountEligible(f.System) == 1,
            "objects added during cleanup wait for a separate confirmed action rather than joining its original snapshot");
    }

    private static void WorldChanges()
    {
        var f = new Fixture();
        _ = f.Generated("old world");
        Universe.CurrentSystem = new();
        Check(SliceResourceMaintenance.ClearGeneratedDebris(f.System).Contains("system changed", StringComparison.Ordinal)
            && Universe.Destroyed.Count == 0 && JobSystems.OrbitSolvers.Waits == 0,
            "a queued confirmation for an old world cannot join or destroy in the new world");

        f = new(); _ = f.Generated("join change");
        JobSystems.NearestOrbitAndPerformanceWorker.OnWait = () => Universe.CurrentSystem = new();
        Check(SliceResourceMaintenance.ClearGeneratedDebris(f.System).Contains("system changed", StringComparison.Ordinal)
            && Universe.Destroyed.Count == 0, "world identity revalidates after native reader joins");

        f = new(); var first = f.Generated("first"); var second = f.Generated("second");
        Universe.OnDestroyed = _ => Universe.CurrentSystem = new();
        string summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary.StartsWith("Removed 1 ", StringComparison.Ordinal) && summary.Contains("system changed", StringComparison.Ordinal)
            && Universe.Destroyed.SequenceEqual([first]) && !second.IsDisposed,
            "a world change inside native destruction stops the remaining snapshot immediately and reports partial progress");
    }

    private static void FailureSummaries()
    {
        var f = new Fixture();
        Check(SliceResourceMaintenance.ClearGeneratedDebris(f.System) == "No eligible generated debris to clear.",
            "an empty cleanup reports a clear no-work result");
        f = new(); var protectedDebris = f.Generated("join failure");
        JobSystems.OrbitSolvers.ThrowOnWait = true;
        string summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary.StartsWith("Removed 0 ", StringComparison.Ordinal) && summary.Contains("reader join failed", StringComparison.Ordinal)
            && !protectedDebris.IsDisposed && Universe.Destroyed.Count == 0,
            "a failed native reader join prevents all destruction and reports the reason");
        f = new(); var first = f.Generated("first"); var failure = f.Generated("failure"); var last = f.Generated("last");
        Universe.FailDestroyId = failure.Id;
        summary = SliceResourceMaintenance.ClearGeneratedDebris(f.System);
        Check(summary.StartsWith("Removed 1 ", StringComparison.Ordinal) && summary.Contains("destruction failed", StringComparison.Ordinal)
            && Universe.Destroyed.SequenceEqual([first]) && !failure.IsDisposed && !last.IsDisposed,
            "native destruction failure stops further changes and reports the completed removal count");
    }

    private sealed class Fixture
    {
        internal readonly CelestialSystem System = new();
        internal Fixture()
        {
            Universe.CurrentSystem = System; Universe.Destroyed.Clear(); Universe.OnDestroyed = null; Universe.FailDestroyId = null;
            KSA.Program.ControlledVehicle = null; ViewportRegistry.Items = [];
            JobSystems.NearestOrbitAndPerformanceWorker.Reset(); JobSystems.OrbitSolvers.Reset(); JobSystems.VehicleSolver.Reset();
        }
        internal Vehicle Vehicle(string id, params string[] templates)
        { var vehicle = new Vehicle(System, id); AddParts(vehicle, templates); return vehicle; }
        internal Vehicle Generated(string id, int parts = 1) => Vehicle(id,
            Enumerable.Range(0, parts).Select(index => "Purr2D2_Fragment_" + id + "_" + index).ToArray());
    }

    private static void AddParts(Vehicle vehicle, params string[] templates)
    {
        foreach (var id in templates) vehicle.Parts.Items.Add(new() { Tree = vehicle.Parts, Template = new(id) });
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Slice maintenance regression: " + message); }
}
