using HarmonyLib;
using KSA;

namespace Purr2D2.Runtime;

internal static class SaberDropHooks
{
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(GameSave), nameof(GameSave.Populate), []),
            prefix: new HarmonyMethod(typeof(SaberDropHooks), nameof(BeforeSave))
            { priority = Priority.Last - 150, after = ["MeowSci.Unscience"] });
        if (!DroppedHiltArrivals.TryInstall(harmony, out var error))
            Console.WriteLine("[purr2d2] Dropped hilts may be recorded in KSA's visit log; arrival filter unavailable: " + error);
    }

    private static void BeforeSave(bool __runOriginal)
    {
        if (!__runOriginal || GameHooks.Runtime is not { } runtime) return;
        // Save capture is outside the normal handoff; join before removing transient colliders.
        JobSystems.WaitAll();
        runtime.ResetAllDroppedSabers();
    }
}
