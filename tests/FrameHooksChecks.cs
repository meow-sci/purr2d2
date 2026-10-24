using System.Reflection;
using System.Reflection.Emit;
using Brutal.Concurrency.Jobs;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Abstractions;
using HarmonyLib;
using KSA;
using Purr2D2.Runtime;
using Purr2D2.Slicing;
using RenderCore.Input;

internal static class FrameHooksChecks
{
    internal static void Run()
    {
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
        Console.WriteLine("PASS: actual KSA PrepareFrame IL preserves four exact call stacks, all other instructions and labels; missing/ambiguous/reordered/prefixed seams rejected; native wait exception preserved; Space somersault hook delegates safely at KittenEva.OnKey");

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
        GameHooks.Runtime = null;

        Console.WriteLine("PASS: actual KSA keyboard guard/controller ordering and KittenEva.OnKey signature verified; accepted, declined, absent and failing runtime paths preserve native input contract; main-window focus loss cancels pending keyboard intent");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
