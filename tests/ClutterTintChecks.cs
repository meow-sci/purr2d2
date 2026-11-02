using Purr2D2.Rendering;

internal static class ClutterTintChecks
{
    internal static void Run(string nativeShaders, string output)
    {
        Purr2D2.Core.DebugTimings.Enabled = false;
        uint source = (917u << 22) | (301u << 12) | (72u << 2);
        for (int i = 0; i < 1000; i++)
        {
            float luminosity = (float)Math.Pow(10, -3 + i / 200.0);
            uint encoded = ClutterSurfaceTint.Luminosity(luminosity);
            float recovered = BitConverter.UInt32BitsToSingle(encoded);
            if (recovered <= 0 || Math.Abs(recovered / luminosity - 1) > .0005)
                throw new Exception("Durable ground luminosity exceeded its 0.05% relative error bound.");
            string id = "Purr2D2_Fragment_abc_s3" + ClutterSurfaceTint.MeshSuffix(source, encoded) + "_m0";
            if (!ClutterSurfaceTint.TryRead(id, out uint color, out uint lum) || color != source || lum != encoded)
                throw new Exception("Durable ground tint failed mesh-name round trip.");
            int flags = ClutterSurfaceTint.Flags(0x455, encoded);
            if (((uint)flags & 0x7ffu) != 0x455 || ((uint)flags & ClutterSurfaceTint.Marker) == 0
                || ((uint)flags & ClutterSurfaceTint.LuminosityMask) != encoded)
                throw new Exception("Tint flags changed native selection/shadow/emission state.");
            if (ClutterSurfaceTint.TryRead(id + "_cap", out _, out _) || ClutterSurfaceTint.TryRead("Ordinary" + id, out _, out _))
                throw new Exception("Tint leaked into cap or ordinary model IDs.");
        }
        foreach (float invalid in new[] { 0f, -1, float.NaN, float.PositiveInfinity })
        {
            bool refused = false;
            try { ClutterSurfaceTint.Luminosity(invalid); } catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("Invalid luminosity was marked for native shader use.");
            string malformed = "Purr2D2_Fragment_abc_s0" + ClutterSurfaceTint.MeshSuffix(source, BitConverter.SingleToUInt32Bits(invalid)) + "_m0";
            if (ClutterSurfaceTint.TryRead(malformed, out uint badColor, out uint badLum) || badColor != 0 || badLum != 0)
                throw new Exception("Invalid persisted tint was not cleared on refusal.");
        }
        foreach (int texture in new[] { 0, 1, 1023, 65535, ClutterFoliageSurface.TextureMask })
            foreach (int flags in new[] { 0, 2, 4, 128, 256, 512, ClutterFoliageSurface.MaximumFlags })
            {
                int packed = ClutterFoliageSurface.Encode(texture, flags);
                if (packed >= -1 || ClutterFoliageSurface.Texture(packed) != texture || ClutterFoliageSurface.Flags(packed) != flags)
                    throw new Exception("Native foliage texture/flag sentinel failed exact round trip.");
            }
        if (ClutterFoliageSurface.IsMesh("Ordinary_fol_m0") || ClutterFoliageSurface.IsMesh("Purr2D2_Fragment_abc_s0_cap")
            || !ClutterFoliageSurface.IsMesh("Purr2D2_Fragment_abc_s0_fol_ct112233443d800000_m0"))
            throw new Exception("Foliage adaptation escaped its generated leaf meshes.");
        Directory.CreateDirectory(output);
        foreach (string filename in new[] { "MeshIndirect.frag", "MeshIndirectRaytraced.frag" })
        {
            string sourceCode = File.ReadAllText(Path.Combine(nativeShaders, "Mesh", filename));
            string rewritten = ClutterSurfaceTint.RewriteShader(sourceCode, filename.Contains("Raytraced"));
            if (!rewritten.Contains("* 2.0", StringComparison.Ordinal) || !rewritten.Contains("purrDiffuse.a", StringComparison.Ordinal)
                || !rewritten.Contains("uintBitsToFloat", StringComparison.Ordinal)) throw new Exception("Native ground diffuse rule was not retained.");
            rewritten = ClutterFoliageSurface.RewriteShader(rewritten, prepass: false, raytraced: filename.Contains("Raytraced"));
            if (!rewritten.Contains("purrClipFoliage(drawData)", StringComparison.Ordinal)
                || !rewritten.Contains("purrThickness", StringComparison.Ordinal)
                || !rewritten.Contains("&& !purrFoliage(drawData)", StringComparison.Ordinal))
                throw new Exception("Foliage opacity, thickness or native thin-film isolation was lost.");
            File.WriteAllText(Path.Combine(output, filename), EnableIncludes(rewritten));
            bool refused = false;
            try { ClutterSurfaceTint.RewriteShader(sourceCode.Replace("inStateFlags & (1u << 4)", "inStateFlags & (1u << 11)"), false); }
            catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("Changed native state allocation did not fail closed.");
        }
        string prepass = File.ReadAllText(Path.Combine(nativeShaders, "Mesh", "MeshNormalIndirect.frag"));
        File.WriteAllText(Path.Combine(output, "MeshNormalIndirect.frag"), EnableIncludes(ClutterFoliageSurface.RewriteShader(prepass, prepass: true)));
        bool changedPrepassRefused = false;
        try { ClutterFoliageSurface.RewriteShader(prepass.Replace("emissiveTextureIndex", "changedTextureIndex"), prepass: true); }
        catch (InvalidDataException) { changedPrepassRefused = true; }
        if (!changedPrepassRefused) throw new Exception("A changed native pre-pass material layout did not fail closed.");
        foreach (string filename in new[] { "ShadowPassNoPush.vert", "ShadowPassNoPushCSM.vert" })
        {
            string shadow = File.ReadAllText(Path.Combine(nativeShaders, filename));
            string guarded = ClutterFoliageSurface.RewriteShadowShader(shadow);
            if (guarded.IndexOf("PURR_FOLIAGE_SHADOW", StringComparison.Ordinal) > guarded.IndexOf("vec3 localPos = inPos", StringComparison.Ordinal)
                || !guarded.Contains("PurrFoliageMarker == 0x7fc05552", StringComparison.Ordinal)
                || guarded.Contains("Highlighted) &", StringComparison.Ordinal))
                throw new Exception("Generated leaf cards were not rejected before native shadow geometry.");
            File.WriteAllText(Path.Combine(output, filename), EnableIncludes(guarded));
            bool refused = false;
            try { ClutterFoliageSurface.RewriteShadowShader(shadow.Replace("int Highlighted;", "int ChangedField;")); }
            catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("Changed shadow instance flags did not fail closed.");
        }
        File.WriteAllText(Path.Combine(output, "MeshIndirect.vert"), EnableIncludes(File.ReadAllText(Path.Combine(nativeShaders, "Mesh", "MeshIndirect.vert"))));
        Console.WriteLine("PASS: exact native RGB10 tint, preserved native flags/caps, 1000 luminosities within 0.05%, foliage opacity/thickness descriptor round trips, fail-closed native color/pre-pass rewrites.");
    }
    private static string EnableIncludes(string source) => System.Text.RegularExpressions.Regex.Replace(source,
        @"#version (\d+)", "#version $1\n#extension GL_GOOGLE_include_directive : require");
}
