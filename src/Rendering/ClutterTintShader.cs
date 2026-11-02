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
    private static bool _failed, _ready;
    internal static bool Ready => Volatile.Read(ref _ready);
    internal static void Install()
    {
        if (_harmony != null) return;
        try
        {
            _harmony = new("meow-sci.purr2d2.clutter-tint");
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
    internal static void Remove() { _harmony?.UnpatchAll(_harmony.Id); _harmony = null; Volatile.Write(ref _ready, false); }

    private static bool BeforeCompile(Device device, string filePath, ref VkShaderStageFlags shaderStage,
        CompileOptions? options, ref VkShaderModule __result)
    {
        string path = "/" + filePath.Replace('\\', '/');
        bool ordinary = path.EndsWith("/Content/Core/Shaders/Mesh/MeshIndirect.frag", StringComparison.OrdinalIgnoreCase);
        bool raytraced = path.EndsWith("/Content/Core/Shaders/Mesh/MeshIndirectRaytraced.frag", StringComparison.OrdinalIgnoreCase);
        if (_failed || !ordinary && !raytraced) return true;
        try
        {
            string rewritten = ClutterSurfaceTint.RewriteShader(File.ReadAllText(filePath), raytraced);
            shaderStage = VkShaderStageFlags.FragmentBit;
            // The original filename remains the compiler's source name, so every
            // native include/dependency callback resolves in its original directory.
            __result = ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(rewritten), shaderStage,
                options, Encoding.UTF8.GetBytes(filePath));
            Volatile.Write(ref _ready, true); return false;
        }
        catch (Exception error)
        {
            _failed = true; Volatile.Write(ref _ready, false);
            Console.WriteLine("[purr2d2] Native clutter tint unavailable; ground objects retain native appearance via displacement: " + error.Message);
            return true; // The unchanged native shader still compiles normally.
        }
    }
}
