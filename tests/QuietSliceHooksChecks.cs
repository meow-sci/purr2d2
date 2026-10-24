using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA;
using Purr2D2.Slicing;

internal static class QuietSliceHooksChecks
{
    internal static void Run()
    {
        var job = typeof(PhysicsBubble).GetNestedType("UpdateModulesJob", BindingFlags.NonPublic)!;
        MethodInfo[] methods = [AccessTools.Method(job, "ExecuteOnBubble"), AccessTools.Method(typeof(PhysicsBubble), "ApplySingleVehicleMotion")];
        var target = AccessTools.Method(typeof(VehicleUpdateState), nameof(VehicleUpdateState.UpdateModules));
        var replacement = AccessTools.Method(typeof(QuietSliceQuiescence), "UpdateModules");
        int count = 0;
        foreach (var method in methods)
        {
            var original = PatchProcessor.GetOriginalInstructions(method);
            var replaced = QuietSliceQuiescence.ReplaceModuleCalls(original).ToList();
            Require(original.Count == replaced.Count, "Module gate must preserve native instruction count.");
            for (int i = 0; i < original.Count; i++)
            {
                if (original[i].Calls(target)) { count++; Require(replaced[i].Calls(replacement), "Every tick call must use the owned gate."); }
                else Require(original[i].opcode == replaced[i].opcode && Equals(original[i].operand, replaced[i].operand), "Unrelated native work changed.");
                Require(original[i].labels.SequenceEqual(replaced[i].labels) && original[i].blocks.SequenceEqual(replaced[i].blocks), "Native branch/exception layout changed.");
            }
        }
        Require(count == 3, "Pinned host must contain exactly three module tick call sites.");
        bool refused = false;
        try { QuietSliceQuiescence.ReplaceModuleCalls([]).ToList(); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "Missing native calls must refuse patching.");
        var harmony = new Harmony("purr2d2.validation.quiet-slicing");
        try
        {
            QuietSliceQuiescence.Install(harmony);
            foreach (var method in methods)
            {
                Require(Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true, "Production quiet gate failed to install.");
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            var patched = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains(harmony.Id) == true).ToArray();
            Require(patched.Length == 5, "Both module gates and all three observation-only failure hooks must install.");
            foreach (var method in patched) RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
        finally { harmony.UnpatchAll(harmony.Id); QuietSliceQuiescence.Clear(); }
        Console.WriteLine("PASS: actual KSA quiet module gate audits, installs and JITs all three caller sites with unchanged native control flow; native physics not invoked");
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
