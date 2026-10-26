namespace Purr2D2.Core;

/// <summary>Bounded preferences for slice geometry, generated assets and local native physics.</summary>
public static class SliceResourceLimits
{
    public const int DefaultVertexArenaMiB = 128, MinimumVertexArenaMiB = 32, MaximumVertexArenaMiB = 1024;
    public const int DefaultIndexArenaMiB = 64, MinimumIndexArenaMiB = 16, MaximumIndexArenaMiB = 512;
    public const int DefaultVertexMaximumMiB = 512, DefaultIndexMaximumMiB = 256;
    public const int DefaultAssetDiskMiB = 2048, MinimumAssetDiskMiB = 128, MaximumAssetDiskMiB = 16384;
    public const int DefaultAssetEntryLimit = 8192, MinimumAssetEntryLimit = 128, MaximumAssetEntryLimit = 65536;
    public const int DefaultPhysicsVehicleLimit = 128, MinimumPhysicsVehicleLimit = 30, MaximumPhysicsVehicleLimit = 512;
    public const int DefaultSolidLimit = 2048, MinimumSolidLimit = 128, MaximumSolidLimit = 16384;
    public const int DefaultTriangleLimit = 120_000, MinimumTriangleLimit = 1_000, MaximumTriangleLimit = 1_000_000;
    // Independent export/collider constraints, not closed-solid counts.
    internal const int MaximumRenderSurfaces = 2046, MaximumFragmentVertices = 65536;
    public const long BytesPerMiB = 1024L * 1024;
}

/// <summary>Immutable admission limits captured once per cut, before copying native geometry.</summary>
internal sealed class SliceGeometryBudget
{
    internal static readonly SliceGeometryBudget Default = new(SliceResourceLimits.DefaultSolidLimit, SliceResourceLimits.DefaultTriangleLimit);
    // Loading existing save assets uses supported ceilings, never a subsequently lowered preference.
    internal static readonly SliceGeometryBudget Maximum = new(SliceResourceLimits.MaximumSolidLimit, SliceResourceLimits.MaximumTriangleLimit);
    private static SliceGeometryBudget _current = Default;
    internal static SliceGeometryBudget Current => Volatile.Read(ref _current);
    internal static void Configure(int solids, int triangles) => Volatile.Write(ref _current, new(solids, triangles));

    internal int Solids { get; }
    internal int SourceTriangles { get; }
    // Preserve the original 120k source / 240k combined output / 200k per-fragment allowances.
    internal int OutputTriangles => SourceTriangles * 2;
    internal int FragmentTriangles => SourceTriangles * 5 / 3;

    internal SliceGeometryBudget(int solids, int triangles)
    {
        Solids = Math.Clamp(solids, SliceResourceLimits.MinimumSolidLimit, SliceResourceLimits.MaximumSolidLimit);
        SourceTriangles = Math.Clamp(triangles, SliceResourceLimits.MinimumTriangleLimit, SliceResourceLimits.MaximumTriangleLimit);
    }

    internal bool AllowsSource(int solids, long triangles) => solids <= Solids && triangles <= SourceTriangles;
    internal bool AllowsFragment(int solids, long triangles) => solids <= Solids && triangles <= FragmentTriangles;
    internal string SourceUsage(int solids, long triangles) => $"{solids:N0} / {Solids:N0} solids; {triangles:N0} / {SourceTriangles:N0} source triangles";
    internal string FragmentUsage(int solids, long triangles) => $"{solids:N0} / {Solids:N0} solids; {triangles:N0} / {FragmentTriangles:N0} fragment triangles";
    internal const string SettingsHint = "Increase the geometry limits in Settings > Slice resources.";
}
