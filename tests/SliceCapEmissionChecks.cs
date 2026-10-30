using Purr2D2.Core;

// Generated cut-cap glow state, applied to every submitted instance on each render path.
internal static class SliceCapEmissionChecks
{
    internal static void Run()
    {
        const string fragment = "Purr2D2_Fragment_2b720ec818963aa983654aa3e5578a0757ccf8f2cbe9a65b0a894e21c1ece118";
        Check(SliceCapEmission.TryGetFragmentId(fragment + "_s3_cap", out string id) && id == fragment,
            "merged-surface cap meshes resolve their fragment heat owner");
        Check(SliceCapEmission.TryGetFragmentId(fragment + "_cap", out id) && id == fragment,
            "older per-fragment cap meshes keep the same heat owner");
        Check(!SliceCapEmission.IsCapMesh(fragment + "_s0_m0") && !SliceCapEmission.IsCapMesh("CoreFuelTankA_cap")
            && !SliceCapEmission.IsCapMesh(null) && !SliceCapEmission.TryGetFragmentId(fragment + "_s0_m0", out _)
            && !SliceCapEmission.TryGetFragmentId(SliceCapEmission.FragmentPrefix + SliceCapEmission.CapSuffix, out _),
            "material surfaces, stock meshes, missing and empty ids are never treated as caps");

        Check(SliceCapEmission.TryGetGlow(0, out uint fresh) && fresh == 0xFFA528
            && SliceCapEmission.TryGetGlow(-1, out uint early) && early == fresh,
            "a fresh cut glows bright orange, and an early clock clamps to the fresh glow");
        bool cooling = true;
        uint previous = fresh;
        for (double age = .5; age < SliceCapEmission.CoolingSeconds; age += .5)
        {
            if (!SliceCapEmission.TryGetGlow(age, out uint color)) { cooling = false; break; }
            uint r = color >> 16, g = (color >> 8) & 0xFF, b = color & 0xFF;
            cooling &= r <= previous >> 16 && g <= ((previous >> 8) & 0xFF) && b <= (previous & 0xFF) && r >= g && g >= b;
            previous = color;
        }
        Check(cooling && previous >> 16 is > 0 and <= 24 && (previous & 0xFFFF) == 0,
            "the glow cools monotonically through orange to a dim red before the cooling window ends");
        Check(!SliceCapEmission.TryGetGlow(SliceCapEmission.CoolingSeconds, out uint cold) && cold == 0
            && !SliceCapEmission.TryGetGlow(double.NaN, out _) && !SliceCapEmission.TryGetGlow(double.PositiveInfinity, out _),
            "caps are cold after 18 seconds, and invalid ages never glow");

        // Stock MeshIndirect.frag: bit 6 suppresses emission, bit 7 adds the instance color.
        Check(SliceCapEmission.NoEmissiveBit == 64 && SliceCapEmission.EmissiveColorBit == 128,
            "cap state bits match the stock part-model shader");
        const int native = 1 << 0 | 1 << 4 | 1 << 10;
        int hot = SliceCapEmission.Flags(native | SliceCapEmission.NoEmissiveBit, true);
        int dark = SliceCapEmission.Flags(native | SliceCapEmission.EmissiveColorBit, false);
        Check(hot == (native | SliceCapEmission.EmissiveColorBit) && dark == (native | SliceCapEmission.NoEmissiveBit)
            && SliceCapEmission.Flags(hot, true) == hot && SliceCapEmission.Flags(dark, false) == dark,
            "hot caps add their color and cold caps suppress the white emissive map; other native bits survive and reapplying is idempotent");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cap emission check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
