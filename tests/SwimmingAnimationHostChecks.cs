using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

internal static class SwimmingAnimationHostChecks
{
    internal static void Run()
    {
        MethodInfo locomotion = AccessTools.Method(typeof(KittenRenderable), "UpdateLocomotionAnimationState");
        var code = PatchProcessor.GetOriginalInstructions(locomotion);
        var branch = code.Single(instruction => instruction.opcode == OpCodes.Switch);
        var cases = (Label[])branch.operand;
        Check((int)LocomotionMode.Swimming < cases.Length, "native locomotion animation has an explicit Swimming route");
        var reachable = Reachable(code, cases[(int)LocomotionMode.Swimming]);
        Check(reachable.Any(c => c.operand is FieldInfo field && field.Name == "_swimPairSampler")
            && reachable.Any(c => c.operand is MethodInfo method && method.DeclaringType == typeof(AnimatedRenderable)
                && method.Name == nameof(AnimatedRenderable.SetAnimation)),
            "the real swimming branch selects the native swim sampler through the ordinary character animation system");
        Check(!reachable.Any(c => c.operand is FieldInfo field && field.Name == nameof(AnimatedRenderable.AnimProcessors)),
            "the real swimming branch never replaces or clears mod animation processors");

        var render = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(KittenRenderable), nameof(KittenRenderable.UpdateRenderData)));
        int state = render.FindIndex(c => c.Calls(locomotion));
        int animation = render.FindIndex(c => c.operand is MethodInfo method && method.DeclaringType == typeof(AnimatedRenderable)
            && method.Name == nameof(AnimatedRenderable.UpdateAnimation));
        Check(state >= 0 && animation > state, "native render data evaluates the selected swimming clip before the shared animation callbacks");
        var update = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(AnimatedRenderable), nameof(AnimatedRenderable.UpdateAnimation)));
        Check(update.Any(c => c.operand is MethodInfo method && method.Name == nameof(IAnimProcessor.UpdateLocalPose))
            && update.Any(c => c.operand is MethodInfo method && method.Name == nameof(IAnimProcessor.UpdateSkeleton))
            && !update.Any(c => c.operand is FieldInfo field && field.DeclaringType == typeof(LocomotionState)),
            "the native local and world processor callbacks share one mode-independent evaluation path");
    }

    private static List<CodeInstruction> Reachable(List<CodeInstruction> code, Label start)
    {
        var labels = new Dictionary<Label, int>();
        for (int index = 0; index < code.Count; index++)
            foreach (Label label in code[index].labels) labels[label] = index;
        var visited = new HashSet<int>(); var pending = new Stack<int>();
        pending.Push(labels[start]);
        while (pending.TryPop(out int index))
        {
            if ((uint)index >= (uint)code.Count || !visited.Add(index)) continue;
            var instruction = code[index];
            if (instruction.operand is Label branch && labels.TryGetValue(branch, out int target)) pending.Push(target);
            if (instruction.operand is Label[] targets)
                foreach (Label label in targets) pending.Push(labels[label]);
            if (instruction.opcode.FlowControl is not (FlowControl.Return or FlowControl.Throw or FlowControl.Branch)) pending.Push(index + 1);
        }
        return visited.Select(index => code[index]).ToList();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Swimming native animation check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
