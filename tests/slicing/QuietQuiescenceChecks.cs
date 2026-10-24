using System.Reflection;
using KSA;
using Purr2D2.Slicing;

internal static class QuietQuiescenceChecks
{
    private static void Main()
    {
        var source = new VehicleUpdateState(); var neighbor = new VehicleUpdateState();
        var update = typeof(QuietSliceQuiescence).GetMethod("UpdateModules", BindingFlags.Static | BindingFlags.NonPublic)!;
        QuietSliceQuiescence.Hold(source);
        update.Invoke(null, [source, new SimStep()]);
        Check(source.ModuleTicks == 0 && source.NumActiveNozzles == 0 && source.UpdateData.RocketNozzles.Values.All(n => n.Performance.TotalThrust == 0
            && n.Performance.MassFlowRate == 0 && n.ThrustFraction == 0 && n.ThrustTime == 0),
            "held source skips functional modules and clears stale firing nozzle performance before active-nozzle selection");
        update.Invoke(null, [neighbor, new SimStep()]);
        Check(neighbor.ModuleTicks == 1 && neighbor.UpdateData.RocketNozzles.Values[0].Performance.TotalThrust == 500,
            "neighboring modules and nozzle performance remain untouched");
        QuietSliceQuiescence.Release(source);
        update.Invoke(null, [source, new SimStep()]);
        Check(source.ModuleTicks == 1, "cancelled source releases only its owned functional tick hold");
        QuietSliceQuiescence.Hold(source); QuietSliceQuiescence.Clear();
        update.Invoke(null, [source, new SimStep()]);
        Check(source.ModuleTicks == 2, "world teardown releases all worker identity references");
        QuietSliceQuiescence.WatchFirstStep([source]);
        var before = typeof(QuietSliceQuiescence).GetMethod("DetectionPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        var after = typeof(QuietSliceQuiescence).GetMethod("DetectionPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
        object[] arguments = [source, false]; before.Invoke(null, arguments);
        var scheduled = new PartFailureEvent(); source.PartFailureEvent = scheduled;
        after.Invoke(null, [source, arguments[1]]);
        QuietSliceQuiescence.BeginSettledUpdate();
        Check(ReferenceEquals(source.PartFailureEvent, scheduled), "first-step failure observations preserve deferred native damage events");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
namespace KSA
{
    internal readonly struct SimStep { }
    internal sealed class Vehicle { internal string Id = "fixture"; }
    internal struct RocketPerformance
    {
        internal double TotalThrust, MassFlowRate;
        internal static readonly RocketPerformance Zero = new();
    }
    internal struct RocketNozzleState
    {
        internal RocketPerformance Performance;
        internal double ThrustFraction, ThrustTime;
        internal bool ActivatedThisFrame, DeactivatedThisFrame;
    }
    internal sealed class Nozzles
    {
        internal RocketNozzleState[] Values = [new() { Performance = new() { TotalThrust = 500, MassFlowRate = 2 }, ThrustFraction = 1, ThrustTime = 3 }];
        internal Span<RocketNozzleState> GetNewStates() => Values;
    }
    internal sealed class VehicleUpdateData { internal Nozzles RocketNozzles = new(); }
    internal sealed class VehicleUpdateState
    {
        internal Vehicle ReadOnlyVehicle = new();
        internal VehicleUpdateData UpdateData = new();
        internal int ModuleTicks, NumActiveNozzles = 1;
        internal PartFailureEvent? PartFailureEvent = null;
        internal object? DestructionEvent = null;
        internal void UpdateModules(SimStep step) => ModuleTicks++;
    }
    internal sealed class PartFailureEvent { internal void Apply(Vehicle vehicle) { } }
    internal static class PartFailure { internal static void Detect(VehicleUpdateState state) { } }
    internal sealed class PhysicsBubble { }
}
