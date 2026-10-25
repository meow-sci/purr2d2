using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;
using KSA.Rendering.Lighting;
using Purr2D2.Rendering;

// Inspect/patch the real host method without constructing a renderer or native window.
internal static class EnvironmentLightingChecks
{
    internal static void Run()
    {
        EnvironmentLightingPatch.ValidateCapacity();
        var target = AccessTools.Method(typeof(ClusteredLightSystem), nameof(ClusteredLightSystem.WriteForwardLights));
        var getter = AccessTools.Method(typeof(EnvironmentLightingPatch), nameof(EnvironmentLightingPatch.GetLimit));
        var original = PatchProcessor.GetOriginalInstructions(target);
        var transformed = EnvironmentLightingPatch.ReplaceLimit(original);
        int limit = original.FindIndex(c => c.LoadsConstant(8));
        Check(transformed.Count == original.Count + 1 && transformed[limit].opcode == OpCodes.Ldarg_1
            && transformed[limit + 1].Calls(getter), "only the cap becomes a viewport-aware runtime call");
        for (int i = 0; i < original.Count; i++)
        {
            var next = transformed[i <= limit ? i : i + 1];
            Check(i == limit || next.opcode == original[i].opcode && Equals(next.operand, original[i].operand),
                "secondary-view budget, light ranking, packing and final count stay intact");
            Check(next.labels.SequenceEqual(original[i].labels) && next.blocks.SequenceEqual(original[i].blocks),
                "native branch labels and exception boundaries stay attached to their instructions");
        }
        Check(original[limit].LoadsConstant(8), "the transformation never mutates its input");
        Reject(c => c.RemoveAt(limit), "missing limit");
        Reject(c => c.Add(new CodeInstruction(OpCodes.Ldc_I4_8)), "ambiguous limit");
        Reject(c => c[limit] = new CodeInstruction(OpCodes.Ldc_I4, 12), "changed stock cap");
        Reject(c => c[limit + 1] = new CodeInstruction(OpCodes.Stloc_0), "different destination local");
        Reject(c => c[limit - 4] = new CodeInstruction(OpCodes.Ldc_I4, 32), "changed native capacity");
        Reject(c => c[limit].labels.Clear(), "missing clustered branch destination");
        Reject(c => c[limit + 2].labels.Clear(), "changed branch join");

        var harmony = new Harmony("purr2d2.tests.environment-lighting");
        bool enabled = false, requested = false;
        var viewport = DispatchProxy.Create<IViewport, LightingViewport>();
        var view = (LightingViewport)viewport;
        try
        {
            EnvironmentLightingPatch.Install(harmony, () => enabled && requested);
            Check(EnvironmentLightingPatch.UnavailableReason == null, "actual host patch installs successfully");
            Check(PatchProcessor.GetCurrentInstructions(target).Count(c => c.Calls(getter)) == 1,
                "Harmony installs exactly one runtime cap decision in the real method");
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "disabled mod preserves stock lighting");
            enabled = true;
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "default-off preference preserves stock lighting");
            requested = true;
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 16, "opting in changes the next cap read without repatching");
            requested = false;
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "opting out restores eight without repatching");
            requested = true;
            enabled = false;
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "disabling the mod overrides a saved true preference");
            enabled = true;
            foreach (var type in new[] { ViewportType.Secondary, ViewportType.CharacterPortrait, ViewportType.PartThumbnail })
            {
                view.Type = type;
                Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "only the main view can receive the increase");
            }
            view.Type = ViewportType.Main;
            foreach (var mode in new[] { ViewportLightMode.Forward, ViewportLightMode.None })
            {
                view.Mode = mode;
                Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "forward and unlit views retain their native paths");
            }
            view.Mode = ViewportLightMode.Clustered;

            // A later Harmony rebuild must fail closed without breaking another mod's instructions.
            var broken = original.Select(c => new CodeInstruction(c)).ToList();
            broken[limit].opcode = OpCodes.Ldc_I4;
            broken[limit].operand = 12;
            var transpile = AccessTools.Method(typeof(EnvironmentLightingPatch), "Transpile");
            var retained = ((IEnumerable<CodeInstruction>)transpile.Invoke(null, [broken])!).ToList();
            Check(retained.SequenceEqual(broken) && EnvironmentLightingPatch.UnavailableReason != null
                && EnvironmentLightingPatch.GetLimit(viewport) == 8,
                "changed host code is retained intact, reports unavailable and disables the increase");
            _ = transpile.Invoke(null, [original]);
            Check(EnvironmentLightingPatch.UnavailableReason == null && EnvironmentLightingPatch.GetLimit(viewport) == 16,
                "a compatible rebuild restores the dynamic decision");
            EnvironmentLightingPatch.Release();
            Check(EnvironmentLightingPatch.GetLimit(viewport) == 8, "shutdown releases the runtime and restores stock behavior");
        }
        finally
        {
            EnvironmentLightingPatch.Release();
            harmony.UnpatchAll(harmony.Id);
        }
        Console.WriteLine("PASS: real KSA forward-light ABI/IL, runtime 8/16 toggle, viewport isolation, shutdown and incompatible-rebuild fallback");

        void Reject(Action<List<CodeInstruction>> mutate, string name)
        {
            var changed = original.Select(c => new CodeInstruction(c)).ToList();
            mutate(changed);
            try { EnvironmentLightingPatch.ReplaceLimit(changed); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Environment-light patch accepted " + name);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Environment lighting: " + message);
    }
}

public class LightingViewport : DispatchProxy
{
    public ViewportType Type = ViewportType.Main;
    public ViewportLightMode Mode = ViewportLightMode.Clustered;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_Type" => Type,
        "get_LightMode" => Mode,
        _ => throw new InvalidOperationException("Unexpected viewport access: " + targetMethod?.Name)
    };
}
