using System.Reflection;
using HarmonyLib;
using KSA;

namespace Purr2D2.Runtime;

/// <summary>
/// KSA 5938 records arrivals for every non-debris vehicle and keeps each destination's first
/// visitor ID in the per-system visit log saved with universe.xml. A dropped hilt is deliberately
/// not debris (native debris eviction must not reclaim owned equipment), so without this filter it
/// could be credited as a visitor and leave its transient ID in the player's save.
/// </summary>
internal static class DroppedHiltArrivals
{
    internal const string IdPrefix = "Purr2D2_DroppedHilt_";
    private static AccessTools.FieldRef<object, Vehicle>? _trackedVehicle;

    internal static bool IsDroppedHilt(string? id) =>
        id is not null && id.Length > IdPrefix.Length && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>The private per-vehicle arrival evaluation, and its tracked-vehicle field.</summary>
    internal static MethodInfo Target(out AccessTools.FieldRef<object, Vehicle> vehicle)
    {
        var tracked = AccessTools.Inner(typeof(ArrivalService), "Tracked")
            ?? throw new MissingMemberException(typeof(ArrivalService).FullName, "Tracked");
        var field = AccessTools.DeclaredField(tracked, "Vehicle");
        if (field is null || field.IsStatic || field.FieldType != typeof(Vehicle))
            throw new MissingFieldException(tracked.FullName, "Vehicle");
        var check = AccessTools.DeclaredMethod(typeof(ArrivalService), "Check", [tracked, typeof(double), typeof(long)]);
        if (check is null || check.IsStatic || check.ReturnType != typeof(void))
            throw new MissingMethodException(typeof(ArrivalService).FullName, "Check");
        vehicle = AccessTools.FieldRefAccess<Vehicle>(tracked, field.Name);
        return check;
    }

    /// <summary>Fail-closed: a changed host leaves native arrival tracking untouched.</summary>
    internal static bool TryInstall(Harmony harmony, out string? error)
    {
        try
        {
            var check = Target(out var vehicle);
            _trackedVehicle = vehicle;
            harmony.Patch(check, prefix: new HarmonyMethod(typeof(DroppedHiltArrivals), nameof(BeforeCheck)));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            _trackedVehicle = null;
            error = ex.Message;
            return false;
        }
    }

    // Skipping the evaluation leaves the hilt tracked but never arrived; every other vehicle is unchanged.
    internal static bool BeforeCheck(object __0) =>
        _trackedVehicle is not { } read || __0 is null || !IsDroppedHilt(read(__0)?.Id);
}
