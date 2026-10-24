using Purr2D2.Core;

internal static class SliceMeshGrowthChecks
{
    public static void Main()
    {
        StockGapsStayUnowned();
        IndependentChannelsAndLimits();
        AdjacentTailAndFragmentation();
        AppendValidationIsAtomic();
        CancelledDemandDoesNotGrow();
        OwnershipTransactionFaults();
        Console.WriteLine("Mesh growth checks passed (disjoint ownership, stable published offsets, channel bounds, overflow, cancellation, old-size copies, publication and allocation/copy/descriptor failure cleanup).");
    }

    private static void StockGapsStayUnowned()
    {
        var arena = new SliceMeshArena(128, 32, 64, 16);
        Check(arena.TryAllocate(128, 32, out var published), "startup segment fits");
        arena.Publish(published!);
        Check(SliceMeshGrowth.TryPlan(arena, 64, 16, 1024, 256, 512, 128, out var plan, out _), "growth plan fits bounds");
        Check(plan.Vertices.AppendBase == 1024 && plan.Indices.AppendBase == 256
            && plan.Vertices.AppendBytes == 128 && plan.Indices.AppendBytes == 32,
            "growth starts at actual global buffer ends, beyond stock meshes following startup reserve");
        Commit(arena, plan);
        Check(arena.TryAllocate(128, 32, out var next) && next!.VertexOffset == 1024 && next.IndexOffset == 256,
            "new uploads use absolute appended offsets");
        Check(published!.VertexOffset == 64 && published.IndexOffset == 16
            && !arena.RetireUnpublished(published), "published startup offsets remain pinned");
        Check(!arena.TryAllocate(32, 4, out _), "intervening native mesh bytes never become allocatable");
    }

    private static void IndependentChannelsAndLimits()
    {
        var arena = new SliceMeshArena(128, 32);
        Check(arena.TryAllocate(128, 4, out _), "vertex-only pressure fixture");
        Check(SliceMeshGrowth.TryPlan(arena, 64, 4, 512, 128, 256, 32, out var plan, out _)
            && plan.Vertices.Grows && !plan.Indices.Grows, "grow only the exhausted vertex channel");
        Check(plan.Vertices.NewBytes == 640 && plan.Indices.NewBytes == 128, "global sizes include stock bytes and independent growth");
        Check(!SliceMeshGrowth.TryPlan(arena, 64, 4, 512, 128, 128, 32, out _, out _), "owned vertex maximum is enforced");
        Check(!SliceMeshGrowth.TryPlan(arena, 64, 4, uint.MaxValue - 31, 128, 256, 32, out _, out _), "global 32-bit arithmetic cannot wrap");
        Check(!SliceMeshGrowth.TryPlan(arena, 63, 4, 512, 128, 256, 32, out _, out _), "invalid native stride is rejected");

        var indexArena = new SliceMeshArena(128, 8);
        indexArena.TryAllocate(32, 8, out _);
        Check(SliceMeshGrowth.TryPlan(indexArena, 32, 4, 515, 32, 128, 16, out plan, out _)
            && !plan.Vertices.Grows && plan.Indices.Grows, "index-only pressure tolerates unaligned unchanged global vertex end");
        Commit(indexArena, plan);
        Check(indexArena.VertexCapacity == 128 && indexArena.IndexCapacity == 16, "only grown channel increases owned capacity");
    }

    private static void AdjacentTailAndFragmentation()
    {
        var arena = new SliceMeshArena(128, 32);
        arena.TryAllocate(96, 24, out var pinned); arena.Publish(pinned!);
        Check(SliceMeshGrowth.TryPlan(arena, 64, 16, 128, 32, 160, 40, out var plan, out _)
            && plan.Vertices.AppendBytes == 32 && plan.Indices.AppendBytes == 8,
            "contiguous owned tail can combine with capped new range");
        Commit(arena, plan);
        Check(arena.TryAllocate(64, 16, out var combined) && combined!.VertexOffset == 96 && combined.IndexOffset == 24,
            "adjacent owned segments coalesce without changing published offsets");

        var fragmented = new SliceMeshArena(192, 24);
        fragmented.TryAllocate(64, 8, out var a); fragmented.TryAllocate(64, 8, out var b); fragmented.TryAllocate(64, 8, out var c);
        fragmented.Publish(b!); fragmented.RetireUnpublished(a!); fragmented.RetireUnpublished(c!); fragmented.ReclaimRetired();
        Check(fragmented.LargestVertexRange == 64 && fragmented.VertexCapacity - fragmented.VertexUsed == 128,
            "fragmented free total is distinct from largest contiguous allocation");
        Check(!SliceMeshGrowth.TryPlan(fragmented, 96, 12, 512, 128, 256, 32, out _, out _),
            "disjoint extension cannot promise contiguity across native gaps");
        Check(SliceMeshGrowth.TryPlan(fragmented, 96, 12, 515, 129, 384, 48, out plan, out _)
            && plan.Vertices.AppendBase == 544 && plan.Indices.AppendBase == 132,
            "global tails are aligned without claiming alignment padding");
    }

    private static void AppendValidationIsAtomic()
    {
        var arena = new SliceMeshArena(128, 32);
        Expect<InvalidOperationException>(() => arena.PrepareAppend(256, 128, 16, 32));
        Check(arena.VertexCapacity == 128 && arena.IndexCapacity == 32
            && arena.TryAllocate(128, 32, out _) && !arena.TryAllocate(32, 4, out _),
            "invalid second channel leaves both pools unchanged before publication");
        Expect<InvalidOperationException>(() => arena.PrepareAppend(257, 128, 64, 32));
        Expect<InvalidOperationException>(() => arena.PrepareAppend(uint.MaxValue - 31, 64, 64, 32));
    }

    private static void CancelledDemandDoesNotGrow()
    {
        var cancelled = new SliceMeshGrowthDemand(1024, 128); cancelled.Cancel();
        Check(!SliceMeshGrowth.TryGetDemand([cancelled, null], out var v, out var i) && v == 0 && i == 0,
            "a canceled or disposed waiting upload requires no replacement allocation or copy");
        var active = new SliceMeshGrowthDemand(64, 8);
        Check(SliceMeshGrowth.TryGetDemand([cancelled, active], out v, out i) && v == 64 && i == 8,
            "canceled large request cannot inflate another live upload's growth");
        active.Cancel();
        Check(!SliceMeshGrowth.TryGetDemand([cancelled, active], out _, out _), "cancel is idempotent across request lifetimes");
        var failed = new SliceMeshGrowthDemand(64, 8); failed.Fail("injected temporary allocation failure");
        Check(!SliceMeshGrowth.TryGetDemand([failed], out _, out _) && failed.Failure != null,
            "a failed waiting upload cannot trigger a growth attempt every frame");
        Check(SliceMeshGrowth.TryGetDemand([failed, new SliceMeshGrowthDemand(64, 8)], out _, out _),
            "a new explicit cut can retry after a temporary failure");
    }

    private static void OwnershipTransactionFaults()
    {
        var plan = new SliceMeshGrowthPlan(new(1024, 1152, 1024, 128), new(256, 288, 256, 32));
        var success = new FakeBackend();
        SliceMeshGrowthTransaction.Execute(plan, success);
        Check(success.Vertex == "newV" && success.Index == "newI" && success.Live.SetEquals(["newV", "newI"]),
            "successful publication releases only old handles");
        Check(success.Events.SequenceEqual(["allocate:V:1152", "allocate:I:288", "copy:V:1024", "copy:I:256", "wait", "publish", "free:oldI", "free:oldV"]),
            "copies preserve complete old bytes and both old handles remain alive until fence plus publication");
        foreach (string failure in new[] { "allocateV", "allocateI", "copy", "publish" })
        {
            var broken = new FakeBackend { Failure = failure };
            Expect<InvalidOperationException>(() => SliceMeshGrowthTransaction.Execute(plan, broken));
            Check(broken.Vertex == "oldV" && broken.Index == "oldI" && broken.Live.SetEquals(["oldV", "oldI"]),
                $"{failure} leaves old buffers usable and releases every successfully created replacement");
        }
        var uncertain = new FakeBackend { Failure = "uncertain" };
        Expect<MeshGrowthUncertainPublicationException>(() => SliceMeshGrowthTransaction.Execute(plan, uncertain));
        Check(uncertain.Live.SetEquals(["oldV", "oldI", "newV", "newI"])
            && uncertain.Retained.SetEquals(["newV", "newI"]), "unknown device/descriptor completion retains both old and new handles");

        var oneChannel = new FakeBackend();
        SliceMeshGrowthTransaction.Execute(new(plan.Vertices, new(256, 256, 256, 0)), oneChannel);
        Check(oneChannel.Index == "oldI" && oneChannel.Live.SetEquals(["newV", "oldI"])
            && !oneChannel.Events.Contains("copy:I:256"), "unchanged channel handle is never allocated, copied or freed");
    }

    private sealed class FakeBackend : IMeshGrowthBackend<string>
    {
        public string Vertex { get; private set; } = "oldV";
        public string Index { get; private set; } = "oldI";
        internal string Failure = "";
        internal readonly HashSet<string> Live = ["oldV", "oldI"], Retained = [];
        internal readonly List<string> Events = [];
        public string Allocate(bool index, uint bytes)
        {
            string channel = index ? "I" : "V"; Events.Add($"allocate:{channel}:{bytes}");
            if (Failure == "allocate" + channel) throw new InvalidOperationException("injected allocation failure");
            string resource = "new" + channel; Check(Live.Add(resource), "fake buffer allocated once"); return resource;
        }
        public void CopyAndWait(SliceMeshGrowthPlan plan, string vertex, string index)
        {
            Check(Live.Contains("oldV") && Live.Contains("oldI"), "old resources stay live during copy");
            if (plan.Vertices.Grows) Events.Add("copy:V:" + plan.Vertices.OldBytes);
            if (plan.Indices.Grows) Events.Add("copy:I:" + plan.Indices.OldBytes);
            if (Failure == "copy") throw new InvalidOperationException("injected recording failure");
            Events.Add("wait");
        }
        public void Publish(SliceMeshGrowthPlan plan, string vertex, string index)
        {
            Events.Add("publish");
            Check(Live.Contains("oldV") && Live.Contains("oldI") && Events.Contains("wait"), "publication follows completion and precedes frees");
            if (Failure == "publish") throw new InvalidOperationException("injected descriptor failure with successful rollback");
            if (Failure == "uncertain") throw new MeshGrowthUncertainPublicationException("injected failed rollback", new Exception());
            Vertex = vertex; Index = index;
        }
        public void Release(string buffer) { Check(Live.Remove(buffer), "each owned GPU handle is freed exactly once"); Events.Add("free:" + buffer); }
        public void RetainUncertain(string buffer) => Retained.Add(buffer);
    }

    private static void Commit(SliceMeshArena arena, SliceMeshGrowthPlan plan)
    {
        arena.PrepareAppend(plan.Vertices.AppendBase, plan.Vertices.AppendBytes, plan.Indices.AppendBase, plan.Indices.AppendBytes);
        arena.Append(plan.Vertices.AppendBase, plan.Vertices.AppendBytes, plan.Indices.AppendBase, plan.Indices.AppendBytes);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Mesh growth check failed: " + message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
