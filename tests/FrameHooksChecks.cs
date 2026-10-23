using System.Reflection;
using System.Reflection.Emit;
using Brutal.Concurrency.Jobs;
using HarmonyLib;
using KSA;
using Purr2D2.Runtime;
using Purr2D2.Slicing;

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
        Console.WriteLine("PASS: actual KSA PrepareFrame IL preserves four exact call stacks, all other instructions and labels; missing/ambiguous/reordered/prefixed seams rejected; native wait exception preserved");

        void Reject(Action<List<CodeInstruction>> mutate, string description)
        {
            var changed = original.Select(c => new CodeInstruction(c)).ToList(); mutate(changed);
            try { GameHooks.InsertSettledUpdate(changed).ToList(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Frame transpiler accepted " + description + ".");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
