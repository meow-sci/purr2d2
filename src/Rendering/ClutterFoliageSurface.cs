namespace Purr2D2.Rendering;

/// <summary>Generated leaf-card shader contract. Native part materials and low instance flags stay intact.</summary>
internal static class ClutterFoliageSurface
{
    internal const string MeshMarker = "_fol";
    internal const int ShadowMarker = 0x7fc05552;
    internal const int TextureMask = 0xfffff, FlagShift = 20, MaximumFlags = 0x3ff;
    internal static bool IsMesh(string? id) => id != null
        && id.StartsWith("Purr2D2_Fragment_", StringComparison.Ordinal)
        && id.Contains(MeshMarker + "_", StringComparison.Ordinal);

    // The native material buffer has no unused words. A generated foliage draw
    // carries its opacity descriptor/flags in its otherwise-unused emissive word.
    // Negative values cannot name a native texture and never enable emission.
    internal static int Encode(int opacityTexture, int flags)
    {
        if ((uint)opacityTexture > TextureMask || (uint)flags > MaximumFlags)
            throw new InvalidDataException("Native foliage texture or pipeline flags exceed the draw-data contract.");
        return -2 - (opacityTexture | flags << FlagShift);
    }
    internal static int Texture(int encoded) => (-2 - encoded) & TextureMask;
    internal static int Flags(int encoded) => (-2 - encoded) >> FlagShift;

    internal static string RewriteShader(string source, bool prepass, bool raytraced = false)
    {
        const string draw = "PerDrawData drawData = perDrawStorageBuffer.Data[inDrawIndex];";
        const string sampler = "layout(set = 2, binding = 1) uniform sampler textureSampler;";
        if (source.Contains("PURR_FOLIAGE", StringComparison.Ordinal)
            || source.Split(draw, StringSplitOptions.None).Length != 2
            || source.Split(sampler, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Native foliage draw/texture shader contract changed.");
        foreach (string field in new[] { "diffuseTextureIndex", "normalTextureIndex", "pbrTextureIndex", "emissiveTextureIndex", "tfiTextureIndex" })
            RequireOnce(source, "int " + field + ";");
        const string helpers = """

            // PURR_FOLIAGE: only generated cards use negative emissive descriptors.
            bool purrFoliage(PerDrawData data) { return data.emissiveTextureIndex < -1; }
            uint purrFoliageData(PerDrawData data) { return uint(-2 - data.emissiveTextureIndex); }
            void purrClipFoliage(PerDrawData data) {
                if (purrFoliage(data)) {
                    uint opacityIndex = purrFoliageData(data) & 0xfffffu;
                    // Same fixed cutoff as native GroundClutter/SolidDepth.frag.
                    if (texture(sampler2D(globalTextures[opacityIndex], textureSampler), inUv).r < 0.5) discard;
                }
            }
            """;
        source = source.Replace(sampler, sampler + "\n" + helpers, StringComparison.Ordinal)
            .Replace(draw, draw + "\n    purrClipFoliage(drawData);", StringComparison.Ordinal);
        if (prepass)
        {
            const string normal = "vec3 worldNormal = getNormalFromMap_ShaderX(sampledNormal, inNormal, camPos() - inWorldPosition, inUv);";
            RequireOnce(source, normal);
            return source.Replace(normal, normal + "\n" + """
                // Match the native custom-normal foliage pre-pass. Both render
                // windings are explicit, so no global cull state changes are needed.
                if (purrFoliage(drawData) && ((purrFoliageData(drawData) >> 20u) & 512u) != 0u) {
                    worldNormal = normalize(cross(dFdx(inWorldPosition), dFdy(inWorldPosition)));
                    worldNormal = dot(worldNormal, inWorldPosition) > 0.0 ? -worldNormal : worldNormal;
                }
                """, StringComparison.Ordinal);
        }
        const string film = "drawData.tfiTextureIndex >= 0";
        RequireOnce(source, film);
        source = source.Replace(film, "drawData.tfiTextureIndex >= 0 && !purrFoliage(drawData)", StringComparison.Ordinal);
        const string result = "return lightColor;";
        RequireOnce(source, result);
        string transmission = """
            // Native leaf thickness uses the same inexpensive back-light term.
            // This remains a part draw, using the source planet's saved luminosity.
            if (purrFoliage(drawData) && drawData.tfiTextureIndex >= 0 && SHOW_SUN != 0u && (inStateFlags & 0x800u) != 0u) {
                float purrThickness = texture(sampler2D(globalTextures[drawData.tfiTextureIndex], textureSampler), inUv).r;
                vec3 purrDirection = normalize(sunPos().xyz - inWorldPosition);
                vec3 purrView = normalize(-inWorldPosition);
                float purrDot = pow(clamp(dot(purrView, -(purrDirection + worldNormal * 0.05)), 0.0, 1.0), 3.5) * 0.3;
                float purrLuminosity = uintBitsToFloat(inStateFlags & 0xfffff000u);
                float purrShadow = 1.0;
                if (celestialShadow) {
                    purrShadow *= getCelestialShadow(inWorldPosition, sunPos());
                    purrShadow *= GetCloudShadow(inWorldPosition);
                }
                float purrDepth = -(global.camera.view * vec4(inWorldPosition, 1.0)).z;
                purrShadow *= GetCascadedShadow(inWorldPosition, worldNormal, max(dot(worldNormal, purrDirection), 0.0), purrDepth);
                lightColor += global.lighting.sunColor.rgb * sampledColor * purrLuminosity
                    * vec3(1.0, 0.8, 0.7) * purrShadow * (purrDot + 0.01) * (1.0 - purrThickness);
            }
            """;
        // All ordinary generated exterior draws carry the luminosity marker.
        // RT declares it unconditionally; native raster dynamic draws omit it.
        if (!raytraced) transmission = "\n#ifdef ENABLE_EMISSIVE\n" + transmission + "\n#endif\n";
        return source.Replace(result, transmission + "\n    " + result, StringComparison.Ordinal);
    }

    internal static string RewriteShadowShader(string source)
    {
        RequireOnce(source, "int Highlighted;");
        RequireOnce(source, "void main()");
        RequireOnce(source, "InstanceStorage.Data[gl_InstanceIndex]");
        if (source.Contains("PURR_FOLIAGE_SHADOW", StringComparison.Ordinal))
            throw new InvalidDataException("Native foliage shadow shader was already rewritten.");
        int brace = source.IndexOf('{', source.IndexOf("void main()", StringComparison.Ordinal));
        if (brace < 0) throw new InvalidDataException("Native foliage shadow main body changed.");
        source = source.Insert(brace + 1, "\n" + """
            // PURR_FOLIAGE_SHADOW: native CSM submits every opaque draw, even
            // ShadowCaster=false. Only generated cards carry this padding value.
            if (InstanceStorage.Data[gl_InstanceIndex].PurrFoliageMarker == 0x7fc05552) {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                return;
            }
            """ + "\n");
        // std430 already rounds the original mat4 + int structure to 80 bytes.
        // Name existing bytes 68 and 72 without changing its layout or stride.
        return source.Replace("int Highlighted;", "int Highlighted;\n    uint PurrUnusedEmission;\n    int PurrFoliageMarker;", StringComparison.Ordinal);
    }

    private static void RequireOnce(string source, string anchor)
    {
        if (source.Split(anchor, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Native foliage shader anchor changed: " + anchor);
    }
}
