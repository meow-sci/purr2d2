using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA;
using Purr2D2.Runtime;

internal static class SliceWorkerHooksChecks
{
    internal static void Run()
    {
        var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(VehicleUpdateTask), "Run"));
        var transformed = SliceWorkerHooks.InsertRunStages(original).ToList();
        var marker = AccessTools.Method(typeof(SliceWorkerHooks), "EnterStage");
        Require(transformed.Count == original.Count + 20 && transformed.Count(c => c.Calls(marker)) == 10,
            "Actual KSA Run must have exactly ten ordered stage markers.");
        int offset = 0;
        for (int i = 0; i < original.Count; i++)
        {
            if (i + offset + 1 < transformed.Count && transformed[i + offset + 1].Calls(marker))
            {
                Require(transformed[i + offset].opcode == OpCodes.Ldc_I4, "Stage marker requires one enum argument.");
                Require(original[i].labels.SequenceEqual(transformed[i + offset].labels)
                    && original[i].blocks.SequenceEqual(transformed[i + offset].blocks),
                    "Call labels and exception boundaries must cover the inserted marker.");
                offset += 2;
            }
            var after = transformed[i + offset];
            Require(original[i].opcode == after.opcode && Equals(original[i].operand, after.operand),
                "Worker profiling must preserve every original instruction and synchronization call.");
        }
        Require(offset == 20, "Worker stage transformation changed instruction structure.");
        Reject(codes => codes.RemoveAt(codes.FindIndex(c => c.operand is MethodInfo m && m.Name == "MergeBubbles")));
        Reject(codes => codes.Insert(0, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(VehicleUpdateTask), "MergeBubbles"))));
        Reject(codes =>
        {
            int a = codes.FindIndex(c => c.operand is MethodInfo m && m.Name == "MergeBubbles");
            int b = codes.FindIndex(c => c.operand is MethodInfo m && m.Name == "BuildContactIslands");
            (codes[a], codes[b]) = (codes[b], codes[a]);
        });
        Reject(codes => codes.Insert(codes.FindIndex(c => c.operand is MethodInfo m && m.Name == "MergeBubbles"),
            new CodeInstruction(OpCodes.Tailcall)));
        var targets = SliceWorkerHooks.ChildTargets();
        Require(targets.Length == 15 && targets.Select(x => x.Method).Distinct().Count() == targets.Length
            && targets.All(x => !x.Method.IsStatic && !x.Method.ContainsGenericParameters && x.Method.ReturnType == typeof(void)),
            "Joined child seams must resolve to fifteen distinct concrete instance methods in the actual engine.");
        Require(targets.Any(x => x.Method.Name.EndsWith("IBepuDispatch.RunWorker", StringComparison.Ordinal))
            && targets.All(x => x.Method.Name != "RunBody"),
            "Bepu helpers must use the concrete joined-worker entry without copying RunBody's function-pointer local.");
        var exception = new InvalidOperationException("fixture");
        var finalizer = AccessTools.Method(typeof(SliceWorkerHooks), "RunFinalizer");
        Require(ReferenceEquals(finalizer.Invoke(null, [exception, default(Purr2D2.Slicing.SliceWorkerRunToken)]), exception),
            "A diagnostic finalizer must preserve the native exception instance.");
        // Actual patch generation catches host constructs (notably function-pointer
        // locals) that instruction-only assertions cannot. Never invoke native physics.
        const string ownerId = "purr2d2.validation.worker-hooks";
        string group = ownerId + ".slice-worker-diagnostics";
        var owner = new Harmony(ownerId);
        try
        {
            SliceWorkerHooks.Install(owner);
            Require(Purr2D2.Slicing.SliceWorkerDiagnostics.Supported, "Production worker patch group must install on the pinned engine.");
            var patched = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(group) == true).ToArray();
            Require(patched.Length == targets.Length + 2, "Every optional worker hook must install as one owned group.");
            foreach (var target in patched) RuntimeHelpers.PrepareMethod(target.MethodHandle);
            SliceWorkerHooks.Install(owner);
            foreach (var target in patched)
            {
                var info = Harmony.GetPatchInfo(target)!;
                Require(info.Prefixes.Count(patch => patch.owner == group) <= 1
                    && info.Finalizers.Count(patch => patch.owner == group) <= 1
                    && info.Transpilers.Count(patch => patch.owner == group) <= 1,
                    "Reinstall must replace its prior callback group without duplicate scopes.");
            }
        }
        finally { SliceWorkerHooks.Uninstall(); }
        Require(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(group) == true),
            "Uninstall must remove every optional worker hook.");
        Console.WriteLine("PASS: actual KSA worker IL has ten exact stage seams, unchanged native work, rejected incompatible layouts, fifteen concrete child targets and preserved native exceptions");
        Console.WriteLine("PASS: actual host worker hook group installs, JITs, reinstalls without duplicate callbacks and uninstalls; native physics is never invoked");

        void Reject(Action<List<CodeInstruction>> mutate)
        {
            var codes = original.Select(c => new CodeInstruction(c)).ToList(); mutate(codes);
            try { SliceWorkerHooks.InsertRunStages(codes).ToList(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Worker transpiler accepted an incompatible layout.");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
