using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using KSA;

namespace Purr2D2.Force;

/// <summary>Captured orphan vehicles never enter a physics bubble, so gravity and contacts cannot run.</summary>
internal static class ForcePhysicsPatches
{
    private static readonly Vehicle[] Empty = [];
    private static Vehicle[] _captured = Empty;
    private static AccessTools.FieldRef<Vehicle, VehicleUpdateState>? _worker;
    private static readonly MethodInfo Prepare = AccessTools.Method(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), [typeof(SimStep)]);
    private static readonly MethodInfo Filter = AccessTools.Method(typeof(ForcePhysicsPatches), nameof(FilterCapturedWorker));

    internal static void Install(Harmony harmony)
    {
        var field = AccessTools.Field(typeof(Vehicle), "_threadWorkerUpdateState");
        if (field?.FieldType != typeof(VehicleUpdateState))
            throw new MissingFieldException("KSA vehicle worker layout changed.");
        _worker = AccessTools.FieldRefAccess<Vehicle, VehicleUpdateState>(field);
        harmony.Patch(Prepare, transpiler: new HarmonyMethod(typeof(ForcePhysicsPatches), nameof(FilterOrphanWorkers)));
    }

    internal static void Remove(Harmony harmony)
    {
        Clear();
        harmony.Unpatch(Prepare, AccessTools.Method(typeof(ForcePhysicsPatches), nameof(FilterOrphanWorkers)));
    }

    internal static void Publish(Vehicle[] sources) => Volatile.Write(ref _captured, sources);
    internal static void Clear() => Volatile.Write(ref _captured, Empty);

    internal static void Detach(Vehicle source)
    {
        var worker = _worker!(source);
        if (!ReferenceEquals(worker.ReadOnlyVehicle, source)) throw new InvalidOperationException("Force worker ownership changed.");
        if (worker.Bubble is { } bubble) source.RemoveFromBubble(bubble);
        if (source.HasPhysicsBubble) throw new InvalidOperationException("The object could not leave ordinary physics.");
    }

    internal static VehicleUpdateState? FilterCapturedWorker(VehicleUpdateState? state)
    {
        if (state == null) return null;
        // The published array is never changed in place; no worker reads mutable UI/controller lists.
        foreach (var vehicle in Volatile.Read(ref _captured))
            if (ReferenceEquals(vehicle, state.ReadOnlyVehicle)) return null;
        return state;
    }

    internal static IEnumerable<CodeInstruction> FilterOrphanWorkers(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        var take = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.TakeUpdateStateIfOrphaned), Type.EmptyTypes);
        int index = codes.FindIndex(c => c.Calls(take));
        if (index < 1 || codes.Count(c => c.Calls(take)) != 1 || take.ReturnType != typeof(VehicleUpdateState))
            throw new InvalidOperationException("Force cannot safely identify the KSA orphan-worker handoff.");
        // Preserve the original getter for other mods' checked transpilers, including somersaults.
        codes.Insert(index + 1, new CodeInstruction(OpCodes.Call, Filter));
        return codes;
    }
}
