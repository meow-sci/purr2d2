namespace Purr2D2.Core;

/// <summary>Per-instance cooling glow for generated cut caps, expressed in KSA's
/// part-model state bits. The cap material's emissive map is uniform white, so
/// every submitted cap instance must carry either the dark or the tinted state.</summary>
internal static class SliceCapEmission
{
    internal const string FragmentPrefix = "Purr2D2_Fragment_", CapSuffix = "_cap";
    internal const int NoEmissiveBit = 1 << 6, EmissiveColorBit = 1 << 7;
    internal const double CoolingSeconds = 18;

    /// <summary>Allocation-free test for a generated cap mesh id.</summary>
    internal static bool IsCapMesh(string? meshId) => meshId != null
        && meshId.StartsWith(FragmentPrefix, StringComparison.Ordinal) && meshId.EndsWith(CapSuffix, StringComparison.Ordinal);

    /// <summary>The heat owner for a cap mesh: <c>{fragment}_s{index}_cap</c>, or the
    /// older <c>{fragment}_cap</c>. Allocates the result; callers cache it per model.</summary>
    internal static bool TryGetFragmentId(string? meshId, out string fragmentId)
    {
        fragmentId = "";
        if (!IsCapMesh(meshId) || meshId!.Length <= FragmentPrefix.Length + CapSuffix.Length) return false;
        int solidMarker = meshId.IndexOf("_s", FragmentPrefix.Length, StringComparison.Ordinal);
        fragmentId = solidMarker < 0 ? meshId[..^CapSuffix.Length] : meshId[..solidMarker];
        return true;
    }

    /// <summary>Packed 0xRRGGBB glow for a cap cut <paramref name="ageSeconds"/> ago;
    /// false once cooled (or for an invalid age), when the cap is drawn dark.</summary>
    internal static bool TryGetGlow(double ageSeconds, out uint color)
    {
        color = 0;
        if (!(ageSeconds < CoolingSeconds)) return false;
        double age = Math.Max(0, ageSeconds);
        uint r = (uint)Math.Clamp(255 * Math.Exp(-age / 7), 0, 255);
        uint g = (uint)Math.Clamp(165 * Math.Exp(-age / 3), 0, 255);
        uint b = (uint)Math.Clamp(40 * Math.Exp(-age / 1.7), 0, 255);
        color = (r << 16) | (g << 8) | b;
        return true;
    }

    /// <summary>Hot caps add the instance color; cold caps suppress the white emissive
    /// map. Other state bits are preserved, and reapplying is idempotent.</summary>
    internal static int Flags(int stateBitFlag, bool hot) => hot
        ? (stateBitFlag & ~NoEmissiveBit) | EmissiveColorBit
        : (stateBitFlag | NoEmissiveBit) & ~EmissiveColorBit;
}
