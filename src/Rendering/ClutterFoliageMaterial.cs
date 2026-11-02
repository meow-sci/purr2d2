using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using KSA;
using KSA.Rendering.Raytracing;

namespace Purr2D2.Rendering;

/// <summary>Opt-in native draw-data bridge; original material assets and all ordinary draws are untouched.</summary>
internal static class ClutterFoliageMaterial
{
    private static bool _mainCompatible, _raytracedCompatible;
    internal static bool Ready => Volatile.Read(ref _mainCompatible) && Volatile.Read(ref _raytracedCompatible);

    internal static void Install(Harmony harmony)
    {
        ValidateShadowLayout();
        var main = AccessTools.Method(typeof(PartModel), nameof(PartModel.WriteInstancesToGpu), [typeof(IViewport), typeof(int)])
            ?? throw new MissingMethodException("Native part foliage upload changed.");
        var raytraced = AccessTools.Method(typeof(PartModelRaytracingData), nameof(PartModelRaytracingData.WriteInstancesToGpu),
            [typeof(PartModel), typeof(int), typeof(int)])
            ?? throw new MissingMethodException("Native raytraced foliage upload changed.");
        harmony.Patch(main, transpiler: new HarmonyMethod(typeof(ClutterFoliageMaterial), nameof(Transpile)));
        harmony.Patch(raytraced, transpiler: new HarmonyMethod(typeof(ClutterFoliageMaterial), nameof(Transpile)));
        if (!Ready) throw new InvalidOperationException("Native foliage draw-data hooks are unavailable.");
    }

    internal static void Reset() { _mainCompatible = _raytracedCompatible = false; }

    internal static void ValidateShadowLayout()
    {
        var padding = AccessTools.Field(typeof(PartModel.PerInstanceData), "packing1");
        if (padding?.FieldType != typeof(int) || padding.IsPublic
            || Unsafe.SizeOf<PartModel.PerInstanceData>() != 80 || Unsafe.SizeOf<PartModelDynamic.PerInstanceData>() != 80
            || Marshal.OffsetOf<PartModel.PerInstanceData>("packing1").ToInt32() != 72
            || Marshal.OffsetOf<PartModel.PerInstanceData>(nameof(PartModel.PerInstanceData.StateBitFlag)).ToInt32() != 64
            || Marshal.OffsetOf<PartModel.PerInstanceData>(nameof(PartModel.PerInstanceData.EmissiveColor)).ToInt32() != 68
            || Marshal.OffsetOf<PartModel.PerInstanceData>(nameof(PartModel.PerInstanceData.Wetness)).ToInt32() != 76
            || Marshal.OffsetOf<PartModelDynamic.PerInstanceData>(nameof(PartModelDynamic.PerInstanceData.TfiThickness)).ToInt32() != 72)
            throw new InvalidDataException("Native foliage shadow instance padding or 80-byte stride changed.");
    }

    internal static void MarkShadow(ref PartModel.PerInstanceData instance)
    {
        // The native static-part padding is deliberately left unnamed in color
        // shaders. Give only generated leaf instances a shadow-only identifier.
        int marker = ClutterFoliageSurface.ShadowMarker;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(new Span<PartModel.PerInstanceData>(ref instance)).Slice(72, 4), in marker);
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var original = instructions.ToList();
        bool main = __originalMethod.DeclaringType == typeof(PartModel);
        try
        {
            var result = RewriteDrawData(original, main ? 0 : 1);
            if (main) Volatile.Write(ref _mainCompatible, true);
            else Volatile.Write(ref _raytracedCompatible, true);
            return result;
        }
        catch (Exception error)
        {
            if (main) Volatile.Write(ref _mainCompatible, false);
            else Volatile.Write(ref _raytracedCompatible, false);
            Console.WriteLine("[purr2d2] Native foliage draw-data contract unavailable: " + error.Message);
            return original;
        }
    }

    // Checked against the actual host IL without constructing a renderer in tests.
    internal static List<CodeInstruction> RewriteDrawData(IEnumerable<CodeInstruction> instructions, int modelArgument)
    {
        if (modelArgument is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(modelArgument));
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        var opacity = AccessTools.Field(typeof(PartModel.PerDrawData), nameof(PartModel.PerDrawData.EmissiveTextureIndex));
        var thickness = AccessTools.Field(typeof(PartModel.PerDrawData), nameof(PartModel.PerDrawData.TfiTextureIndex));
        if (opacity?.FieldType != typeof(int) || thickness?.FieldType != typeof(int)
            || codes.Count(c => c.StoresField(opacity)) != 1 || codes.Count(c => c.StoresField(thickness)) != 1)
            throw new InvalidDataException("Native foliage draw texture fields changed.");
        var result = new List<CodeInstruction>(codes.Count + 4);
        foreach (var code in codes)
        {
            if (code.StoresField(opacity) || code.StoresField(thickness))
            {
                var load = new CodeInstruction(modelArgument == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                // Branches targeting the original store must include the adapter.
                load.labels.AddRange(code.labels); code.labels.Clear();
                load.blocks.AddRange(code.blocks); code.blocks.Clear();
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClutterFoliageMaterial),
                    code.StoresField(opacity) ? nameof(Opacity) : nameof(Thickness))));
            }
            result.Add(code);
        }
        return result;
    }

    private static int Opacity(int original, PartModel model)
    {
        if (!TryMaterial(model, out var material)) return original;
        int texture = material.OpacityMap?.Get().BindlessHandle ?? TextureReference.EmptyWhite.BindlessHandle;
        return ClutterFoliageSurface.Encode(texture, (int)material.ColorPipelineFlags);
    }

    private static int Thickness(int original, PartModel model) => TryMaterial(model, out var material)
        ? material.ThicknessMap?.Get().BindlessHandle ?? -1 : original;

    private static bool TryMaterial(PartModel model, out GroundClutterMaterialReference material)
    {
        material = null!;
        if (!ClutterFoliageSurface.IsMesh(model.Template.Mesh?.Id)) return false;
        material = model.Template.Material as GroundClutterMaterialReference
            ?? throw new InvalidDataException("Generated foliage lost its native ground material.");
        return true;
    }
}
