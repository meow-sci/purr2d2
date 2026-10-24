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
        internal ReadOnlySpan<Vehicle> AsSpan() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Items);
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
        internal int Waits;
        internal void Wait() { Waits++; if (ThrowOnWait) throw new InvalidOperationException("fixture join failure"); }
    }
    internal static class JobSystems
    {
        internal static readonly Scheduler OrbitSolvers = new();
        internal static readonly Scheduler NearestOrbitAndPerformanceWorker = new();
        internal static readonly Scheduler VehicleSolver = new();
    }
    internal sealed class Part
    {
        internal PartTree? Tree;
        internal bool IsAttachedInternal = false;
        internal double4x4 MatrixAsmb2VehicleAsmb = double4x4.Identity;
        internal readonly List<Part> SubParts = [];
        internal readonly Modules Modules = new();
    }
    internal sealed class Modules
    {
        internal readonly List<object> Items = [];
        internal IEnumerable<T> Get<T>() => Items.OfType<T>();
    }
    internal sealed class MeshReference { internal double3[][]? PositionsCompare; internal MeshReference Get() => this; }
    internal sealed class ModelTemplate { internal MeshReference? Mesh = null; }
    internal sealed class Model { internal ModelTemplate Template = new(); }
    internal sealed class PartModelModule { internal Model PartModel = new(); }
    internal sealed class PartModelDynamicModule { internal Model PartModelDynamic = new(); }
    internal sealed class PartModelGlassModule { internal Model PartModelGlass = new(); }
    internal sealed class MeshViewModule { internal MeshReference? MeshView; }
    internal sealed class PartTree
    {
        internal readonly List<Part> Items = [];
        internal Vehicle OwningVehicle;
        internal PartTree(Vehicle owner)
        {
            OwningVehicle = owner;
            var part = new Part { Tree = this };
            part.Modules.Items.Add(new MeshViewModule { MeshView = new MeshReference {
                PositionsCompare = [[double3.Zero, new(0, .1, 0), new(0, 0, .1)]] } });
            Items.Add(part);
        }
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
        internal double3 CenterOfMassAsmb = double3.Zero;
        internal double BoundingSphereRadiusBody = 1;
        internal bool HasPhysicsBubble => _threadWorkerUpdateState.Bubble != null;
        internal int PlanWrites;
        internal int FailPlanWrite;
        internal double3[] BreakupOffsets = [];
        internal bool FailBreakupPreparation, FailBreakupCommit, Drained;
        internal int BreakupCommits;
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
        internal static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
    }
}

namespace Purr2D2.Force
{
    using KSA;
    // The controller runs unchanged; native mesh/template construction is checked
    // separately against real KSA. This boundary models irreversible replacement.
    internal sealed class ForceVehicleBreakup(Vehicle source) : IDisposable
    {
        internal static bool NeedsBreakup(Vehicle vehicle) => vehicle.BreakupOffsets.Length > 1;
        internal int Count => source.BreakupOffsets.Length;
        internal void Prepare()
        { if (source.FailBreakupPreparation) throw new InvalidOperationException("fixture breakup preparation failed"); }
        internal Vehicle[] Commit()
        {
            if (source.FailBreakupCommit) throw new InvalidOperationException("fixture breakup commit failed");
            source.BreakupCommits++;
            var pieces = source.BreakupOffsets.Select((offset, i) => new Vehicle(source.System, source.Parent,
                source.Id + "_piece" + i, source.GetPositionCci() + offset.Transform(source.GetBody2Cci()), source.GetVelocityCci())
                { Body2Cce = source.GetBody2Cci(), BodyRates = source.BodyRates }).ToArray();
            source.Drained = source.IsDisposed = true;
            source.System.All.Items.Remove(source);
            return pieces;
        }
        public void Dispose() { }
    }
    internal sealed record ForcePick(Vehicle? Vehicle, Part? Part, ClutterForceTarget? Clutter,
        IParentBody Parent, CelestialSystem System, double3 HitBody, double3 HitCci)
    {
        internal ForcePick(Vehicle vehicle, Part part, IParentBody parent, CelestialSystem system, double3 hitBody)
            : this(vehicle, part, null, parent, system, hitBody,
                vehicle.GetPositionCci() + hitBody.Transform(vehicle.GetBody2Cci())) { }
        internal ForcePick(ClutterForceTarget clutter, double3 hitCci)
            : this(null, null, clutter, clutter.Parent, clutter.System, double3.Zero, hitCci) { }
    }
    // Only the native host boundary is substituted. Production mixed gathering, geometry math,
    // controller and worker filter are compiled unchanged.
    internal static partial class ForceTargeting
    {
        internal const int MaximumVehicles = 2048, MaximumParts = 32768;
        internal static bool IsLive(Vehicle source) => !source.IsDisposed && !source.IsEditedVehicle
            && ReferenceEquals(source.System, Universe.CurrentSystem) && source.System.All.Contains(source);
        internal static bool Valid(KittenEva owner, ForcePick pick) => IsLive(owner)
            && ReferenceEquals(owner, Program.ControlledVehicle) && !Program.EditorFlag
            && ReferenceEquals(pick.Parent, owner.Parent) && ReferenceEquals(pick.System, owner.System)
            && (pick.Vehicle is { } vehicle ? IsLive(vehicle) && ReferenceEquals(pick.Part?.Tree?.OwningVehicle, vehicle)
                : pick.Clutter?.IsValid == true);
    }
    internal readonly record struct ClutterForceIdentity(string Key);
    internal readonly record struct ClutterForceCandidate(ClutterForceTarget Target, double DistanceSquared);
    internal sealed class ClutterForceTarget
    {
        internal readonly IParentBody Parent;
        internal readonly CelestialSystem System;
        internal readonly ClutterForceIdentity StableKey;
        internal string StableOrder => StableKey.Key;
        internal string DisplayName => StableKey.Key;
        internal bool Live = true, Local = true, Captured, FailCapture, FailRelease;
        internal int Captures, Releases, Writes, FailWrite;
        internal ForceRigidState State;
        internal bool IsValid => Live && ReferenceEquals(System, Universe.CurrentSystem);
        internal bool IsLocalTo(KittenEva owner) => Local && IsValid && ReferenceEquals(owner.System, System)
            && ReferenceEquals(owner.Parent, Parent);
        internal ClutterForceTarget(KittenEva owner, string key, double3 position, double3 velocity)
        { Parent = owner.Parent; System = owner.System; StableKey = new(key); State = new(position, velocity, doubleQuat.Identity, double3.Zero); }
        internal bool TryRead(out ForceRigidState state) { state = State; return IsValid; }
        internal bool TryCapture(out ClutterForceHold? hold, out string reason)
        {
            Captures++; reason = "fixture promotion failed"; hold = null;
            if (!IsValid || FailCapture || Captured) return false;
            Captured = true; hold = new(this); return true;
        }
    }
    internal sealed class ClutterForceHold(ClutterForceTarget target) : IDisposable
    {
        private bool _released;
        internal ClutterForceTarget Target => target;
        internal bool IsValid => !_released && target.IsValid && target.Captured;
        internal bool TryRead(out ForceRigidState state) => target.TryRead(out state);
        internal void Write(ForceRigidState state)
        {
            if (!IsValid || ++target.Writes == target.FailWrite) throw new InvalidOperationException("fixture clutter-write failure");
            target.State = state;
        }
        internal bool Release(ForceRigidState? state = null)
        {
            if (_released) return true;
            _released = true; target.Captured = false; target.Releases++;
            if (state.HasValue && target.IsValid) target.State = state.Value;
            return !target.FailRelease;
        }
        public void Dispose() => Release();
    }
    internal static class ClutterForceTargets
    {
        internal static ClutterForceTarget[] Candidates = [];
        internal static bool QueryFails;
        internal static int GatherCalls;
        internal static void Install(HarmonyLib.Harmony harmony) { }
        internal static void Remove(HarmonyLib.Harmony harmony) { }
        internal static bool GatherNear(KittenEva owner, double3 center, double radius,
            List<ClutterForceCandidate> destination, int maximum, out string reason)
        {
            GatherCalls++; destination.Clear(); reason = "fixture native query refused";
            if (QueryFails) return false;
            var seen = new HashSet<ClutterForceIdentity>();
            foreach (var target in Candidates)
            {
                double distanceSquared = (target.State.Position - center).LengthSquared();
                if (distanceSquared <= radius * radius + 1e-9 && seen.Add(target.StableKey))
                {
                    destination.Add(new(target, distanceSquared));
                }
            }
            destination.Sort((left, right) =>
            {
                int distance = left.DistanceSquared.CompareTo(right.DistanceSquared);
                return distance != 0 ? distance
                    : StringComparer.Ordinal.Compare(left.Target.StableKey.Key, right.Target.StableKey.Key);
            });
            if (destination.Count > maximum) destination.RemoveRange(maximum, destination.Count - maximum);
            reason = "";
            return true;
        }
    }
}
