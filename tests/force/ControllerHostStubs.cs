using Brutal.Numerics;
using System.Runtime.CompilerServices;

namespace KSA
{
    internal readonly record struct UniverseTime(double Value);
    internal readonly record struct SimStep(UniverseTime PreviousTime, UniverseTime NextTime, double DeltaTime);
    internal interface IParentBody { doubleQuat GetCci2Cce(); }
    internal sealed class ParentBody : IParentBody { public doubleQuat GetCci2Cce() => doubleQuat.Identity; }
    internal sealed class CelestialSystem { internal readonly Registry All = new(); }
    internal sealed class Registry
    {
        internal readonly List<Vehicle> Items = [];
        internal bool Contains(Vehicle vehicle) => Items.Contains(vehicle);
    }
    internal static class Program
    {
        internal static Vehicle? ControlledVehicle;
        internal static bool EditorFlag;
    }
    internal static class Universe
    {
        internal static CelestialSystem? CurrentSystem;
        internal static double Time;
        internal static SimStep GetLastSimStep() => new(new(Time), new(Time), 0);
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void PrepareVehicleWorkers(SimStep step)
        {
            foreach (var vehicle in CurrentSystem!.All.Items)
            {
                vehicle.PrepareWorker(step);
                var worker = vehicle.TakeUpdateStateIfOrphaned();
                if (worker != null) worker.Adoptions++;
            }
        }
    }
    internal sealed class Scheduler
    {
        internal bool ThrowOnWait;
        internal void Wait() { if (ThrowOnWait) throw new InvalidOperationException("fixture join failure"); }
    }
    internal static class JobSystems
    {
        internal static readonly Scheduler OrbitSolvers = new();
        internal static readonly Scheduler NearestOrbitAndPerformanceWorker = new();
    }
    internal sealed class Part
    {
        internal PartTree? Tree;
    }
    internal sealed class PartTree
    {
        internal readonly List<Part> Items = [];
        internal Vehicle OwningVehicle;
        internal PartTree(Vehicle owner) { OwningVehicle = owner; Items.Add(new() { Tree = this }); }
        internal ReadOnlySpan<Part> Parts => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Items);
    }
    internal readonly record struct StateVectors(UniverseTime StateTime, double3 PositionCci, double3 VelocityCci);
    internal sealed class Orbit
    {
        internal IParentBody Parent;
        internal StateVectors StateVectors;
        internal byte4 OrbitLineColor;
        private Orbit(IParentBody parent, UniverseTime time, double3 position, double3 velocity, byte4 color)
        { Parent = parent; StateVectors = new(time, position, velocity); OrbitLineColor = color; }
        internal static Orbit CreateFromStateCci(IParentBody parent, UniverseTime time, double3 position, double3 velocity, byte4 color) =>
            new(parent, time, position, velocity, color);
    }
    internal sealed class FlightPlan
    {
        internal readonly Orbit Orbit;
        internal FlightPlan(Orbit orbit, int hash) { Orbit = orbit; }
    }
    internal sealed class PhysicsBubble;
    internal sealed class VehicleUpdateState
    {
        internal Vehicle ReadOnlyVehicle;
        internal PhysicsBubble? Bubble = new();
        internal int Adoptions;
        internal VehicleUpdateState(Vehicle vehicle) { ReadOnlyVehicle = vehicle; }
    }
    internal class Vehicle
    {
        private readonly VehicleUpdateState _threadWorkerUpdateState;
        internal CelestialSystem System;
        internal IParentBody Parent;
        internal PartTree Parts;
        internal string Id;
        internal int Hash = 0;
        internal bool IsDisposed = false;
        internal bool IsEditedVehicle = false;
        internal FlightPlan FlightPlan;
        internal Orbit Orbit => FlightPlan.Orbit;
        internal doubleQuat Body2Cce = doubleQuat.Identity;
        internal double3 BodyRates;
        internal bool HasPhysicsBubble => _threadWorkerUpdateState.Bubble != null;
        internal int PlanWrites;
        internal int FailPlanWrite;
        internal Vehicle(CelestialSystem system, IParentBody parent, string id, double3 position, double3 velocity)
        {
            System = system; Parent = parent; Id = id;
            FlightPlan = new(Orbit.CreateFromStateCci(parent, new(0), position, velocity, default), Hash);
            Parts = new(this); _threadWorkerUpdateState = new(this); system.All.Items.Add(this);
        }
        internal double3 GetPositionCci() => Orbit.StateVectors.PositionCci;
        internal double3 GetVelocityCci() => Orbit.StateVectors.VelocityCci;
        internal doubleQuat GetBody2Cci() => Body2Cce;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void PrepareWorker(SimStep step) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal VehicleUpdateState? TakeUpdateStateIfOrphaned() => HasPhysicsBubble ? null : _threadWorkerUpdateState;
        internal void RemoveFromBubble(PhysicsBubble bubble) { _threadWorkerUpdateState.Bubble = null; }
        internal void SetFlightPlan(FlightPlan plan)
        {
            if (++PlanWrites == FailPlanWrite) throw new InvalidOperationException("fixture plan-write failure: " + Id);
            FlightPlan = plan;
        }
        internal void Teleport(Orbit? orbit, doubleQuat? rotation, double3? rates)
        {
            if (orbit != null) throw new InvalidOperationException("Force must not use expensive forecasting teleport");
            if (rotation.HasValue) Body2Cce = rotation.Value;
            if (rates.HasValue) BodyRates = rates.Value;
        }
        internal void UpdatePerFrameData() { }
        internal void SetNativeState(double3 position, double3 velocity, doubleQuat rotation, double3 rates)
        {
            FlightPlan = new(Orbit.CreateFromStateCci(Parent, new(Universe.Time), position, velocity, default), Hash);
            Body2Cce = rotation; BodyRates = rates;
        }
    }
    internal sealed class KittenEva(CelestialSystem system, IParentBody parent, string id, double3 position, double3 velocity)
        : Vehicle(system, parent, id, position, velocity);
    internal static class NumericExtensions
    {
        internal static doubleQuat NormalizedOrZero(this doubleQuat value) => doubleQuat.NormalizeOrZero(value);
        internal static doubleQuat Inverse(this doubleQuat value) => doubleQuat.Inverse(value);
        internal static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
}

namespace Purr2D2.Force
{
    using KSA;
    internal sealed record ForcePick(Vehicle Vehicle, Part Part, IParentBody Parent, CelestialSystem System, double3 HitBody);
    // Deliberately substitutes only native selection. The actual controller/physics filter are compiled unchanged.
    internal static class ForceTargeting
    {
        internal static Vehicle[] Candidates = [];
        internal static bool IsLive(Vehicle source) => !source.IsDisposed && !source.IsEditedVehicle
            && ReferenceEquals(source.System, Universe.CurrentSystem) && source.System.All.Contains(source);
        internal static bool Gather(KittenEva owner, ForcePick pick, bool angry, List<Vehicle> result, out string reason)
        {
            reason = "stale fixture pick";
            if (!IsLive(owner) || !IsLive(pick.Vehicle) || !ReferenceEquals(owner, Program.ControlledVehicle)
                || !ReferenceEquals(pick.Parent, owner.Parent) || !ReferenceEquals(pick.Part.Tree?.OwningVehicle, pick.Vehicle)) return false;
            result.Add(pick.Vehicle);
            if (angry) foreach (var item in Candidates) if (!ReferenceEquals(item, pick.Vehicle)) result.Add(item);
            return true;
        }
    }
}
