using System.Reflection;
using System.Reflection.Emit;
using Brutal.GlfwApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Cutting;

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
        // Native PartPicker is populated by Universe.OnDrawUi in this method.
        // OnFrameViewports is earlier and would pair last frame's hit with this frame's ray.
        harmony.Patch(Require(typeof(Program), "OnDrawUiThreadSafe", typeof(double)),
            postfix: new HarmonyMethod(typeof(GameHooks), nameof(Overlay)));
        harmony.Patch(Require(typeof(Program), nameof(Program.OnMouseButton), typeof(GlfwWindow), typeof(GlfwMouseButton), typeof(GlfwButtonAction), typeof(GlfwModifier)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayMouseButton)));
        harmony.Patch(Require(typeof(Program), nameof(Program.OnCursorPos), typeof(GlfwWindow), typeof(double2)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayCursor)));
        harmony.Patch(Require(typeof(Program), nameof(Program.OnScroll), typeof(GlfwWindow), typeof(double2)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayScroll)));
        harmony.Patch(Require(typeof(Program), nameof(Program.OnFocus), typeof(GlfwWindow), typeof(GlfwFocusEvent)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayFocus)));
        harmony.Patch(Require(typeof(ImGuiBackendGlfwImpl), "OnMouseButton", typeof(GlfwWindow), typeof(GlfwMouseButton), typeof(GlfwButtonAction), typeof(GlfwModifier)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayImGuiMouseButton)));
        harmony.Patch(Require(typeof(ImGuiBackendGlfwImpl), "OnScroll", typeof(GlfwWindow), typeof(double2)),
            prefix: new HarmonyMethod(typeof(GameHooks), nameof(OverlayImGuiScroll)));
        harmony.Patch(AccessTools.Constructor(typeof(Part),
                [typeof(string), typeof(PartTemplate), typeof(PartInstance), typeof(Part)])
            ?? throw new MissingMethodException(typeof(Part).FullName, ".ctor(string, PartTemplate, PartInstance, Part)"),
            postfix: new HarmonyMethod(typeof(GameHooks), nameof(RepairFragmentPicker)));
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
    // Main-window game callbacks precede the backend's independent subscription.
    // Consume owned input in both routes so neither camera nor covered ImGui widgets react.
    private static bool OverlayMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action) =>
        !(Runtime?.FilterOverlayMouseButton(window, button, action) ?? false);
    private static bool OverlayCursor(GlfwWindow window) => !(Runtime?.FilterOverlayCursor(window) ?? false);
    private static bool OverlayScroll(GlfwWindow window, double2 offset) => !(Runtime?.FilterOverlayScroll(window, offset) ?? false);
    private static void OverlayFocus(GlfwWindow window, GlfwFocusEvent focusEvent)
    {
        if (window == Program.GetWindow() && focusEvent == GlfwFocusEvent.LostFocus) Runtime?.OverlayFocusLost();
    }
    private static bool OverlayImGuiMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action) =>
        !(Runtime?.SuppressOverlayImGuiMouseButton(window, button, action) ?? false);
    private static bool OverlayImGuiScroll(GlfwWindow __0) => !(Runtime?.SuppressOverlayImGuiScroll(__0) ?? false);
    private static void RepairFragmentPicker(Part __instance)
    {
        try { PartSliceTargeting.RepairLegacyFragmentPicker(__instance); }
        catch (Exception ex) { SaberRuntime.Report("Legacy fragment picker repair failed", ex); }
    }
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
