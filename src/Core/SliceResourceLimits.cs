namespace Purr2D2.Core;

/// <summary>Bounded preferences for generated slice assets and local native physics.</summary>
public static class SliceResourceLimits
{
    public const int DefaultVertexArenaMiB = 128, MinimumVertexArenaMiB = 32, MaximumVertexArenaMiB = 1024;
    public const int DefaultIndexArenaMiB = 64, MinimumIndexArenaMiB = 16, MaximumIndexArenaMiB = 512;
    public const int DefaultVertexMaximumMiB = 512, DefaultIndexMaximumMiB = 256;
    public const int DefaultAssetDiskMiB = 2048, MinimumAssetDiskMiB = 128, MaximumAssetDiskMiB = 16384;
    public const int DefaultAssetEntryLimit = 8192, MinimumAssetEntryLimit = 128, MaximumAssetEntryLimit = 65536;
    public const int DefaultPhysicsVehicleLimit = 128, MinimumPhysicsVehicleLimit = 30, MaximumPhysicsVehicleLimit = 512;
    public const long BytesPerMiB = 1024L * 1024;
}
