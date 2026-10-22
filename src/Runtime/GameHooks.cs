using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

namespace Purr2D2.Runtime;

/// <summary>Injects into the caller so optimized/inlined engine helpers cannot bypass the handoff.</summary>
internal static class GameHooks
{
    internal static SaberRuntime? Runtime;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(Require(typeof(Program), "PrepareFrame", typeof(double), typeof(double)),
            transpiler: new HarmonyMethod(typeof(GameHooks), nameof(InsertSettledUpdate)));
        harmony.Patch(Require(typeof(Program), "OnFrameEditor", typeof(double)),
            postfix: new HarmonyMethod(typeof(GameHooks), nameof(Frame)));
        harmony.Patch(Require(typeof(Program), "OnFrameViewports", typeof(double)),
            postfix: new HarmonyMethod(typeof(GameHooks), nameof(Overlay)));
        harmony.Patch(Require(typeof(Program), nameof(Program.OnGameLoaded)),
            postfix: new HarmonyMethod(typeof(GameHooks), nameof(WorldChanged)));
        harmony.Patch(Require(typeof(Universe), nameof(Universe.DeserializeSave), typeof(UniverseData)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(WorldChanged)));
        harmony.Patch(Require(typeof(Program), nameof(Program.Dispose)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(Shutdown)));
    }

    private static MethodInfo Require(Type type, string name, params Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);

    // Kept internal so validation can inspect the exact production transformation using real game IL.
    internal static IEnumerable<CodeInstruction> InsertSettledUpdate(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        MethodInfo[] ordered =
        [
            Require(typeof(Universe), nameof(Universe.ApplyOrbitSolvers)),
            Require(typeof(Universe), nameof(Universe.ApplyVehicleSolvers)),
            Require(typeof(Universe), nameof(Universe.ApplyClothSolvers)),
            Require(typeof(InputEvents), nameof(InputEvents.ApplyInputEvents)),
            Require(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers), typeof(double), typeof(SimStep)),
            Require(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), typeof(double), typeof(SimStep)),
            Require(typeof(Universe), nameof(Universe.ExecuteNextOrbitSolvers), typeof(double), typeof(SimStep))
        ];
        int previous = -1;
        int inputIndex = -1;
        foreach (MethodInfo method in ordered)
        {
            int index = codes.FindIndex(c => c.Calls(method));
            if (index <= previous || codes.Count(c => c.Calls(method)) != 1)
                throw new InvalidOperationException($"Purr2D2 cannot safely modify this KSA frame loop: {method.Name} moved or is ambiguous.");
            previous = index;
            if (method.DeclaringType == typeof(InputEvents)) inputIndex = index;
        }
        // Input events have finished and no new readers/solvers have been dispatched yet.
        // This leaves GetJobSimStep untouched, allowing other mods' handoff wrappers to coexist.
        codes.InsertRange(inputIndex + 1,
        [
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GameHooks), nameof(SettledUpdate)))
        ]);
        return codes;
    }

    private static void SettledUpdate(double dtPlayer) => Runtime?.BeforePhysics(dtPlayer);
    private static void Frame(double dtPlayer) => Runtime?.Frame(dtPlayer);
    private static void Overlay() { if (Program.DrawUI) Runtime?.DrawSliceOverlay(); }
    private static void WorldChanged()
    {
        try { Runtime?.ResetWorld(); }
        catch (Exception ex) { SaberRuntime.Report("World reset cleanup failed", ex); }
    }
    private static void Shutdown()
    {
        try { Runtime?.Shutdown(); }
        catch (Exception ex) { SaberRuntime.Report("Pre-shutdown cleanup failed", ex); }
    }
}
