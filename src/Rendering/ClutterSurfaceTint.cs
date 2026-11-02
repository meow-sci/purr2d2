using System.Globalization;
using System.Text.RegularExpressions;

namespace Purr2D2.Rendering;

/// <summary>Durable native RGB10 tint, with the top 20 bits of its positive luminosity float.</summary>
internal static class ClutterSurfaceTint
{
    internal const uint Marker = 1u << 11, LuminosityMask = 0xfffff000u;
    private const string MeshMarker = "_ct";
    internal static uint Luminosity(float value)
    {
        if (!float.IsFinite(value) || value <= 0) throw new InvalidDataException("Ground surface luminosity must be positive and finite.");
        uint bits = BitConverter.SingleToUInt32Bits(value) & LuminosityMask;
        if (BitConverter.UInt32BitsToSingle(bits) <= 0) throw new InvalidDataException("Ground surface luminosity is below the supported range.");
        return bits;
    }
    internal static string MeshSuffix(uint color, uint luminosity) => luminosity == 0 ? "" : MeshMarker + color.ToString("x8") + luminosity.ToString("x8");
    internal static bool TryRead(string? meshId, out uint color, out uint luminosity)
    {
        color = luminosity = 0;
        if (meshId == null || !meshId.StartsWith("Purr2D2_Fragment_", StringComparison.Ordinal)
            || meshId.EndsWith("_cap", StringComparison.Ordinal)) return false;
        int marker = meshId.IndexOf(MeshMarker, StringComparison.Ordinal);
        if (marker < 0 || meshId.Length < marker + 22 || !meshId.AsSpan(marker + 19, 2).SequenceEqual("_m")
            || !int.TryParse(meshId.AsSpan(marker + 21), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !uint.TryParse(meshId.AsSpan(marker + 3, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsedColor)
            || !uint.TryParse(meshId.AsSpan(marker + 11, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsedLuminosity)) return false;
        float value = BitConverter.UInt32BitsToSingle(parsedLuminosity);
        if ((parsedLuminosity & ~LuminosityMask) != 0 || !float.IsFinite(value) || value <= 0) return false;
        color = parsedColor; luminosity = parsedLuminosity; return true;
    }
    internal static int Flags(int original, uint luminosity) => unchecked((int)(((uint)original & 0x7ffu) | Marker | luminosity));

    // Only this single declaration is changed; all unmarked models execute their
    // original material path. Raster has a conditional emission varying, while
    // native raytraced PartModel declares the same varying unconditionally.
    internal static string RewriteShader(string source, bool raytraced)
    {
        const string declaration = "vec3 sampledColor = gammaToLinear(texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv).xyz);";
        if (source.Split(declaration, StringSplitOptions.None).Length != 2 || !source.Contains("inStateFlags", StringComparison.Ordinal)
            || !source.Contains("inEmissiveColor", StringComparison.Ordinal) || source.Contains("PURR_CLUTTER_TINT", StringComparison.Ordinal))
            throw new InvalidDataException("Native PartModel diffuse shader contract changed.");
        var flags = Regex.Matches(source, @"inStateFlags\s*&\s*\(1u\s*<<\s*(\d+)u?\)");
        if (flags.Count == 0 || flags.Any(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 10))
            throw new InvalidDataException("Native PartModel state flag allocation changed.");
        string tint = """
            // PURR_CLUTTER_TINT: generated exterior meshes only; native low flags stay intact.
            if ((inStateFlags & 0x800u) != 0u) {
                vec4 purrDiffuse = texture(sampler2D(globalTextures[drawData.diffuseTextureIndex], textureSampler), inUv);
                vec3 purrColor = vec3(float((inEmissiveColor >> 22u) & 1023u), float((inEmissiveColor >> 12u) & 1023u), float((inEmissiveColor >> 2u) & 1023u)) / 1023.0;
                float purrLuminosity = uintBitsToFloat(inStateFlags & 0xfffff000u);
                sampledColor = mix(pow(purrDiffuse.rgb, vec3(2.2)), purrDiffuse.rgb, purrDiffuse.a) * 2.0;
                sampledColor *= mix(vec3(purrLuminosity), purrColor, purrDiffuse.a) / purrLuminosity;
            }
            """;
        if (!raytraced) tint = "\n#ifdef ENABLE_EMISSIVE\n" + tint + "\n#endif\n";
        return source.Replace(declaration, declaration + "\n" + tint, StringComparison.Ordinal);
    }
}
