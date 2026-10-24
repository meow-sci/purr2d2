using KSA;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

/// <summary>Remove only expendable generated wreckage; registered assets and save dependencies stay intact.</summary>
internal static class SliceResourceMaintenance
{
    /// <summary>UI-safe managed metadata only: no worker joins, native physics or resource disposal.</summary>
    internal static int CountEligible(CelestialSystem? system)
    {
        if (system == null || !ReferenceEquals(system, Universe.CurrentSystem)) return 0;
        int count = 0;
        foreach (var item in system.All.AsSpan())
            if (item is Vehicle vehicle && Eligible(vehicle, system)) count++;
        return count;
    }

    /// <summary>Call only from the settled handoff after vehicle workers have joined.</summary>
    internal static string ClearGeneratedDebris(CelestialSystem expectedSystem)
    {
        if (expectedSystem == null || !ReferenceEquals(expectedSystem, Universe.CurrentSystem))
            return "Generated debris was not cleared because the current system changed.";
        int removed = 0;
        try
        {
            // These readers can retain vehicle references independently of the vehicle solver.
            JobSystems.NearestOrbitAndPerformanceWorker.Wait();
            JobSystems.OrbitSolvers.Wait();
            if (!ReferenceEquals(expectedSystem, Universe.CurrentSystem))
                return "Generated debris was not cleared because the current system changed.";

            // DestroyVehicle removes from the registry. Never mutate its live span while enumerating.
            var candidates = new List<Vehicle>();
            foreach (var item in expectedSystem.All.AsSpan())
                if (item is Vehicle vehicle && Eligible(vehicle, expectedSystem)) candidates.Add(vehicle);
            foreach (var vehicle in candidates)
            {
                if (!ReferenceEquals(expectedSystem, Universe.CurrentSystem))
                    return Summary(removed) + " Cleanup stopped because the current system changed.";
                // Destruction callbacks can change later candidates, ownership or camera focus.
                if (!Eligible(vehicle, expectedSystem)) continue;
                Universe.DestroyVehicle(vehicle);
                removed++;
            }
            return candidates.Count == 0 ? "No eligible generated debris to clear." : Summary(removed);
        }
        catch (Exception error)
        {
            return Summary(removed) + " Cleanup stopped: " + error.Message;
        }
    }

    private static bool Eligible(Vehicle vehicle, CelestialSystem system)
    {
        if (!ReferenceEquals(system, Universe.CurrentSystem) || vehicle.IsDisposed || vehicle is KittenEva
            || !vehicle.IsDebris || vehicle.IsEditedVehicle || ReferenceEquals(vehicle, Program.ControlledVehicle)
            || !ReferenceEquals(vehicle.System, system)
            || !system.All.TryGet(vehicle.Id, out var registered) || !ReferenceEquals(registered, vehicle)
            || vehicle.Parts is not { IsDisposed: false } tree || !ReferenceEquals(tree.OwningVehicle, vehicle)) return false;
        foreach (var seat in vehicle.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero) return false;
        foreach (var viewport in ViewportRegistry.GameViews)
            if (ReferenceEquals(viewport.BaseCamera.Following, vehicle)
                || ReferenceEquals(viewport.MapCamera.Following, vehicle)) return false;
        if (tree.Parts.Length == 0) return false;
        // A stock part docked or attached to generated wreckage makes the whole vessel ineligible.
        // Include internal full parts as well: never infer that a mixed vessel is disposable.
        foreach (var part in tree.Parts)
            if (!ReferenceEquals(part.Tree, tree) || part.Template?.Id is not { } id
                || !RuntimeFragmentAssets.IsGeneratedTemplate(id)) return false;
        return true;
    }

    private static string Summary(int removed) => $"Removed {removed} generated debris {(removed == 1 ? "vessel" : "vessels")}.";
}
