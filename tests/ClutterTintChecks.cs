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
        Directory.CreateDirectory(output);
        foreach (string filename in new[] { "MeshIndirect.frag", "MeshIndirectRaytraced.frag" })
        {
            string sourceCode = File.ReadAllText(Path.Combine(nativeShaders, "Mesh", filename));
            string rewritten = ClutterSurfaceTint.RewriteShader(sourceCode, filename.Contains("Raytraced"));
            if (!rewritten.Contains("* 2.0", StringComparison.Ordinal) || !rewritten.Contains("purrDiffuse.a", StringComparison.Ordinal)
                || !rewritten.Contains("uintBitsToFloat", StringComparison.Ordinal)) throw new Exception("Native ground diffuse rule was not retained.");
            File.WriteAllText(Path.Combine(output, filename), EnableIncludes(rewritten));
            bool refused = false;
            try { ClutterSurfaceTint.RewriteShader(sourceCode.Replace("inStateFlags & (1u << 4)", "inStateFlags & (1u << 11)"), false); }
            catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("Changed native state allocation did not fail closed.");
        }
        File.WriteAllText(Path.Combine(output, "MeshIndirect.vert"), EnableIncludes(File.ReadAllText(Path.Combine(nativeShaders, "Mesh", "MeshIndirect.vert"))));
        Console.WriteLine("PASS: exact native RGB10 tint identity, preserved low native flags, cap exclusion, 1000 luminosities within 0.05%, fail-closed native shader rewrites.");
    }
    private static string EnableIncludes(string source) => System.Text.RegularExpressions.Regex.Replace(source,
        @"#version (\d+)", "#version $1\n#extension GL_GOOGLE_include_directive : require");
}
