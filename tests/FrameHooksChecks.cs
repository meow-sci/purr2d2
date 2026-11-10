using System.Reflection;
using System.Reflection.Emit;
using Brutal.Concurrency.Jobs;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Abstractions;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Runtime;
using Purr2D2.Slicing;
using RenderCore.Input;

internal static class FrameHooksChecks
{
    internal static void Run()
    {
        CheckDropSpinKeyRouting();
        GameHooks.Runtime = null;
        var method = AccessTools.Method(typeof(KSA.Program), "PrepareFrame", [typeof(double), typeof(double)])!;
        var original = PatchProcessor.GetOriginalInstructions(method);
        var transformed = GameHooks.InsertSettledUpdate(original).ToList();
        Require(transformed.Count == original.Count + 3, "Frame hook must add exactly entry + settled-load/call instructions.");
        Require(transformed[0].Calls(AccessTools.Method(typeof(SliceFrameDiagnostics), "EnterFrame")),
            "Frame interval baseline must precede all original PrepareFrame work.");
        int input = original.FindIndex(c => c.Calls(AccessTools.Method(typeof(InputEvents), "ApplyInputEvents")));
        Require(transformed[input + 2].opcode == OpCodes.Ldarg_2
            && transformed[input + 3].Calls(AccessTools.Method(typeof(GameHooks), "SettledUpdate")),
            "Settled runtime must receive dtPlayer immediately after input and before dispatch.");

        var expected = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.Method(typeof(JobScheduler), "Wait", Type.EmptyTypes)!] = AccessTools.Method(typeof(GameHooks), "WaitVehicleSolver")!,
            [AccessTools.Method(typeof(PartTree), "FlushDirtyDerived")!] = AccessTools.Method(typeof(GameHooks), "FlushDirtyDerived")!,
            [AccessTools.Method(typeof(PartTree), "FlushDirtyResourceManagers")!] = AccessTools.Method(typeof(GameHooks), "FlushDirtyResources")!,
            [AccessTools.Method(typeof(Universe), "ExecuteNextVehicleSolvers", [typeof(double), typeof(SimStep)])!] =
                AccessTools.Method(typeof(GameHooks), "PrepareAndDispatchVehicles")!
        };
        int replaced = 0;
        var solver = AccessTools.Field(typeof(JobSystems), "VehicleSolver");
        for (int index = 0; index < original.Count; index++)
        {
            var before = original[index];
            var after = transformed[index + (index <= input ? 1 : 3)];
            bool vehicleWait = before.Calls(AccessTools.Method(typeof(JobScheduler), "Wait", Type.EmptyTypes))
                && index > 0 && original[index - 1].LoadsField(solver);
            if (before.operand is MethodInfo target && expected.TryGetValue(target, out var wrapper)
                && (target.Name != "Wait" || vehicleWait))
            {
                Require(after.opcode == OpCodes.Call && after.Calls(wrapper), "Wrong diagnostic call replacement.");
                var stack = (target.IsStatic ? Enumerable.Empty<Type>() : [target.DeclaringType!])
                    .Concat(target.GetParameters().Select(p => p.ParameterType));
                Require(wrapper.IsStatic && wrapper.ReturnType == target.ReturnType
                    && stack.SequenceEqual(wrapper.GetParameters().Select(p => p.ParameterType)),
                    "A wrapper must preserve receiver/argument order and return stack exactly.");
                replaced++;
            }
            else Require(before.opcode == after.opcode && Equals(before.operand, after.operand),
                "Frame transpiler altered unrelated native work or synchronization.");
            Require(before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
                "Original branch labels and exception boundaries must be preserved.");
        }
        Require(replaced == 4, "Exactly one native vehicle wait, derived flush, resource flush and vehicle dispatch must be wrapped.");

        var flush = AccessTools.Method(typeof(PartTree), "FlushDirtyDerived")!;
        Reject(codes => codes.RemoveAt(codes.FindIndex(c => c.Calls(flush))), "missing seam");
        Reject(codes => codes.Insert(0, new CodeInstruction(OpCodes.Call, flush)), "duplicate seam");
        Reject(codes =>
        {
            int derived = codes.FindIndex(c => c.Calls(flush));
            int resources = codes.FindIndex(c => c.Calls(AccessTools.Method(typeof(PartTree), "FlushDirtyResourceManagers")));
            (codes[derived], codes[resources]) = (codes[resources], codes[derived]);
        }, "reordered seams");
        Reject(codes => codes[codes.FindIndex(c => c.LoadsField(solver))].operand = AccessTools.Field(typeof(JobSystems), "OrbitSolvers"),
            "different wait receiver");
        Reject(codes => codes.Insert(codes.FindIndex(c => c.Calls(flush)), new CodeInstruction(OpCodes.Tailcall)), "prefixed seam");

        // A direct delegate to the internal helper preserves the native exception instead
        // of wrapping it in TargetInvocationException on the actual frame path.
        var resource = AccessTools.Method(typeof(PartTree), "FlushDirtyResourceManagers")!;
        Require(resource.IsAssembly && resource.IsStatic && resource.ReturnType == typeof(void)
            && resource.GetParameters().Length == 0 && resource.CreateDelegate<Action>() != null,
            "Host-internal resource flush must remain bindable with the exact cached Action signature.");
        var waitWrapper = AccessTools.Method(typeof(GameHooks), "WaitVehicleSolver")!;
        try { waitWrapper.Invoke(null, [null]); throw new Exception("Native null scheduler exception was swallowed."); }
        catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { }
        CheckSpaceSomersaultInputHook();
        CheckPartConstructionHooks();
        Console.WriteLine("PASS: actual KSA PrepareFrame IL preserves four exact call stacks, all other instructions and labels; missing/ambiguous/reordered/prefixed seams rejected; native wait exception preserved; Space somersault hook delegates safely at KittenEva.OnKey; every public Part constructor reaches exactly one fragment-picker postfix");

        void Reject(Action<List<CodeInstruction>> mutate, string description)
        {
            var changed = original.Select(c => new CodeInstruction(c)).ToList(); mutate(changed);
            try { GameHooks.InsertSettledUpdate(changed).ToList(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Frame transpiler accepted " + description + ".");
        }
    }

    private static void CheckSpaceSomersaultInputHook()
    {
        var kittenOnKey = AccessTools.Method(typeof(KittenEva), nameof(KittenEva.OnKey), [typeof(GlfwKeyEvent)])!;
        Require(kittenOnKey.DeclaringType == typeof(KittenEva) && kittenOnKey.IsVirtual
            && kittenOnKey.ReturnType == typeof(bool) && kittenOnKey.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(GlfwKeyEvent)]),
            "Space somersault must patch the native KittenEva.OnKey(GlfwKeyEvent) override returning bool.");
        var prefix = AccessTools.Method(typeof(GameHooks), nameof(GameHooks.SpaceSomersaultKey))!;
        var prefixParameters = prefix.GetParameters();
        Require(prefix.IsStatic && prefix.ReturnType == typeof(bool)
            && prefixParameters.Length == 3
            && prefixParameters[0].ParameterType == typeof(KittenEva)
            && prefixParameters[1].ParameterType == typeof(GlfwKeyEvent)
            && prefixParameters[2].ParameterType == typeof(bool).MakeByRefType(),
            "Space somersault prefix must match Harmony's instance/event/result contract.");

        var programKey = AccessTools.Method(typeof(Program), nameof(Program.OnKey),
            [typeof(GlfwWindow), typeof(GlfwKey), typeof(int), typeof(GlfwKeyAction), typeof(GlfwModifier)])!;
        var dispatchKey = AccessTools.Method(typeof(Program), "DispatchKeyEvent")!;
        var programKeyCalls = PatchProcessor.GetOriginalInstructions(programKey).Where(c => c.operand is MethodInfo).Select(c => (MethodInfo)c.operand!).ToList();
        var dispatchCalls = PatchProcessor.GetOriginalInstructions(dispatchKey).Where(c => c.operand is MethodInfo).Select(c => (MethodInfo)c.operand!).ToList();
        var dispatch = AccessTools.Method(typeof(Program), "DispatchKeyEvent")!;
        var controls = new[]
        {
            AccessTools.Method(typeof(GameSettings), "OnKeyAll", [typeof(GlfwKeyEvent)])!,
            AccessTools.Method(typeof(Popup), "OnKeyAll", [typeof(GlfwKeyEvent)])!,
            AccessTools.Method(typeof(ConsoleWindowEx), "OnKey", [typeof(ConsoleWindow), typeof(GlfwWindow), typeof(GlfwKey), typeof(int), typeof(GlfwKeyAction), typeof(GlfwModifier)])!,
            AccessTools.PropertyGetter(typeof(ConsoleWindow), "IsOpen")!,
            AccessTools.PropertyGetter(typeof(ImGuiIOPtr), "WantCaptureKeyboard")!,
            dispatch
        };
        int previous = -1;
        foreach (MethodInfo control in controls)
        {
            int index = programKeyCalls.FindIndex(control.Equals);
            Require(index > previous, $"Program.OnKey no longer applies native guard/routing {control.DeclaringType?.Name}.{control.Name} in order.");
            previous = index;
        }
        var editorKey = AccessTools.Method(typeof(VehicleEditor), "OnKey", [typeof(GlfwKeyEvent)])!;
        var cameraKey = AccessTools.Method(typeof(Camera), "OnKey", [typeof(GlfwKeyEvent)])!;
        var activeControllerKey = AccessTools.Method(typeof(Controller), "OnKey", [typeof(GlfwKeyEvent)])!;
        var vehicleKey = AccessTools.Method(typeof(Vehicle), "OnKey", [typeof(GlfwKeyEvent)])!;
        var route = new[] { editorKey, cameraKey, activeControllerKey, vehicleKey };
        previous = -1;
        foreach (MethodInfo method in route)
        {
            int index = dispatchCalls.FindIndex(method.Equals);
            Require(index > previous, $"Program.DispatchKeyEvent no longer routes editor/camera/controller before the vehicle ({method.DeclaringType?.Name}.{method.Name}).");
            previous = index;
        }

        var eventValue = new GlfwKeyEvent(null!, GlfwKeyAction.Press, GlfwKey.Space, (GlfwModifier)0);
        var runtime = new SaberRuntime { SpaceSomersaultFilterResult = true };
        GameHooks.Runtime = runtime;
        object?[] args = [null, eventValue, false];
        bool runOriginal = (bool)prefix.Invoke(null, args)!;
        Require(!runOriginal && Equals(args[2], true) && runtime.SpaceSomersaultFilterCalls == 1
            && runtime.LastSpaceSomersaultOwner == null
            && runtime.LastSpaceSomersaultKeyEvent.Key == GlfwKey.Space
            && runtime.LastSpaceSomersaultKeyEvent.Action == GlfwKeyAction.Press,
            "A runtime-accepted somersault press must set the handled result and suppress native kitten input.");

        runtime.SpaceSomersaultFilterCalls = 0;
        runtime.SpaceSomersaultFilterResult = false;
        args = [null, eventValue, false];
        runOriginal = (bool)prefix.Invoke(null, args)!;
        Require(runOriginal && Equals(args[2], false) && runtime.SpaceSomersaultFilterCalls == 1,
            "A runtime-unhandled Space event must reach native KittenEva.OnKey without changing its result.");

        GameHooks.Runtime = null;
        args = [null, eventValue, false];
        runOriginal = (bool)prefix.Invoke(null, args)!;
        Require(runOriginal && Equals(args[2], false),
            "A missing runtime must pass native kitten input through unchanged.");

        runtime.SpaceSomersaultFilterThrows = true;
        runtime.SpaceSomersaultFilterCalls = 0;
        GameHooks.Runtime = runtime;
        SaberRuntime.LastReportMessage = null;
        SaberRuntime.LastReportException = null;
        args = [null, eventValue, false];
        runOriginal = (bool)prefix.Invoke(null, args)!;
        Require(runOriginal && Equals(args[2], false) && runtime.SpaceSomersaultFilterCalls == 1
            && SaberRuntime.LastReportMessage == "Space somersault input hook failed"
            && SaberRuntime.LastReportException is InvalidOperationException,
            "A runtime failure must be reported and must not break native input dispatch.");

        var focus = AccessTools.Method(typeof(GameHooks), "OverlayFocus")!;
        focus.Invoke(null, [null, GlfwFocusEvent.LostFocus]);
        Require(runtime.OverlayFocusLostCalls == 1 && runtime.PendingSpaceSomersaultCancelCalls == 1,
            "Losing focus on the main window must release overlay input and cancel pending keyboard somersault intent.");
        runtime.DeferHudFocusLoss = true;
        focus.Invoke(null, [null, GlfwFocusEvent.LostFocus]);
        Require(runtime.OverlayFocusLostCalls == 1 && runtime.PendingSpaceSomersaultCancelCalls == 2,
            "Moving focus into a detached HUD preserves its gestures while cancelling main-window keyboard intent.");
        runtime.HudBackendResult = true;
        Require(!(bool)AccessTools.Method(typeof(GameHooks), "OverlayImGuiMouseButton")!.Invoke(null,
            [null, GlfwMouseButton.Left, GlfwButtonAction.Press])! && runtime.HudButtonRoutes == 1 && runtime.HudButtonSuppressions == 1,
            "Secondary custom presses are routed once before backend ImGui consumption.");
        AccessTools.Method(typeof(GameHooks), "OverlayImGuiMouseButton")!.Invoke(null,
            [null, GlfwMouseButton.Left, GlfwButtonAction.Release]);
        Require(runtime.HudButtonSuppressions == 2,
            "Routed secondary releases still retire the ImGui paired-button suppression mask.");
        Require(!(bool)AccessTools.Method(typeof(GameHooks), "OverlayImGuiScroll")!.Invoke(null,
            [null, new double2(0, 1)])! && runtime.HudScrollRoutes == 1,
            "Secondary custom wheel input applies through its backend route once.");
        AccessTools.Method(typeof(GameHooks), "OverlayImGuiCursor")!.Invoke(null, [null]);
        Require(runtime.HudCursorRoutes == 1, "Secondary cursor input reaches the HUD without skipping the ImGui backend.");
        Require(!(bool)AccessTools.Method(typeof(GameHooks), "OverlayImGuiKey")!.Invoke(null,
            [null, GlfwKey.Escape, GlfwKeyAction.Press])! && runtime.HudKeyRoutes == 1,
            "Detached gesture cancellation reaches the HUD before ImGui consumes Escape.");
        runtime.HudGameBlock = true;
        Require(!(bool)AccessTools.Method(typeof(GameHooks), "OverlayScroll")!.Invoke(null, [null, new double2(0, 1)])!,
            "An owned secondary window cannot fall through into native camera controls.");
        GameHooks.Runtime = null;

        Console.WriteLine("PASS: actual KSA keyboard guard/controller ordering and KittenEva.OnKey signature verified; accepted, declined, absent and failing runtime paths preserve native input contract; main-window focus loss cancels pending keyboard intent");
    }

    private static void CheckDropSpinKeyRouting()
    {
        var hook = AccessTools.Method(typeof(GameHooks), "OverlayKey")!;
        var runtime = new SaberRuntime { DropSpinKeyResult = true };
        GameHooks.Runtime = runtime;
        Require(!(bool)hook.Invoke(null, [null, GlfwKey.Escape, GlfwKeyAction.Press])!
            && runtime.DropSpinKeyCalls == 1 && runtime.ForceKeyCalls == 0,
            "A drop-pill Escape must be consumed before Furce or native key handling.");
        runtime.DropSpinKeyResult = false;
        Require((bool)hook.Invoke(null, [null, GlfwKey.Escape, GlfwKeyAction.Release])!
            && runtime.DropSpinKeyCalls == 2 && runtime.ForceKeyCalls == 1,
            "Unhandled drop-pill keys must retain the existing Furce and native input route.");
        GameHooks.Runtime = null;
        Require((bool)hook.Invoke(null, [null, GlfwKey.Escape, GlfwKeyAction.Press])!,
            "Missing runtime must leave native key handling available.");
        Console.WriteLine("PASS: drop spin key routing consumes owned cancellation before Furce/native input and preserves unhandled keys");
    }

    // Legacy fragment picking is repaired in a constructor postfix. Follow each public
    // constructor's `: this(...)` chain in host IL: a bypassed postfix leaves old saved
    // fragments unpickable, and a doubled one would run the repair twice.
    private static void CheckPartConstructionHooks()
    {
        var hooks = GameHooks.PartConstructionHooks();
        Require(hooks.Select(h => h.MethodHandle).Distinct().Count() == hooks.Length, "Part construction hooks must be distinct.");
        var constructors = typeof(Part).GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        Require(constructors.Length > 0, "Part must expose public construction entry points.");
        foreach (var constructor in constructors)
            Require(Reached(constructor, hooks) == 1,
                $"Public Part constructor {constructor} must reach exactly one fragment-picker postfix.");
        var saveLoad = AccessTools.Constructor(typeof(Part), [typeof(PartInstance), typeof(Part)])!;
        Require(Reached(saveLoad, [hooks[0]]) == 0,
            "Constructor-chain analysis must detect that the string/template hook alone misses save-load construction.");
        var rootSaveLoad = AccessTools.Constructor(typeof(Part), [typeof(PartInstance)])!;
        Require(Reached(rootSaveLoad, hooks.Where(h => h.MethodHandle != rootSaveLoad.MethodHandle).ToArray()) == 0,
            "Constructor-chain analysis must detect that 5801's one-argument save constructor bypasses the other public hooks.");
        var subtree = PatchProcessor.GetOriginalInstructions(AccessTools.PropertyGetter(typeof(Part), nameof(Part.SubtreeModules))!);
        Require(subtree.Any(c => c.LoadsField(AccessTools.Field(typeof(Part), "_subtreeModules")))
            && subtree.Any(c => c.operand is MemberInfo m && m.Name is "Modules" or "get_Modules"),
            "Part.SubtreeModules must still fall back to Modules, as the picker-repair stub models.");
    }

    private static int Reached(ConstructorInfo constructor, ConstructorInfo[] hooks)
    {
        int reached = 0;
        for (int depth = 0; constructor != null && depth < 8; depth++)
        {
            var handle = constructor.MethodHandle;
            if (hooks.Any(h => h.MethodHandle == handle)) reached++;
            constructor = (PatchProcessor.GetOriginalInstructions(constructor).FirstOrDefault(c => c.opcode == OpCodes.Call
                && c.operand is ConstructorInfo next && next.DeclaringType == typeof(Part))?.operand as ConstructorInfo)!;
        }
        return reached;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
