using Purr2D2.Core;

internal static class SliceMeshArenaChecks
{
    public static void Main()
    {
        PublishedRegionsRemainPinned();
        RetiredRegionsCoalesce();
        PartialReservationRollsBack();
        FailedUploadsRecoverOnPressure();
        OwnershipAndRepeatedCleanup();
        Console.WriteLine("Slice mesh arena checks passed (published resource pinning, fence-owned retirement, coalescing, paired allocation rollback, pressure recovery and idempotent cleanup).");
    }

    private static void PublishedRegionsRemainPinned()
    {
        var arena = new SliceMeshArena(128, 32);
        Check(arena.TryAllocate(64, 16, out var live), "live mesh reservation fits");
        arena.Publish(live!);
        Check(!arena.RetireUnpublished(live!) && arena.ReclaimRetired() == 0,
            "registered template memory cannot become reclaimable through cache clearing");
        Check(arena.TryAllocate(64, 16, out var pending), "the remaining reservation stays available");
        Check(!arena.TryAllocate(32, 4, out _), "allocation refuses to overwrite live or in-flight GPU ranges");
        Check(arena.RetireUnpublished(pending!) && arena.VertexUsed == 128
            && arena.ReclaimableVertexBytes == 64, "retirement tracks completed unpublished work without freeing it early");
        Check(arena.ReclaimRetired() == 1 && arena.VertexUsed == 64 && arena.IndexUsed == 16,
            "explicit cleanup returns only unpublished ranges");
        Check(arena.TryAllocate(64, 16, out var reused) && reused!.VertexOffset == 64 && reused.IndexOffset == 16,
            "reused memory excludes the pinned registered mesh at the start of the arena");
        Check(arena.ReclaimedVertexBytes == 64 && arena.ReclaimedIndexBytes == 16,
            "usage reports actual reclaimed bytes independently of currently used capacity");
    }

    private static void RetiredRegionsCoalesce()
    {
        var arena = new SliceMeshArena(192, 24);
        Check(arena.TryAllocate(64, 8, out var a), "first adjacent upload region fits");
        Check(arena.TryAllocate(64, 8, out var b), "second adjacent upload region fits");
        Check(arena.TryAllocate(64, 8, out var c), "three adjacent upload regions fit exactly");
        arena.RetireUnpublished(b!); arena.RetireUnpublished(a!); arena.RetireUnpublished(c!);
        Check(arena.ReclaimRetired() == 3 && arena.TryAllocate(192, 24, out var combined)
            && combined!.VertexOffset == 0 && combined.IndexOffset == 0,
            "retirement in arbitrary order coalesces both pools into a full-sized contiguous reservation");
    }

    private static void PartialReservationRollsBack()
    {
        var arena = new SliceMeshArena(160, 16);
        Check(arena.TryAllocate(64, 12, out var pinned), "uneven vertex/index fixture fits");
        arena.Publish(pinned!);
        Check(!arena.TryAllocate(64, 8, out _) && arena.VertexUsed == 64 && arena.IndexUsed == 12,
            "index exhaustion rolls back a provisional vertex reservation without changing usage");
        Check(arena.TryAllocate(96, 4, out var rest) && rest!.VertexOffset == 64 && rest.IndexOffset == 12,
            "failed paired allocation leaves all remaining vertex bytes available");
        Check(!arena.TryAllocate(1, 4, out _) && !arena.TryAllocate(32, 1, out _),
            "invalid stride sizes cannot misalign native vertex or index offsets");
    }

    private static void FailedUploadsRecoverOnPressure()
    {
        var arena = new SliceMeshArena(64, 8);
        Check(arena.TryAllocate(64, 8, out var failed), "failed-upload fixture reserves the arena");
        arena.RetireUnpublished(failed!);
        Check(arena.TryAllocate(64, 8, out var retry) && retry!.VertexOffset == 0
            && arena.ReclaimableVertexBytes == 0 && arena.ReclaimedVertexBytes == 64,
            "a later allocation automatically recovers completed abandoned uploads before reporting full");
    }

    private static void OwnershipAndRepeatedCleanup()
    {
        var a = new SliceMeshArena(64, 8); var b = new SliceMeshArena(64, 8);
        Check(a.TryAllocate(64, 8, out var entry) && !b.RetireUnpublished(entry!),
            "another arena cannot retire an allocation it does not own");
        Check(a.RetireUnpublished(entry!) && !a.RetireUnpublished(entry!)
            && a.ReclaimRetired() == 1 && a.ReclaimRetired() == 0 && !a.RetireUnpublished(entry!)
            && a.VertexUsed == 0 && a.IndexUsed == 0 && a.ReclaimedVertexBytes == 64,
            "duplicate cleanup cannot return the same bytes twice or underflow usage");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Slice mesh arena check failed: " + message);
    }
}
