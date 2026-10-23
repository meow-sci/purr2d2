using System.Reflection;
using System.Reflection.Emit;
using Brutal.Concurrency.Jobs;
using Brutal.GlfwApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

namespace Purr2D2.Runtime;

/// <summary>Injects into the caller so optimized/inlined engine helpers cannot bypass the handoff.</summary>
internal static class GameHooks
{
    internal static SaberRuntime? Runtime;
    // The host marks this helper internal. Bind once, never MethodInfo.Invoke on the frame path.
    private static Action _flushDirtyResources = null!;

    internal static void Install(Harmony harmony)
    {
        _flushDirtyResources = Require(typeof(PartTree), "FlushDirtyResourceManagers").CreateDelegate<Action>();
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
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        var wait = Require(typeof(JobScheduler), nameof(JobScheduler.Wait));
        var vehicleSolver = AccessTools.Field(typeof(JobSystems), nameof(JobSystems.VehicleSolver));
        if (vehicleSolver?.FieldType != typeof(JobScheduler))
            throw new InvalidOperationException("Purr2D2 cannot safely locate the KSA vehicle solver.");
        int waitIndex = -1;
        for (int index = 1; index < codes.Count; index++)
            if (codes[index].Calls(wait) && codes[index - 1].LoadsField(vehicleSolver)) waitIndex = index;
        if (waitIndex < 1 || codes.Count(c => c.LoadsField(vehicleSolver)) != 1
            || codes[waitIndex - 1].opcode != OpCodes.Ldsfld)
            throw new InvalidOperationException("Purr2D2 cannot safely locate the existing KSA vehicle wait.");
        MethodInfo[] ordered =
        [
            Require(typeof(Universe), nameof(Universe.ApplyOrbitSolvers)),
            Require(typeof(Universe), nameof(Universe.ApplyVehicleSolvers)),
            Require(typeof(Universe), nameof(Universe.ApplyClothSolvers)),
            Require(typeof(InputEvents), nameof(InputEvents.ApplyInputEvents)),
            Require(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers), typeof(double), typeof(SimStep)),
            Require(typeof(PartTree), nameof(PartTree.FlushDirtyDerived)),
            Require(typeof(PartTree), "FlushDirtyResourceManagers"),
            Require(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), typeof(double), typeof(SimStep)),
            Require(typeof(Universe), nameof(Universe.ExecuteNextOrbitSolvers), typeof(double), typeof(SimStep))
        ];
        int previous = waitIndex;
        int inputIndex = -1;
        foreach (MethodInfo method in ordered)
        {
            int index = codes.FindIndex(c => c.Calls(method));
            if (index <= previous || codes.Count(c => c.Calls(method)) != 1 || !method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException($"Purr2D2 cannot safely modify this KSA frame loop: {method.Name} moved or is ambiguous.");
            previous = index;
            if (method.DeclaringType == typeof(InputEvents)) inputIndex = index;
        }
        // Replace only existing calls. Each static wrapper consumes exactly the same
        // stack (including the Wait receiver) and returns the same value. Labels and
        // exception boundaries remain attached to the original call instruction.
        ReplaceCall(codes[waitIndex], wait, Require(typeof(GameHooks), nameof(WaitVehicleSolver), typeof(JobScheduler)));
        Wrap(Require(typeof(PartTree), nameof(PartTree.FlushDirtyDerived)), nameof(FlushDirtyDerived));
        Wrap(Require(typeof(PartTree), "FlushDirtyResourceManagers"), nameof(FlushDirtyResources));
        Wrap(Require(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), typeof(double), typeof(SimStep)),
            nameof(PrepareAndDispatchVehicles), typeof(double), typeof(SimStep));
        // Input events have finished and no new readers/solvers have been dispatched yet.
        // This leaves GetJobSimStep untouched, allowing other mods' handoff wrappers to coexist.
        codes.InsertRange(inputIndex + 1,
        [
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GameHooks), nameof(SettledUpdate)))
        ]);
        codes.Insert(0, new CodeInstruction(OpCodes.Call, Require(typeof(SliceFrameDiagnostics), nameof(SliceFrameDiagnostics.EnterFrame))));
        return codes;

        void Wrap(MethodInfo original, string wrapperName, params Type[] parameters)
        {
            int index = codes.FindIndex(c => c.Calls(original));
            if (index > 0 && codes[index - 1].opcode.OpCodeType == OpCodeType.Prefix)
                throw new InvalidOperationException("Purr2D2 cannot safely wrap a prefixed KSA call.");
            ReplaceCall(codes[index], original, Require(typeof(GameHooks), wrapperName, parameters));
        }
    }

    private static void ReplaceCall(CodeInstruction instruction, MethodInfo original, MethodInfo replacement)
    {
        Type[] originalStack = (original.IsStatic ? Enumerable.Empty<Type>() : [original.DeclaringType!])
            .Concat(original.GetParameters().Select(p => p.ParameterType)).ToArray();
        if (!replacement.IsStatic || replacement.ReturnType != original.ReturnType
            || !originalStack.SequenceEqual(replacement.GetParameters().Select(p => p.ParameterType))
            || (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
            || (original.IsStatic && instruction.opcode != OpCodes.Call))
            throw new InvalidOperationException("Purr2D2 diagnostic call wrapper changes the KSA evaluation stack.");
        instruction.opcode = OpCodes.Call; instruction.operand = replacement;
    }

    private static void SettledUpdate(double dtPlayer)
    {
        try { Runtime?.BeforePhysics(dtPlayer); }
        finally { SliceFrameDiagnostics.HookReturned(); }
    }
    private static void Frame(double dtPlayer)
    {
        try { Runtime?.Frame(dtPlayer); }
        finally { SliceFrameDiagnostics.PumpSummaries(); }
    }
    private static void WaitVehicleSolver(JobScheduler scheduler)
    {
        var start = SliceFrameDiagnostics.StartSegment();
        try { scheduler.Wait(); }
        finally { SliceFrameDiagnostics.EndSegment(SliceFrameSegment.VehicleWait, start); }
    }
    private static void FlushDirtyDerived()
    {
        var start = SliceFrameDiagnostics.StartSegment();
        try { PartTree.FlushDirtyDerived(); }
        finally { SliceFrameDiagnostics.EndSegment(SliceFrameSegment.DirtyDerived, start); }
    }
    private static void FlushDirtyResources()
    {
        var start = SliceFrameDiagnostics.StartSegment();
        try { _flushDirtyResources(); }
        finally { SliceFrameDiagnostics.EndSegment(SliceFrameSegment.ResourceFlush, start); }
    }
    private static void PrepareAndDispatchVehicles(double dtPlayer, SimStep simStep)
    {
        var start = SliceFrameDiagnostics.StartSegment();
        try { Universe.ExecuteNextVehicleSolvers(dtPlayer, simStep); }
        finally { SliceFrameDiagnostics.EndSegment(SliceFrameSegment.VehiclePreparationAndDispatch, start); }
    }
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
        SliceFrameDiagnostics.Reset();
        try { Runtime?.ResetWorld(); }
        catch (Exception ex) { SaberRuntime.Report("World reset cleanup failed", ex); }
    }
    private static void Shutdown()
    {
        SliceFrameDiagnostics.Reset();
        try { Runtime?.Shutdown(); }
        catch (Exception ex) { SaberRuntime.Report("Pre-shutdown cleanup failed", ex); }
    }
}
