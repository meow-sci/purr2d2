using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using KSA;
using KSA.Rendering.Raytracing;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using RenderCore;

internal static class ClutterFoliageHostChecks
{
    internal static void Run()
    {
        ClutterFoliageMaterial.ValidateShadowLayout();
        var leafInstance = new PartModel.PerInstanceData
        {
            StateBitFlag = unchecked((int)0xfedcba98), EmissiveColor = 0x12345678, Wetness = .65f
        };
        var originalInstance = MemoryMarshal.AsBytes(new Span<PartModel.PerInstanceData>(ref leafInstance)).ToArray();
        ClutterFoliageMaterial.MarkShadow(ref leafInstance);
        var markedInstance = MemoryMarshal.AsBytes(new Span<PartModel.PerInstanceData>(ref leafInstance)).ToArray();
        Check(float.IsNaN(BitConverter.Int32BitsToSingle(ClutterFoliageSurface.ShadowMarker))
            && BitConverter.ToInt32(markedInstance, 72) == ClutterFoliageSurface.ShadowMarker
            && originalInstance.AsSpan(0, 72).SequenceEqual(markedInstance.AsSpan(0, 72))
            && originalInstance.AsSpan(76).SequenceEqual(markedInstance.AsSpan(76)),
            "shadow marker changes only checked static padding72, preserving every matrix/paint/state/emission/wetness byte; finite dynamic TFI cannot collide");
        var opacityField = AccessTools.Field(typeof(PartModel.PerDrawData), nameof(PartModel.PerDrawData.EmissiveTextureIndex));
        var thicknessField = AccessTools.Field(typeof(PartModel.PerDrawData), nameof(PartModel.PerDrawData.TfiTextureIndex));
        var opacityCall = AccessTools.Method(typeof(ClutterFoliageMaterial), "Opacity");
        var thicknessCall = AccessTools.Method(typeof(ClutterFoliageMaterial), "Thickness");
        var main = AccessTools.Method(typeof(PartModel), nameof(PartModel.WriteInstancesToGpu));
        var raytraced = AccessTools.Method(typeof(PartModelRaytracingData), nameof(PartModelRaytracingData.WriteInstancesToGpu));
        var shadowCompiler = AccessTools.Method(typeof(PartModelRenderer.DepthData), "CompileShadowVertex");
        var variantCompiler = AccessTools.Method(typeof(ShaderReference), nameof(ShaderReference.CompileVariantWithCustomOptions));
        var initialCompiler = AccessTools.Method(typeof(ShaderReference), "Compile");
        var fromFile = typeof(ShaderModuleUtils).GetMethods().Single(m => m.Name == nameof(ShaderModuleUtils.FromFile)
            && m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(string));
        Check(PatchProcessor.GetOriginalInstructions(shadowCompiler).Count(c => c.Calls(variantCompiler)) == 1
            && PatchProcessor.GetOriginalInstructions(variantCompiler).Count(c => c.Calls(fromFile)) == 1
            && PatchProcessor.GetOriginalInstructions(initialCompiler).Count(c => c.Calls(fromFile)) == 1,
            "actual native cached and deformed shadow shaders both compile through the guarded FromFile route");
        foreach (string pipeline in new[] { "CreateCsmPipeline", "CreateLightSystemPipeline" })
            Check(PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(PartModelRenderer.DepthData), pipeline))
                .Count(c => c.Calls(shadowCompiler)) == 1,
                "both actual native shadow pipelines use the checked compilation route");
        var constructor = typeof(PartModel).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(PartModelModule.Template)], null)!;
        var constructorCodes = PatchProcessor.GetOriginalInstructions(constructor);
        var rayMode = AccessTools.Field(typeof(PartModelModule.Template), nameof(PartModelModule.Template.RayTracing));
        var rtModels = AccessTools.Field(typeof(PartModel), nameof(PartModel.InstancesRayTrace));
        int rayModeRead = constructorCodes.FindIndex(c => c.LoadsField(rayMode));
        Check((int)PartModelModule.RaytracingMode.Disabled == 0 && rayModeRead >= 0
            && (constructorCodes[rayModeRead + 1].opcode == OpCodes.Brfalse || constructorCodes[rayModeRead + 1].opcode == OpCodes.Brfalse_S)
            && constructorCodes.FindIndex(c => c.LoadsField(rtModels)) > rayModeRead + 1,
            "native Disabled mode bypasses registration in the RT geometry list");
        var (fragment, fixtureMaterials) = FragmentAssetChecks.Fixture();
        var leafMaterials = fixtureMaterials.ToDictionary(p => p.Key, p => p.Value);
        leafMaterials[42] = new("FixtureNativeLeaves", false, 0, ClutterSurfaceTint.Luminosity(.2f), true, true, true);
        var asset = FragmentAssetWriter.Build(fragment with { Foliage = [fragment.Solids[0][0] with { Material = 42 }] }, leafMaterials, default);
        var decoded = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), asset.Xml, asset.Id, asset.Uploads.Length);
        var leafTemplate = decoded.Assets.OfType<PartTemplate>().SelectMany(p => p.Components).OfType<PartModelModule.Template>()
            .Single(p => ClutterFoliageSurface.IsMesh(p.Mesh?.Id));
        Check(!leafTemplate.ShadowCaster && leafTemplate.RayTracing == PartModelModule.RaytracingMode.Disabled
            && leafTemplate.Material?.Id == "FixtureNativeLeaves",
            "actual native XML loader excludes leaf point-shadow/RT geometry and retains the original native material ID");
        foreach (var (method, argument) in new[] { (main, 0), (raytraced, 1) })
        {
            var original = PatchProcessor.GetOriginalInstructions(method);
            var rewritten = ClutterFoliageMaterial.RewriteDrawData(original, argument);
            Check(rewritten.Count == original.Count + 4 && rewritten.Count(c => c.Calls(opacityCall)) == 1
                && rewritten.Count(c => c.Calls(thicknessCall)) == 1, "each actual native upload receives exactly two descriptor adapters");
            var stripped = rewritten.Where(c => !c.Calls(opacityCall) && !c.Calls(thicknessCall)).ToList();
            foreach (var field in new[] { opacityField, thicknessField })
            {
                int store = stripped.FindIndex(c => c.StoresField(field));
                Check(stripped[store - 1].opcode == (argument == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1),
                    "adapter receives the correct real PartModel argument");
                stripped.RemoveAt(store - 1);
            }
            Check(stripped.Zip(original).All(p => p.First.opcode == p.Second.opcode && Equals(p.First.operand, p.Second.operand)),
                "all ordinary draw, instance, dent and shadow upload instructions remain unchanged");
            bool refused = false;
            try { ClutterFoliageMaterial.RewriteDrawData(original.Where(c => !c.StoresField(opacityField)), argument); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "an altered native texture layout fails closed");
        }

        var harmony = new Harmony("purr2d2.tests.foliage-material");
        try
        {
            ClutterFoliageMaterial.Install(harmony);
            Check(ClutterFoliageMaterial.Ready && new[] { main, raytraced }.All(m =>
                PatchProcessor.GetCurrentInstructions(m).Count(c => c.Calls(opacityCall)) == 1),
                "both actual native hooks install before foliage can be admitted");
            var opacity = new TextureReference();
            var thickness = new TextureReference();
            AccessTools.Property(typeof(TextureReference), nameof(TextureReference.BindlessHandle)).SetValue(opacity, 713);
            AccessTools.Property(typeof(TextureReference), nameof(TextureReference.BindlessHandle)).SetValue(thickness, 919);
            var material = new GroundClutterMaterialReference
            {
                Id = "FixtureNativeLeaves", OpacityMap = opacity, ThicknessMap = thickness,
                ColorPipelineFlags = GroundClutterMaterialReference.PipelineFlags.UseOpacityTexture
                    | GroundClutterMaterialReference.PipelineFlags.DoubleSided
                    | GroundClutterMaterialReference.PipelineFlags.Transmissive
                    | GroundClutterMaterialReference.PipelineFlags.KeepBackfaceNormals
                    | GroundClutterMaterialReference.PipelineFlags.UsesCustomNormals
            };
            var model = (PartModel)RuntimeHelpers.GetUninitializedObject(typeof(PartModel));
            model.Template = new PartModelModule.Template
            {
                Mesh = new MeshReference { Id = "Purr2D2_Fragment_fixture_s0_fol_m0" }, Material = material
            };
            int encoded = (int)opacityCall.Invoke(null, [33, model])!;
            Check(ClutterFoliageSurface.Texture(encoded) == 713
                && ClutterFoliageSurface.Flags(encoded) == (int)material.ColorPipelineFlags
                && (int)thicknessCall.Invoke(null, [44, model])! == 919,
                "native source opacity/thickness descriptors and foliage flags reach the existing draw buffer");
            model.Template.Mesh.Id = "Purr2D2_Fragment_fixture_s0_m0";
            Check((int)opacityCall.Invoke(null, [33, model])! == 33 && (int)thicknessCall.Invoke(null, [44, model])! == 44,
                "opaque generated wood/caps and ordinary part materials retain their descriptors");
            Check(ReferenceEquals(model.Template.Material, material) && material.OpacityMap == opacity && material.ThicknessMap == thickness,
                "the adapter never changes source materials or texture ownership");
            // Avoid the native finalizer on a deliberately unconstructed test object.
            GC.SuppressFinalize(model);
        }
        finally { harmony.UnpatchAll(harmony.Id); ClutterFoliageMaterial.Reset(); }
        Console.WriteLine("PASS: actual native main/raytraced foliage upload IL, guarded Harmony hooks, source texture descriptors/flags, ordinary material isolation and unchanged ownership.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Foliage material: " + message); }
}
