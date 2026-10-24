using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

namespace Purr2D2.Slicing;

internal static class QuietSliceQuiescence
{
    // Published only at the joined handoff; workers read an immutable list.
    private static VehicleUpdateState[] _held = [];
    private static VehicleUpdateState[] _watched = [];

    internal static void Install(Harmony harmony)
    {
        // UpdateModules is AggressiveInlining. Patch the actual native callers
        // rather than depending on a prefix that optimized callers can bypass.
        var job = typeof(PhysicsBubble).GetNestedType("UpdateModulesJob", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("PhysicsBubble.UpdateModulesJob");
        MethodInfo[] callers = [Require(job, "ExecuteOnBubble"), Require(typeof(PhysicsBubble), "ApplySingleVehicleMotion")];
        int total = callers.Sum(m => PatchProcessor.GetOriginalInstructions(m).Count(c => c.Calls(ModuleTick)));
        if (total != 3) throw new InvalidOperationException("Quiet slicing cannot safely locate all three native module tick calls.");
        foreach (var caller in callers)
            harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(QuietSliceQuiescence), nameof(ReplaceModuleCalls)));
        harmony.Patch(AccessTools.Method(typeof(PartFailureEvent), nameof(PartFailureEvent.Apply)),
            prefix: new HarmonyMethod(typeof(QuietSliceQuiescence), nameof(FailurePrefix)));
        var structuralFailure = AccessTools.Method(typeof(PhysicsBubble), "DetectStructuralFailure", [typeof(VehicleUpdateState), typeof(SimStep)])
            ?? throw new MissingMethodException("PhysicsBubble.DetectStructuralFailure(VehicleUpdateState, SimStep)");
        foreach (var method in new[] { Require(typeof(PartFailure), nameof(PartFailure.Detect)), structuralFailure })
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(QuietSliceQuiescence), nameof(DetectionPrefix)),
                postfix: new HarmonyMethod(typeof(QuietSliceQuiescence), nameof(DetectionPostfix)));
    }

    internal static void Hold(VehicleUpdateState state)
    {
        if (Volatile.Read(ref _held).Contains(state)) return;
        Volatile.Write(ref _held, [.. _held, state]);
    }
    internal static void Release(VehicleUpdateState state) => Volatile.Write(ref _held, _held.Where(s => s != state).ToArray());
    internal static void BeginSettledUpdate() => Volatile.Write(ref _watched, []);
    internal static void WatchFirstStep(VehicleUpdateState[] states) => Volatile.Write(ref _watched, states);
    internal static void Clear() { Volatile.Write(ref _held, []); Volatile.Write(ref _watched, []); }

    private static readonly MethodInfo ModuleTick = Require(typeof(VehicleUpdateState), nameof(VehicleUpdateState.UpdateModules));
    private static MethodInfo Require(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    internal static IEnumerable<CodeInstruction> ReplaceModuleCalls(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        int replaced = 0;
        foreach (var code in codes)
            if (code.Calls(ModuleTick))
            { code.opcode = OpCodes.Call; code.operand = Require(typeof(QuietSliceQuiescence), nameof(UpdateModules)); replaced++; }
        if (replaced == 0) throw new InvalidOperationException("Quiet slicing module caller changed.");
        return codes;
    }

    private static void UpdateModules(VehicleUpdateState state, SimStep step)
    {
        if (!Volatile.Read(ref _held).Contains(state)) { state.UpdateModules(step); return; }
        // The caller next runs UpdateActiveNozzles. Merely skipping the tick would
        // reuse the firing source's old performance and keep applying stale thrust.
        foreach (ref var nozzle in state.UpdateData.RocketNozzles.GetNewStates())
        {
            nozzle.Performance = RocketPerformance.Zero;
            nozzle.ThrustFraction = 0;
            nozzle.ThrustTime = 0;
            nozzle.ActivatedThisFrame = false;
            nozzle.DeactivatedThisFrame = true;
        }
        state.NumActiveNozzles = 0;
    }

    // Observe deferred destructive processing without swallowing pending failures
    // or granting terrain/crash immunity to a source or neighboring craft.
    private static void FailurePrefix(Vehicle vehicle)
    {
        if (Volatile.Read(ref _held).Concat(Volatile.Read(ref _watched)).Any(s => ReferenceEquals(s.ReadOnlyVehicle, vehicle)))
            Console.WriteLine($"[purr2d2] Native part failure applied to slice participant {vehicle.Id}; native failure behavior is retained.");
    }

    private static void DetectionPrefix(VehicleUpdateState __0, out bool __state) =>
        __state = __0.PartFailureEvent != null || __0.DestructionEvent != null;

    private static void DetectionPostfix(VehicleUpdateState __0, bool __state)
    {
        if (__state || __0.PartFailureEvent == null && __0.DestructionEvent == null
            || !Volatile.Read(ref _held).Contains(__0) && !Volatile.Read(ref _watched).Contains(__0)) return;
        Console.WriteLine($"[purr2d2] Native failure scheduled during quiet breakup: {__0.ReadOnlyVehicle.Id}; "
            + $"partFailure={__0.PartFailureEvent != null}, destruction={__0.DestructionEvent?.GetType().Name ?? "none"}.");
    }
}
