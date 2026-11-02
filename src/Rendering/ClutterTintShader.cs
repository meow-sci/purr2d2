using System.Text;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using HarmonyLib;
using RenderCore;

namespace Purr2D2.Rendering;

/// <summary>Early, fail-closed extension of native PartModel material sampling. No shader files or textures are changed.</summary>
internal static class ClutterTintShader
{
    private static Harmony? _harmony;
    private static bool _failed, _ready, _foliageFailed, _foliageColorReady, _foliagePrepassReady,
        _foliageCsmReady, _foliagePointShadowReady;
    internal static bool Ready => Volatile.Read(ref _ready);
    internal static bool FoliageReady => Ready && !_failed && !_foliageFailed && ClutterFoliageMaterial.Ready
        && Volatile.Read(ref _foliageColorReady) && Volatile.Read(ref _foliagePrepassReady)
        && Volatile.Read(ref _foliageCsmReady) && Volatile.Read(ref _foliagePointShadowReady);
    internal static void Install()
    {
        if (_harmony != null) return;
        _failed = _ready = _foliageFailed = _foliageColorReady = _foliagePrepassReady = false;
        _foliageCsmReady = _foliagePointShadowReady = false;
        try
        {
            _harmony = new("meow-sci.purr2d2.clutter-tint");
            try { ClutterFoliageMaterial.Install(_harmony); }
            catch (Exception error)
            {
                _foliageFailed = true;
                Console.WriteLine("[purr2d2] Native foliage material bridge unavailable: " + error.Message);
            }
            var method = AccessTools.Method(typeof(ShaderModuleUtils), nameof(ShaderModuleUtils.FromFile),
                [typeof(Device), typeof(string), typeof(VkShaderStageFlags).MakeByRefType(), typeof(CompileOptions?)])
                ?? throw new MissingMethodException("Native shader compilation seam changed.");
            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClutterTintShader), nameof(BeforeCompile)));
        }
        catch (Exception error)
        {
            _failed = true; Volatile.Write(ref _ready, false);
            Console.WriteLine("[purr2d2] Native clutter tint hook unavailable; native displacement remains available: " + error.Message);
        }
    }
    internal static void Remove()
    {
        _harmony?.UnpatchAll(_harmony.Id); _harmony = null;
        _foliageColorReady = _foliagePrepassReady = false;
        _foliageCsmReady = _foliagePointShadowReady = false;
        ClutterFoliageMaterial.Reset(); Volatile.Write(ref _ready, false);
    }

    private static bool BeforeCompile(Device device, string filePath, ref VkShaderStageFlags shaderStage,
        CompileOptions? options, ref VkShaderModule __result)
    {
        string path = "/" + filePath.Replace('\\', '/');
        bool ordinary = path.EndsWith("/Content/Core/Shaders/Mesh/MeshIndirect.frag", StringComparison.OrdinalIgnoreCase);
        bool raytraced = path.EndsWith("/Content/Core/Shaders/Mesh/MeshIndirectRaytraced.frag", StringComparison.OrdinalIgnoreCase);
        bool prepass = path.EndsWith("/Content/Core/Shaders/Mesh/MeshNormalIndirect.frag", StringComparison.OrdinalIgnoreCase);
        bool csm = path.EndsWith("/Content/Core/Shaders/ShadowPassNoPushCSM.vert", StringComparison.OrdinalIgnoreCase);
        bool pointShadow = path.EndsWith("/Content/Core/Shaders/ShadowPassNoPush.vert", StringComparison.OrdinalIgnoreCase);
        bool shadow = csm || pointShadow;
        if (_failed || !ordinary && !raytraced && !prepass && !shadow || (prepass || shadow) && _foliageFailed) return true;
        try
        {
            string rewritten = File.ReadAllText(filePath);
            if (!prepass && !shadow) rewritten = ClutterSurfaceTint.RewriteShader(rewritten, raytraced);
            if (!_foliageFailed) rewritten = shadow ? ClutterFoliageSurface.RewriteShadowShader(rewritten)
                : ClutterFoliageSurface.RewriteShader(rewritten, prepass, raytraced);
            shaderStage = shadow ? VkShaderStageFlags.VertexBit : VkShaderStageFlags.FragmentBit;
            // The original filename remains the compiler's source name, so every
            // native include/dependency callback resolves in its original directory.
            __result = ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(rewritten), shaderStage,
                options, Encoding.UTF8.GetBytes(filePath + "\0"));
            if (ordinary || raytraced) Volatile.Write(ref _ready, true);
            if (!_foliageFailed)
            {
                if (prepass) Volatile.Write(ref _foliagePrepassReady, true);
                if (ordinary) Volatile.Write(ref _foliageColorReady, true);
                if (csm) Volatile.Write(ref _foliageCsmReady, true);
                if (pointShadow) Volatile.Write(ref _foliagePointShadowReady, true);
            }
            return false;
        }
        catch (Exception error)
        {
            _failed = true; Volatile.Write(ref _ready, false);
            Console.WriteLine("[purr2d2] Native clutter tint unavailable; ground objects retain native appearance via displacement: " + error.Message);
            return true; // The unchanged native shader still compiles normally.
        }
    }
}
