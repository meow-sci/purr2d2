using System.Runtime.InteropServices;

// Only native host boundaries are substituted; the cleanup policy and mutation
// loop compile directly from production SliceResourceMaintenance.cs.
namespace KSA
{
    internal readonly record struct KeyHash(int Value) { internal static KeyHash Zero => new(0); }
    internal sealed class IVASeat { internal KeyHash AssignedKittenHash; }
    internal sealed class PartTemplate(string id) { internal string Id = id; }
    internal sealed class Part { internal PartTree? Tree; internal PartTemplate? Template; }
    internal sealed class PartTree(Vehicle owner)
    {
        internal bool IsDisposed = false;
        internal Vehicle? OwningVehicle = owner;
        internal readonly List<Part> Items = [];
        internal ReadOnlySpan<Part> Parts => CollectionsMarshal.AsSpan(Items);
    }
    internal class Vehicle
    {
        internal string Id;
        internal CelestialSystem System;
        internal bool IsDisposed, IsDebris = true, IsEditedVehicle;
        internal PartTree Parts;
        internal readonly List<IVASeat> Seats = [];
        internal ReadOnlySpan<IVASeat> Crew => CollectionsMarshal.AsSpan(Seats);
        internal Vehicle(CelestialSystem system, string id)
        { System = system; Id = id; Parts = new(this); system.All.Items.Add(this); }
    }
    internal sealed class KittenEva(CelestialSystem system, string id) : Vehicle(system, id);
    internal sealed class CelestialSystem { internal readonly Registry All = new(); }
    internal sealed class Registry
    {
        internal readonly List<object> Items = [];
        internal ReadOnlySpan<object> AsSpan() => CollectionsMarshal.AsSpan(Items);
        internal bool TryGet(string id, out object? registered)
        {
            registered = Items.OfType<Vehicle>().FirstOrDefault(vehicle => vehicle.Id == id);
            return registered != null;
        }
    }
    internal sealed class Camera { internal object? Following; }
    internal sealed class GameViewport
    {
        internal readonly Camera BaseCamera = new(), MapCamera = new();
    }
    internal static class ViewportRegistry
    {
        internal static GameViewport[] Items = [];
        internal static ReadOnlySpan<GameViewport> GameViews => Items;
    }
    internal static class Program { internal static Vehicle? ControlledVehicle; }
    internal sealed class Scheduler
    {
        internal int Waits;
        internal bool ThrowOnWait;
        internal Action? OnWait;
        internal void Wait()
        {
            Waits++;
            OnWait?.Invoke();
            if (ThrowOnWait) throw new InvalidOperationException("fixture reader join failed");
        }
        internal void Reset() { Waits = 0; ThrowOnWait = false; OnWait = null; }
    }
    internal static class JobSystems
    {
        internal static readonly Scheduler NearestOrbitAndPerformanceWorker = new(), OrbitSolvers = new(), VehicleSolver = new();
    }
    internal static class Universe
    {
        internal static CelestialSystem? CurrentSystem;
        internal static readonly List<Vehicle> Destroyed = [];
        internal static Action<Vehicle>? OnDestroyed;
        internal static string? FailDestroyId;
        internal static void DestroyVehicle(Vehicle vehicle)
        {
            if (JobSystems.NearestOrbitAndPerformanceWorker.Waits != 1 || JobSystems.OrbitSolvers.Waits != 1)
                throw new InvalidOperationException("native registry destruction ran without exactly one reader join");
            if (vehicle.Id == FailDestroyId) throw new InvalidOperationException("fixture destruction failed");
            vehicle.IsDisposed = true;
            vehicle.Parts.IsDisposed = true;
            vehicle.System.All.Items.Remove(vehicle);
            Destroyed.Add(vehicle);
            OnDestroyed?.Invoke(vehicle);
        }
    }
}

namespace Purr2D2.Rendering
{
    // This is the sole fragment backend seam used by maintenance. No disk,
    // template unregister, mesh arena or Vulkan disposal API is exposed here.
    internal static class RuntimeFragmentAssets
    {
        internal static bool IsGeneratedTemplate(string id) => id.StartsWith("Purr2D2_Fragment_", StringComparison.Ordinal);
    }
}
