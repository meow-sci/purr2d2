using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA;
using KSA.Rendering.Lighting;

namespace Purr2D2.Rendering;

/// <summary>Opt-in use of the stock sixteen-slot forward-light buffer in the main view.</summary>
internal static class EnvironmentLightingPatch
{
    private const int StockLimit = 8, ExtendedLimit = 16;
    private static Func<bool>? _requested;
    private static bool _compatible;
    internal static string? UnavailableReason { get; private set; }

    internal static void Install(Harmony harmony, Func<bool> requested)
    {
        _requested = requested;
        _compatible = false;
        try
        {
            var target = AccessTools.Method(typeof(ClusteredLightSystem), nameof(ClusteredLightSystem.WriteForwardLights),
                [typeof(IViewport), typeof(UboLightingData).MakeByRefType()])
                ?? throw new MissingMethodException("KSA forward-light upload changed.");
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(EnvironmentLightingPatch), nameof(Transpile)));
        }
        catch (Exception error) { Unavailable(error.Message); }
    }

    // Release the runtime on shutdown; any still-installed call immediately returns the stock limit.
    internal static void Release() => _requested = null;

    internal static int GetLimit(IViewport viewport) => _compatible && _requested?.Invoke() == true
        && viewport.Type == ViewportType.Main && viewport.LightMode == ViewportLightMode.Clustered
            ? ExtendedLimit : StockLimit;

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var original = instructions.ToList();
        try
        {
            ValidateCapacity();
            var result = ReplaceLimit(original);
            _compatible = true;
            UnavailableReason = null;
            return result;
        }
        catch (Exception error)
        {
            // Also runs when another mod causes Harmony to rebuild this method later.
            // Return its incoming instructions intact if our seam is no longer recognized.
            Unavailable(error.Message);
            return original;
        }
    }

    private static void Unavailable(string reason)
    {
        _compatible = false;
        string message = "Extended environment lighting is unavailable: " + reason;
        if (UnavailableReason != message) Console.WriteLine("[purr2d2] " + message);
        UnavailableReason = message;
    }

    internal static void ValidateCapacity()
    {
        var array = typeof(UboLightingData.ForwardLightArray);
        var limit = AccessTools.Field(typeof(UboLightingData), nameof(UboLightingData.MAX_FORWARD_LIGHTS));
        if (array.GetCustomAttribute<InlineArrayAttribute>()?.Length != ExtendedLimit
            || limit?.GetRawConstantValue() is not int value || value != ExtendedLimit)
            throw new InvalidOperationException("KSA's forward-light buffer layout changed.");
        foreach (string name in new[] { nameof(UboLightingData.ForwardLightPositionRange),
                     nameof(UboLightingData.ForwardLightColorSpotOffset), nameof(UboLightingData.ForwardLightDirectionSpotScale) })
            if (AccessTools.Field(typeof(UboLightingData), name)?.FieldType != array)
                throw new InvalidOperationException("KSA's forward-light arrays changed.");

        var candidates = AccessTools.Field(typeof(ClusteredLightSystem), "_forwardCandidates");
        var constructors = typeof(ClusteredLightSystem).GetConstructors();
        if (candidates?.FieldType.IsArray != true || constructors.Length != 1)
            throw new InvalidOperationException("KSA's forward-light candidate storage changed.");
        var codes = PatchProcessor.GetOriginalInstructions(constructors[0]);
        int store = codes.FindIndex(c => c.StoresField(candidates));
        if (store < 2 || codes.Count(c => c.StoresField(candidates)) != 1
            || codes[store - 1].opcode != OpCodes.Newarr
            || !Equals(codes[store - 1].operand, candidates.FieldType.GetElementType())
            || !codes[store - 2].LoadsConstant(ExtendedLimit))
            throw new InvalidOperationException("KSA no longer reserves sixteen forward-light candidates.");
    }

    // Inspectable against real host IL; only the clustered branch's constant is replaced.
    internal static List<CodeInstruction> ReplaceLimit(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        var clamp = AccessTools.Method(typeof(int), nameof(int.Clamp), [typeof(int), typeof(int), typeof(int)]);
        var mode = AccessTools.PropertyGetter(typeof(IViewport), nameof(IViewport.LightMode));
        int index = codes.FindIndex(c => c.LoadsConstant(StockLimit));
        if (index < 6 || index + 2 >= codes.Count || codes.Count(c => c.LoadsConstant(StockLimit)) != 1
            || !codes[index - 5].LoadsConstant(1) || !codes[index - 4].LoadsConstant(ExtendedLimit)
            || !codes[index - 3].Calls(clamp) || !codes[index - 2].IsStloc()
            || codes[index - 1].opcode.FlowControl != FlowControl.Branch
            || codes[index - 1].operand is not Label join || !codes[index + 2].labels.Contains(join)
            || codes[index + 1].opcode != codes[index - 2].opcode
            || !Equals(codes[index + 1].operand, codes[index - 2].operand)
            || codes.Skip(index - 5).Take(8).Any(c => c.blocks.Count != 0))
            throw new InvalidOperationException("KSA's eight-light selection branch changed or is ambiguous.");

        int branches = 0;
        for (int i = 2; i < index; i++)
            if ((codes[i].opcode == OpCodes.Bne_Un || codes[i].opcode == OpCodes.Bne_Un_S)
                && codes[i].operand is Label label && codes[index].labels.Contains(label)
                && codes[i - 1].LoadsConstant((int)ViewportLightMode.Forward) && codes[i - 2].Calls(mode))
                branches++;
        if (branches != 1)
            throw new InvalidOperationException("KSA's forward/clustered light selection changed.");

        // Retain the branch label on the first replacement instruction.
        codes[index].opcode = OpCodes.Ldarg_1;
        codes[index].operand = null;
        codes.Insert(index + 1, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(EnvironmentLightingPatch), nameof(GetLimit))));
        return codes;
    }
}
