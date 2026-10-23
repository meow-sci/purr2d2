using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;
using Purr2D2.Force;
using Purr2D2.Runtime;

/// <summary>Inspect real game IL without constructing a game, scene or native physics object.</summary>
internal static class ForceHostChecks
{
    public static void Main()
    {
        Run();
        Console.WriteLine("Force host IL checks passed (actual KSA handoff, both patch orders, stack contracts, safe rejection).");
    }

    internal static void Run()
    {
        MethodInfo caller = Require(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), typeof(SimStep));
        MethodInfo take = Require(typeof(Vehicle), nameof(Vehicle.TakeUpdateStateIfOrphaned));
        MethodInfo native = Require(typeof(Vehicle), nameof(Vehicle.PrepareWorker), typeof(SimStep));
        MethodInfo wrapped = Require(typeof(SomersaultMotionController), nameof(SomersaultMotionController.PrepareWorkerAndHandoff),
            typeof(Vehicle), typeof(SimStep));
        MethodInfo filter = Require(typeof(ForcePhysicsPatches), nameof(ForcePhysicsPatches.FilterCapturedWorker), typeof(VehicleUpdateState));
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(caller);
        Check(caller.DeclaringType == typeof(Universe) && caller.Module.Assembly.GetName().Name == "KSA"
            && original.Count > 20 && original.Count(c => c.Calls(take)) == 1,
            "the fixture reads the actual KSA Universe method rather than a synthetic host seam");
        Check(!take.IsStatic && take.ReturnType == typeof(VehicleUpdateState) && take.GetParameters().Length == 0
            && filter.IsStatic && filter.ReturnType == take.ReturnType
            && filter.GetParameters().Select(p => p.ParameterType).SequenceEqual([take.ReturnType]),
            "the Force filter consumes exactly the native nullable worker reference and returns the same stack type");
        Check(wrapped.IsStatic && wrapped.ReturnType == native.ReturnType
            && wrapped.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                new[] { typeof(Vehicle) }.Concat(native.GetParameters().Select(p => p.ParameterType))),
            "the somersault wrapper preserves the native virtual receiver, arguments and return stack");

        List<CodeInstruction> forceOnly = ForcePhysicsPatches.FilterOrphanWorkers(original).ToList();
        Check(forceOnly.Count == original.Count + 1 && forceOnly.Count(c => c.Calls(filter)) == 1,
            "Force adds precisely one worker filter to the actual host method");
        List<CodeInstruction> forceFirst = SomersaultMotionController.InsertWorkerHandoff(forceOnly).ToList();
        List<CodeInstruction> motionFirst = ForcePhysicsPatches.FilterOrphanWorkers(
            SomersaultMotionController.InsertWorkerHandoff(original)).ToList();
        Check(forceFirst.Count == motionFirst.Count && forceFirst.Zip(motionFirst).All(pair => Same(pair.First, pair.Second)),
            "Force then somersault and somersault then Force produce identical actual-host IL");
        foreach (var transformed in new[] { forceFirst, motionFirst })
        {
            Check(transformed.Count == original.Count + 1 && transformed.Count(c => c.Calls(take)) == 1
                && transformed.Count(c => c.Calls(filter)) == 1 && transformed.Count(c => c.Calls(wrapped)) == 1
                && transformed.All(c => !c.Calls(native)),
                "composition retains one original getter, one native-preserving prepare wrapper and one filter");
            int nativeGetter = original.FindIndex(c => c.Calls(take));
            int emittedGetter = transformed.FindIndex(c => c.Calls(take));
            Check(emittedGetter == nativeGetter && transformed[emittedGetter + 1].opcode == OpCodes.Call
                && transformed[emittedGetter + 1].Calls(filter),
                "the filter runs immediately after the original getter and before its native null/adoption branch");
            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction before = original[i], after = transformed[i + (i > nativeGetter ? 1 : 0)];
                if (before.Calls(native))
                    Check(after.opcode == OpCodes.Call && after.Calls(wrapped), "only native preparation is replaced by the checked handoff");
                else Check(before.opcode == after.opcode && Equals(before.operand, after.operand),
                    "all remaining actual KSA instructions and operands survive composition");
                Check(before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
                    "all native branch labels and exception boundaries remain on their original instructions");
            }
        }

        Reject(codes => codes.RemoveAt(codes.FindIndex(c => c.Calls(take))), "a missing native orphan getter");
        Reject(codes => codes.Add(new CodeInstruction(OpCodes.Call, take)), "duplicate native orphan getters");
        Reject(codes =>
        {
            int index = codes.FindIndex(c => c.Calls(take));
            CodeInstruction getter = codes[index]; codes.RemoveAt(index); codes.Insert(0, getter);
        }, "a getter without its native receiver setup");
        Check(ForcePhysicsPatches.FilterCapturedWorker(null) is null,
            "the inserted filter preserves the native null result without touching game state");

        void Reject(Action<List<CodeInstruction>> mutate, string description)
        {
            foreach (bool afterMotion in new[] { false, true })
            {
                List<CodeInstruction> changed = afterMotion
                    ? SomersaultMotionController.InsertWorkerHandoff(original).ToList()
                    : original.Select(c => new CodeInstruction(c)).ToList();
                mutate(changed);
                try { ForcePhysicsPatches.FilterOrphanWorkers(changed).ToList(); }
                catch (InvalidOperationException)
                {
                    Console.WriteLine($"PASS: Force rejects {description} {(afterMotion ? "after" : "before")} somersault transformation");
                    continue;
                }
                throw new InvalidOperationException("Force host IL check accepted " + description + ".");
            }
        }
    }

    private static MethodInfo Require(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
    private static bool Same(CodeInstruction a, CodeInstruction b) => a.opcode == b.opcode && Equals(a.operand, b.operand)
        && a.labels.SequenceEqual(b.labels) && a.blocks.SequenceEqual(b.blocks);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Force host IL check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
