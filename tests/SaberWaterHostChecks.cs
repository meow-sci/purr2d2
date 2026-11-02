using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering;
using KSA.Rendering.Water.Rendering;
using KSA.Rendering.Water.Rendering.ShaderStructs;

// Inspect the actual host assembly without constructing a renderer, initializing
// GPU-owned native singletons, recording commands or claiming a visual result.
internal static class SaberWaterHostChecks
{
    internal static void Run()
    {
        HooksAndNativeFields();
        RenderOrder();
        NativeUniformAbi();
        DescriptorContracts();
        TargetLifetimeAndResolve();
    }

    private static void HooksAndNativeFields()
    {
        var copy = AccessTools.DeclaredMethod(typeof(OceanRenderer), "PerformScreenCopy", [typeof(CommandBuffer), typeof(IViewport)]);
        var bloom = AccessTools.DeclaredMethod(typeof(SunbloomRenderer), nameof(SunbloomRenderer.Render),
            [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);
        Check(copy is { IsPrivate: true, ReturnType: var copyReturn } && copyReturn == typeof(void)
            && bloom is { IsPublic: true, ReturnType: var bloomReturn } && bloomReturn == typeof(void),
            "native opaque-copy and post-underwater/pre-bloom hook signatures match the water pass");
        foreach (var field in new (string Name, Type Type)[] {
            ("_target", typeof(RenderTarget)), ("_depthCopyTarget", typeof(RenderTarget)),
            ("_sharedOceanDataDescriptorSetLayout", typeof(DescriptorSetLayoutEx)),
            ("_oceanDataDescriptorSetLayout", typeof(DescriptorSetLayoutEx)), ("_sharedDataDescriptorSet", typeof(VkDescriptorSet)) })
            Check(typeof(OceanRenderer).GetField(field.Name, BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == field.Type,
                "native borrowed water resource field has the expected type: " + field.Name);
        Check(typeof(OceanRenderer).GetField("_celestialToOceanRenderData")?.FieldType == typeof(Dictionary<KeyHash, OceanRenderData>)
            && typeof(OceanRenderData).GetField(nameof(OceanRenderData.OceanDataDescriptorSets))?.FieldType == typeof(VkDescriptorSet[])
            && typeof(OceanRenderData).GetField(nameof(OceanRenderData.OceanRadius))?.FieldType == typeof(double)
            && typeof(OceanRenderData).GetField(nameof(OceanRenderData.MaxDisplacement))?.FieldType == typeof(double)
            && typeof(OceanRenderer).GetField(nameof(OceanRenderer.DynamicOceanDataStride))?.FieldType == typeof(ByteSize),
            "native per-celestial sets, double ocean geometry and dynamic offset stride retain their public ABI");
    }

    private static void RenderOrder()
    {
        var game = Code(typeof(KSA.Program), "RenderGame");
        int main = FindCall(game, "SuperMeshRenderSystem", "RenderMainPass");
        int opaqueEnd = FindCall(game, nameof(RenderTarget), nameof(RenderTarget.EndRendering), main + 1);
        int particles = FindCall(game, "ParticleSystem", "WriteCommandsColorOpaque", opaqueEnd + 1);
        int ocean = FindCall(game, nameof(OceanRenderer), nameof(OceanRenderer.Render), particles + 1);
        int translucent = FindCall(game, "SuperMeshRenderSystem", "RenderTranslucencyPass", ocean + 1);
        int resolve = FindCall(game, nameof(RenderTarget), nameof(RenderTarget.ResolveAttachments), translucent + 1);
        int underwater = FindCall(game, nameof(OceanRenderer), nameof(OceanRenderer.RenderUnderwater), resolve + 1);
        int sunbloom = FindCall(game, nameof(SunbloomRenderer), nameof(SunbloomRenderer.Render), underwater + 1);
        int bloom = FindCall(game, "OverallBloomRenderer", "Render", sunbloom + 1);
        int toMsaa = FindCall(game, "SingleToMultisamplePass", "Run", bloom + 1);
        Check(new[] { main, opaqueEnd, particles, ocean, translucent, resolve, underwater, sunbloom, bloom, toMsaa }
                .All(index => index >= 0) && game[resolve - 1].LoadsConstant(0),
            "actual main-view IL orders opaque resolve, ocean, late translucency, color-only resolve, underwater, both blooms and MSAA reload");
        Check(game.Skip(underwater + 1).Take(sunbloom - underwater - 1).Any(c => Field(c, nameof(ImageBarrierInfo.Presets.StorageReadFc)))
            && game.Skip(underwater + 1).Take(sunbloom - underwater - 1).Any(c => Field(c, nameof(ImageBarrierInfo.Presets.DepthSampledReadVfc))),
            "native sunbloom entry expects resolved color in General storage-read and depth in sampled-read layout");
        var secondary = Code(typeof(KSA.Program), "RenderViewport");
        Check(!secondary.Any(c => c.operand is MethodInfo method && method.DeclaringType == typeof(OceanRenderer)
                && method.Name is nameof(OceanRenderer.Render) or nameof(OceanRenderer.RenderUnderwater)),
            "the secondary viewport render path does not provide a main-camera ocean context");
        var render = Code(typeof(OceanRenderer), nameof(OceanRenderer.Render));
        int copy = FindCall(render, nameof(OceanRenderer), "PerformScreenCopy");
        int waves = FindCall(render, nameof(OceanRenderer), "PerformRendering");
        Check(copy >= 0 && waves > copy, "the native ocean copies opaque depth before writing displaced-wave surface depth");
        var screenCopy = Code(typeof(OceanRenderer), "PerformScreenCopy");
        Check(screenCopy.Any(c => Field(c, "_depthCopyTarget")) && screenCopy.Any(c => c.operand is MethodInfo method
                && method.Name == "Dispatch" && (method.DeclaringType == typeof(CommandBuffer)
                    || method.GetParameters().FirstOrDefault()?.ParameterType == typeof(CommandBuffer)))
            && screenCopy.Any(c => Field(c, nameof(ImageBarrierInfo.Presets.StorageReadWriteFc))),
            "the native screen-copy hook runs after a real compute dispatch and preserves the borrowed copy in General layout");
    }

    private static void NativeUniformAbi()
    {
        Check(Marshal.SizeOf<StaticOceanData>() == 256 && Marshal.SizeOf<DynamicOceanData>() == 512
            && Marshal.SizeOf<CascadeDataArray>() == 80 && Marshal.SizeOf<UnderwaterData>() == 16,
            "native ocean uniform blocks retain their 256/512-byte layouts and five cascade vectors");
        Check(Offsets<StaticOceanData>((nameof(StaticOceanData.OceanRadius), 0), (nameof(StaticOceanData.OceanLevel), 4),
            (nameof(StaticOceanData.Color), 8), (nameof(StaticOceanData.TransparencyDepth), 12),
            (nameof(StaticOceanData.TessellationEdgeLengthPixels), 16), (nameof(StaticOceanData.TessellationRangeMeters), 20),
            (nameof(StaticOceanData.TerrainMinHeight), 24), (nameof(StaticOceanData.TerrainMaxHeight), 28),
            (nameof(StaticOceanData.FoamAmount), 32), (nameof(StaticOceanData.WaveCount), 36),
            (nameof(StaticOceanData.WaveAmplitude), 40), (nameof(StaticOceanData.Wavelength), 44),
            (nameof(StaticOceanData.WaveSpeed), 48), (nameof(StaticOceanData.AmplitudePersistence), 52),
            (nameof(StaticOceanData.WavelengthPersistence), 56), (nameof(StaticOceanData.SpeedPersistence), 60)),
            "native static ocean fields match the included shader's radius, color, transparency and wave ABI");
        Check(Offsets<DynamicOceanData>((nameof(DynamicOceanData.WorldToPlanet), 0), (nameof(DynamicOceanData.BillboardMatrix), 64),
            (nameof(DynamicOceanData.FrameNormalAndTime), 128), (nameof(DynamicOceanData.FrameTangent), 144),
            (nameof(DynamicOceanData.FrameBitangent), 160), (nameof(DynamicOceanData.CascadeData), 176)),
            "native dynamic ocean matrices, frame axes and cascade offsets match the shader include ABI");
        Check(Offsets<UboCameraData>((nameof(UboCameraData.InverseView), 192), (nameof(UboCameraData.InverseProjection), 256),
            (nameof(UboCameraData.NearPlane), 336), (nameof(UboCameraData.ScreenWidth), 348), (nameof(UboCameraData.ScreenHeight), 352)),
            "native Global camera inverse transforms, near plane and framebuffer dimensions retain their shader ABI");
        var initializer = PatchProcessor.GetOriginalInstructions(typeof(OceanRenderer).TypeInitializer!);
        int size = initializer.FindIndex(c => c.operand is MethodInfo method && method.Name == nameof(ByteSize.Of)
            && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual([typeof(DynamicOceanData)]));
        int alignment = initializer.FindIndex(c => Field(c, nameof(Utils.MinUniformBufferOffsetAlignment)));
        int align = initializer.FindIndex(size + 1, c => c.operand is MethodInfo method && method.Name == "AlignTo");
        int stride = initializer.FindIndex(c => c.opcode == OpCodes.Stsfld && Field(c, nameof(OceanRenderer.DynamicOceanDataStride)));
        Check(size >= 0 && alignment > size && align > alignment && stride > align,
            "native dynamic ocean stride is sizeof(DynamicOceanData) aligned to the real device UBO limit");
    }

    private static void DescriptorContracts()
    {
        var build = Code(typeof(OceanRenderer), "Build");
        var bindings = BindingInitializers(build);
        Check(bindings.Count == 5 && bindings.Select(b => b[nameof(VkDescriptorSetLayoutBinding.Binding)]).SequenceEqual([0L, 1L, 2L, 3L, 4L])
            && bindings.Select(b => b[nameof(VkDescriptorSetLayoutBinding.DescriptorCount)]).SequenceEqual([1L, 1L, 5L, 5L, 5L])
            && bindings.All(b => b[nameof(VkDescriptorSetLayoutBinding.DescriptorType)] == (long)VkDescriptorType.CombinedImageSampler
                && (b[nameof(VkDescriptorSetLayoutBinding.StageFlags)] & (long)VkShaderStageFlags.FragmentBit) != 0),
            "native shared ocean set exposes opaque-depth, color and five FFT cascades to fragment shaders");
        var imageLayouts = FieldValues(build, typeof(VkDescriptorImageInfo), nameof(VkDescriptorImageInfo.ImageLayout));
        Check(imageLayouts.Count == 5 && imageLayouts.All(value => value == (long)VkImageLayout.General),
            "native copied depth/color and FFT descriptors all require General image layout");
        var ctor = typeof(OceanRenderer).GetConstructors().Single();
        var counts = PatchProcessor.GetOriginalInstructions(ctor);
        Check(FieldValues(counts, typeof(Utils.BindingCounts), nameof(Utils.BindingCounts.ImageSamplers)).Contains(3)
            && FieldValues(counts, typeof(Utils.BindingCounts), nameof(Utils.BindingCounts.UniformBuffers)).Contains(1)
            && FieldValues(counts, typeof(Utils.BindingCounts), nameof(Utils.BindingCounts.UniformDynamicBuffers)).Contains(1),
            "native individual ocean layout requests three samplers, one static UBO and one dynamic UBO");
        var individual = Code(typeof(OceanRenderer), "CreateOceanDataDescriptorSetLayout");
        int write = FindCall(individual, nameof(Utils), nameof(Utils.WriteBindings));
        Check(write >= 3 && individual.Take(write).Any(c => c.LoadsConstant((long)(VkShaderStageFlags.VertexBit
            | VkShaderStageFlags.TessellationControlBit | VkShaderStageFlags.TessellationEvaluationBit
            | VkShaderStageFlags.FragmentBit | VkShaderStageFlags.ComputeBit))),
            "native individual ocean descriptors include fragment-stage visibility");
        var bindingOrder = FieldValues(Code(typeof(Utils), nameof(Utils.WriteBindings)),
            typeof(VkDescriptorSetLayoutBinding), nameof(VkDescriptorSetLayoutBinding.DescriptorType));
        Check(bindingOrder.SequenceEqual([(long)VkDescriptorType.StorageImage, (long)VkDescriptorType.CombinedImageSampler,
            (long)VkDescriptorType.UniformBuffer, (long)VkDescriptorType.UniformBufferDynamic, (long)VkDescriptorType.StorageBuffer]),
            "native binding writer places the three ocean samplers before static binding three and dynamic binding four");
        var global = BindingInitializers(Code(typeof(GlobalShaderBindings), nameof(GlobalShaderBindings.CreateDescriptors)));
        Check(global.Count == 2 && global.Select(b => b[nameof(VkDescriptorSetLayoutBinding.Binding)]).SequenceEqual([0L, 1L])
            && global.All(b => b[nameof(VkDescriptorSetLayoutBinding.DescriptorCount)] == 1
                && b[nameof(VkDescriptorSetLayoutBinding.DescriptorType)] == (long)VkDescriptorType.UniformBuffer
                && (b[nameof(VkDescriptorSetLayoutBinding.StageFlags)] & (long)VkShaderStageFlags.FragmentBit) != 0),
            "native Global set bindings zero and one are fragment-visible uniform buffers");
        Check(ImageBarrierInfo.Presets.StorageReadFc.Layout == VkImageLayout.General
            && (ImageBarrierInfo.Presets.StorageReadFc.StageMask & VkPipelineStageFlags2.FragmentShaderBit) != 0
            && ImageBarrierInfo.Presets.DepthSampledReadF.Layout == VkImageLayout.DepthStencilReadOnlyOptimal
            && ImageBarrierInfo.Presets.DepthSampledReadVfc.Layout == VkImageLayout.DepthStencilReadOnlyOptimal,
            "actual native barrier presets distinguish borrowed General images from sampled attachment depth");
    }

    private static void TargetLifetimeAndResolve()
    {
        var copy = Code(typeof(OceanRenderer), "BuildScreenCopyResources");
        Check(copy.Any(c => c.LoadsConstant((long)VkFormat.R32SFloat)) && copy.Any(c => c.opcode == OpCodes.Newobj
            && c.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(RenderTarget))
            && copy.Any(c => c.opcode == OpCodes.Stfld && Field(c, "_depthCopyTarget")),
            "native opaque reverse-Z depth copy is an R32SFloat color image");
        var rebuild = Code(typeof(OceanRenderer), nameof(OceanRenderer.RebuildFrameResources));
        Check(FindCall(rebuild, nameof(OceanRenderer), "DestroyFrameResources") >= 0
            && FindCall(rebuild, nameof(OceanRenderer), "Build") > FindCall(rebuild, nameof(OceanRenderer), "DestroyFrameResources"),
            "native frame-resource rebuild destroys water resources before recreating descriptors and copy targets");
        var destroy = Code(typeof(OceanRenderer), "DestroyFrameResources");
        Check(destroy.Any(c => Field(c, "_sharedOceanDataDescriptorSetLayout"))
            && destroy.Any(c => Field(c, "_sharedOceanDataDescriptorPool")) && destroy.Any(c => Field(c, "_depthCopyTarget"))
            && destroy.Any(c => Calls(c, nameof(RenderTarget), nameof(RenderTarget.Dispose)))
            && Code(typeof(OceanRenderer), "Build").Any(c => Calls(c, nameof(OceanRenderer), "BuildScreenCopyResources")),
            "native rebuild invalidates borrowed shared sets/layouts and opaque-depth image views");
        var targets = typeof(RenderTarget).GetConstructors().SelectMany(constructor => PatchProcessor.GetOriginalInstructions(constructor)).ToList();
        var resolves = targets.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.opcode == OpCodes.Newobj
            && item.instruction.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(TargetImage))
            .Select(item => Integer(targets, item.index - 1).Value).ToArray();
        Check(resolves.SequenceEqual([(long)VkResolveModeFlags.AverageBit, (long)VkResolveModeFlags.MaxBit]),
            "native MSAA color resolves by average while reverse-Z depth resolves the nearest sample by maximum");
        var targetCtor = PatchProcessor.GetOriginalInstructions(typeof(TargetImage).GetConstructors().Single());
        Check(targetCtor.Any(c => c.operand is string message && message.Contains("output image must be single-sampled", StringComparison.OrdinalIgnoreCase)),
            "native resolved output image is constrained to one sample independently of its MSAA attachment");
        var imageRebuild = Code(typeof(TargetImage), nameof(TargetImage.Rebuild));
        Check(imageRebuild.Any(c => Calls(c, "RenderImage", "Rebuild")),
            "native target image views can be rebuilt while the RenderTarget object remains the same");
    }

    private static bool Offsets<T>(params (string Field, int Offset)[] fields) => fields.All(field => Marshal.OffsetOf<T>(field.Field).ToInt32() == field.Offset);
    private static List<CodeInstruction> Code(Type type, string method) => PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(type, method));
    private static bool Calls(CodeInstruction instruction, string type, string method) => instruction.operand is MethodBase called
        && called.DeclaringType?.Name.Split('`')[0] == type && called.Name == method;
    private static int FindCall(List<CodeInstruction> code, string type, string method, int start = 0) =>
        code.FindIndex(Math.Clamp(start, 0, code.Count), c => Calls(c, type, method));
    private static bool Field(CodeInstruction instruction, string name) => instruction.operand is FieldInfo field && field.Name == name;

    private static List<Dictionary<string, long>> BindingInitializers(List<CodeInstruction> code)
    {
        var result = new List<Dictionary<string, long>>(); var current = new Dictionary<string, long>();
        for (int index = 0; index < code.Count; index++)
        {
            if (code[index].opcode != OpCodes.Stfld || code[index].operand is not FieldInfo field
                || field.DeclaringType != typeof(VkDescriptorSetLayoutBinding)) continue;
            current[field.Name] = Integer(code, index - 1).Value;
            if (field.Name != nameof(VkDescriptorSetLayoutBinding.StageFlags)) continue;
            result.Add(current); current = new();
        }
        return result;
    }

    private static List<long> FieldValues(List<CodeInstruction> code, Type type, string name) => code.Select((instruction, index) => (instruction, index))
        .Where(item => item.instruction.opcode == OpCodes.Stfld && item.instruction.operand is FieldInfo field && field.DeclaringType == type && field.Name == name)
        .Select(item => Integer(code, item.index - 1).Value).ToList();

    // Read constant initializer values and guaranteed OR-ed stage bits from real IL.
    // Unknown extra-stage arguments contribute zero; they can only add stage bits.
    private static (long Value, int Start) Integer(List<CodeInstruction> code, int index)
    {
        var instruction = code[index];
        for (int value = -1; value <= 8; value++) if (instruction.LoadsConstant(value)) return (value, index);
        if (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S || instruction.opcode == OpCodes.Ldc_I8)
            return (Convert.ToInt64(instruction.operand), index);
        if (instruction.opcode == OpCodes.Or)
        {
            var right = Integer(code, index - 1); var left = Integer(code, right.Start - 1);
            return (left.Value | right.Value, left.Start);
        }
        if (instruction.IsLdloc())
        {
            int local = LocalIndex(instruction);
            for (int previous = index - 1; previous >= 0; previous--)
                if (code[previous].IsStloc() && LocalIndex(code[previous]) == local)
                    return (Integer(code, previous - 1).Value, index);
        }
        if (instruction.opcode.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true) return (0, index);
        throw new InvalidOperationException("Unrecognized native constant/stage initializer: " + instruction);
    }

    private static int LocalIndex(CodeInstruction instruction) => instruction.operand switch
    {
        LocalBuilder local => local.LocalIndex,
        LocalVariableInfo local => local.LocalIndex,
        int local => local,
        byte local => local,
        _ => instruction.opcode.Name?.Last() is { } digit && digit is >= '0' and <= '3' ? digit - '0'
            : throw new InvalidOperationException("Unknown native local: " + instruction)
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Water native contract check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
