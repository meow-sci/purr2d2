using System.Threading;
using Purr2D2.Core;

namespace Purr2D2.Rendering;

internal readonly record struct SliceResourceUsage(uint VertexBytesUsed, uint VertexBytesCapacity,
    uint IndexBytesUsed, uint IndexBytesCapacity, int RuntimeTemplateCount, int PreparedCacheCount,
    int FailedPreparationCount, int HotCapCount, int ActiveUploadJobs, long DiskBytes, long DiskByteCapacity,
    int DiskEntries, int DiskEntryCapacity, uint ReclaimableVertexBytes, uint ReclaimableIndexBytes,
    long ReclaimedVertexBytes, long ReclaimedIndexBytes);

internal static partial class RuntimeFragmentAssets
{
    private static readonly object StorageGate = new();
    private static long _diskCapacity = SliceResourceLimits.DefaultAssetDiskMiB * SliceResourceLimits.BytesPerMiB;
    private static int _entryCapacity = SliceResourceLimits.DefaultAssetEntryLimit;
    private static long _diskBytes;
    private static int _diskEntries, _registeredTemplates, _activeUploadJobs;
    private static readonly List<string> ExpiredHeat = [];
    private static double _lastHeatPrune;

    internal static bool IsGeneratedTemplate(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    internal static void ConfigureStorageLimits(int diskMiB, int entryLimit)
    {
        Volatile.Write(ref _diskCapacity, Math.Clamp(diskMiB, SliceResourceLimits.MinimumAssetDiskMiB,
            SliceResourceLimits.MaximumAssetDiskMiB) * SliceResourceLimits.BytesPerMiB);
        Volatile.Write(ref _entryCapacity, Math.Clamp(entryLimit, SliceResourceLimits.MinimumAssetEntryLimit,
            SliceResourceLimits.MaximumAssetEntryLimit));
    }

    // Main-thread snapshot only. File enumeration belongs to startup/worker writes,
    // never to the Settings drawing path, even for a large persistent asset library.
    internal static SliceResourceUsage GetUsage() => new(_arena?.VertexUsed ?? 0, _arena?.VertexCapacity ?? 0,
        _arena?.IndexUsed ?? 0, _arena?.IndexCapacity ?? 0, _registeredTemplates, Prepared.Count,
        FailedPreparations.Count, HotCaps.Count, _activeUploadJobs, Volatile.Read(ref _diskBytes),
        Volatile.Read(ref _diskCapacity), Volatile.Read(ref _diskEntries), Volatile.Read(ref _entryCapacity),
        _arena?.ReclaimableVertexBytes ?? 0, _arena?.ReclaimableIndexBytes ?? 0,
        _arena?.ReclaimedVertexBytes ?? 0, _arena?.ReclaimedIndexBytes ?? 0);

    /// <summary>Settled maintenance; registered meshes and all durable save assets remain pinned.</summary>
    internal static string ClearUnusedCaches()
    {
        if (_activeUploadJobs != 0) return "Slice uploads are still active; finish or cancel the slice before clearing unused caches.";
        int records = Prepared.Count;
        Prepared.Clear();
        PruneExpiredHeat();
        long before = (_arena?.ReclaimedVertexBytes ?? 0) + (_arena?.ReclaimedIndexBytes ?? 0);
        int allocations = _arena?.ReclaimRetired() ?? 0;
        long reclaimed = (_arena?.ReclaimedVertexBytes ?? 0) + (_arena?.ReclaimedIndexBytes ?? 0) - before;
        return $"Cleared {records} preparation cache record(s) and {allocations} unused upload allocation(s) ({reclaimed / 1048576.0:0.##} MiB). Registered meshes and saved fragment files are retained.";
    }

    private static void PruneExpiredHeat()
    {
        ExpiredHeat.Clear();
        foreach (var entry in HotCaps)
            if (_now - entry.Value >= 18) ExpiredHeat.Add(entry.Key);
        foreach (string id in ExpiredHeat) HotCaps.Remove(id);
        ExpiredHeat.Clear();
    }

    // Call only from startup or while holding StorageGate on the CPU worker.
    private static void RefreshDiskUsage(string directory)
    {
        long bytes = 0; int entries = 0;
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                bytes = checked(bytes + new FileInfo(path).Length);
                if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) entries++;
            }
        Volatile.Write(ref _diskBytes, bytes); Volatile.Write(ref _diskEntries, entries);
    }
}
