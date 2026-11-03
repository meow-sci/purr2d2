using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using RenderCore;

namespace Purr2D2.Runtime;

/// <summary>Optional, scoped clutter interest. Callers must publish from the settled
/// phase and clear on every owner/world transition. Coverage readiness is deliberately
/// unknown until readback and bubble installation can both be proven.</summary>
internal static class SliceClutterCoverageHooks
{
    internal const float MaximumRequestedRadius = 64;
    internal const int MaximumCandidates = 4096;
    internal const int MaximumAdditionalCells = 32;
    private sealed record ExpectedEcotype(ClutterEcotypePhysicalData Ecotype, CubeCellGrid.Cell[] Cells);
    private sealed record Interest(Vehicle Owner, object Parent, object Scene, long Epoch, float Radius, ExpectedEcotype[] Expected);
    private static Interest? _interest;
    private static Harmony? _harmony;
    private static int _installed;
    private static readonly SliceClutterReadiness Readiness = new();
    private static FieldInfo? _generatedIds;
    private static FieldInfo? _pendingReallocations;
    private static FieldInfo? _bubbleInstances;
    private static FieldInfo? _bubbleDesired;
    private static ConstructorInfo? _bubbleCellKey;
    private static MethodInfo? _desiredContains;
    private sealed record BubbleEntry(object Key, SliceClutterReadiness.Ticket Ticket);
    private sealed record BubbleObservation(Interest Interest, List<BubbleEntry> Entries, int Expected, long Revision);
    private sealed record CoverageReport(Interest Interest, int Expected, int Readbacks, int Resident, bool Ready, long Revision);
    internal readonly record struct CoverageStatus(string State, double Requested, float Admitted, int Expected, int Readbacks, int ReadyCells);
    private static double _requested;
    private static int _limited;
    private static CoverageReport? _coverage;
    internal static CoverageStatus Status
    {
        get
        {
            var interest = Volatile.Read(ref _interest);
            var report = Volatile.Read(ref _coverage);
            if (!Installed || interest == null)
                return new(Volatile.Read(ref _limited) != 0 ? "limited" : "unknown", Volatile.Read(ref _requested), interest?.Radius ?? 0, 0, 0, 0);
            if (!MatchesFrame(interest.Owner, interest.Owner.Parent, Universe.CurrentSystem,
                    interest.Owner, interest.Parent, interest.Scene))
                return new("unknown", Volatile.Read(ref _requested), 0, 0, 0, 0);
            if (report == null || !ReferenceEquals(interest, report.Interest))
                return new(Volatile.Read(ref _limited) != 0 ? "limited" : "unknown", Volatile.Read(ref _requested), interest.Radius, 0, 0, 0);
            bool current = report.Revision == Readiness.Revision;
            string state = Volatile.Read(ref _limited) != 0 ? "limited"
                : ClassifyCoverage(report.Expected, report.Readbacks, report.Resident, current);
            return new(state, Volatile.Read(ref _requested), interest.Radius,
                report.Expected, current ? report.Readbacks : 0, current ? report.Resident : 0);
        }
    }

    internal static string ClassifyCoverage(int expected, int readbacks, int ready, bool current) => !current ? "unknown"
        : expected > 0 && readbacks == expected && ready == expected ? "ready" : "pending";
    internal static void MarkLimited()
    {
        // A rejected request cannot safely keep the previous radius: it may no
        // longer cover the pending targets or respect the current cell budget.
        // Keep the readback ledger and requested distance for diagnostics and a
        // later valid request; only a successful preview republishes interest.
        Volatile.Write(ref _interest, null);
        Volatile.Write(ref _coverage, null);
        Volatile.Write(ref _limited, 1);
    }

    // The checked readback, invalidation and bubble-installation seams are available
    // on the locally verified game layout. Install still validates every target and
    // the exact radius call sites at runtime; CoverageStatus remains pending until
    // observed cell readbacks and bubble installation prove readiness.
    internal static bool ReadinessKnown => true;
    internal static bool Installed => Volatile.Read(ref _installed) != 0;

    internal static bool Install(Harmony owner, out string diagnostic)
    {
        Uninstall();
        if (!ReadinessKnown)
        { diagnostic = "Clutter coverage unavailable: physical-cell readiness is not verified."; return false; }
        if (_harmony != null)
        { diagnostic = "Previous clutter coverage hooks could not be removed."; return false; }
        var harmony = new Harmony(owner.Id + ".slice-clutter-coverage");
        _harmony = harmony;
        try
        {
            var render = RenderTarget();
            var bubble = BubbleTarget();
            var readback = ReadbackTarget();
            CheckBubbleLayout();
            var invalidations = InvalidationTargets();
            var clearBubble = AccessTools.Method(typeof(BubbleClutterStatics), nameof(BubbleClutterStatics.Clear), [typeof(ConstraintSim)])
                ?? throw new MissingMethodException("BubbleClutterStatics.Clear");
            _ = ReplaceBound(PatchProcessor.GetOriginalInstructions(render)).ToArray();
            _ = ReplaceBound(PatchProcessor.GetOriginalInstructions(bubble)).ToArray();
            var transpiler = new HarmonyMethod(typeof(SliceClutterCoverageHooks), nameof(ReplaceBound));
            harmony.Patch(render, transpiler: transpiler);
            harmony.Patch(bubble, transpiler: transpiler,
                prefix: new HarmonyMethod(typeof(SliceClutterCoverageHooks), nameof(BeforeBubble)),
                postfix: new HarmonyMethod(typeof(SliceClutterCoverageHooks), nameof(AfterBubble)));
            harmony.Patch(clearBubble, prefix: new HarmonyMethod(typeof(SliceClutterCoverageHooks), nameof(BeforeBubbleClear)));
            harmony.Patch(readback, postfix: new HarmonyMethod(typeof(SliceClutterCoverageHooks), nameof(AfterReadback)));
            foreach (var target in invalidations)
            {
                string prefix = target.Name == "UnloadGroundClutter" ? nameof(BeforeUnload)
                    : target.Name == "ProcessPendingReallocations" ? nameof(BeforePendingReallocation)
                    : nameof(BeforeInvalidation);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SliceClutterCoverageHooks), prefix));
            }
            // Transpilers validate again as Harmony composes patches. Until both
            // succeed, all callbacks return the original radius.
            Volatile.Write(ref _installed, 1);
            diagnostic = "Clutter coverage readiness unknown.";
            return true;
        }
        catch (Exception error)
        {
            Uninstall();
            diagnostic = "Clutter coverage hooks unavailable: " + error.GetBaseException().Message;
            return false;
        }
    }

    internal static void Uninstall()
    {
        Volatile.Write(ref _installed, 0);
        Clear();
        var harmony = _harmony;
        if (harmony == null) return;
        try { harmony.UnpatchAll(harmony.Id); _harmony = null; }
        catch (Exception error) { Console.WriteLine("[purr2d2] Clutter coverage cleanup failed: " + error.GetBaseException().Message); }
    }

    internal static void Clear()
    {
        Volatile.Write(ref _interest, null);
        Volatile.Write(ref _coverage, null);
        Volatile.Write(ref _limited, 0);
        Volatile.Write(ref _requested, 0);
        Readiness.Invalidate();
    }

    private static MethodInfo ReadbackTarget()
    {
        var field = AccessTools.Field(typeof(ClutterEcotypePhysicalData), "_idsGeneratedThisFrame");
        if (field?.FieldType != typeof(HashSet<(CubeCellGrid.Cell, int)>[]))
            throw new InvalidOperationException("Unsupported physical clutter readback layout.");
        var method = AccessTools.Method(typeof(ClutterEcotypePhysicalData), "ReadbackGroundClutter", [typeof(int)])
            ?? throw new MissingMethodException("ClutterEcotypePhysicalData.ReadbackGroundClutter");
        var getBuffer = AccessTools.Method(typeof(MultiCubeCellGrid), nameof(MultiCubeCellGrid.GetBufferId), [typeof(CubeCellGrid.Cell)]);
        var codes = PatchProcessor.GetOriginalInstructions(method);
        if (method.ReturnType != typeof(void) || getBuffer == null || codes.Count(c => c.Calls(getBuffer)) != 1
            || codes.Count(c => c.LoadsField(field)) != 2)
            throw new InvalidOperationException("Unsupported physical clutter readback loop.");
        _generatedIds = field;
        return method;
    }

    private static MethodInfo[] InvalidationTargets()
    {
        var type = typeof(ClutterEcotypePhysicalData);
        var pending = AccessTools.Field(type, "_reallocationCellCounts");
        if (pending?.FieldType != typeof(int[]))
            throw new InvalidOperationException("Unsupported clutter buffer reallocation layout.");
        _pendingReallocations = pending;
        MethodInfo Require(string name, params Type[] args)
        {
            var method = AccessTools.Method(type, name, args);
            if (method == null || method.ReturnType != typeof(void))
                throw new MissingMethodException(type.FullName, name);
            return method;
        }
        return [Require("UnloadGroundClutter", typeof(ReadOnlySpan<CubeCellGrid.Cell>)),
            Require("InvalidateGeneratedClutter"),
            Require("Reallocate", typeof(int), typeof(CommandBuffer), typeof(int)),
            Require("ProcessPendingReallocations", typeof(int), typeof(CommandBuffer)), Require("Dispose")];
    }

    // A postfix runs only after the native readback completed normally. Never force a
    // GPU wait. A failed observation discards all evidence rather than guessing.
    private static void AfterReadback(ClutterEcotypePhysicalData __instance, int frameIndex)
    {
        if (!Installed) return;
        try
        {
            if (_generatedIds?.GetValue(__instance) is not HashSet<(CubeCellGrid.Cell Cell, int BufferId)>[] frames
                || (uint)frameIndex >= (uint)frames.Length || frames[frameIndex] == null
                || frames[frameIndex].Count > MaximumCandidates)
            { Readiness.Invalidate(); return; }
            foreach (var (cell, bufferId) in frames[frameIndex])
            {
                int current = __instance.VesselGrid.GetBufferId(cell);
                if (current != bufferId) { Readiness.Invalidate(__instance, cell); continue; }
                int[] counts = __instance.EcotypeCountData;
                if ((uint)bufferId >= (uint)counts.Length
                    || !Readiness.RecordReadback(__instance, cell, bufferId, current, counts[bufferId]))
                { Readiness.Invalidate(); return; }
            }
        }
        catch { Readiness.Invalidate(); }
    }

    private static void BeforeBubbleClear(BubbleClutterStatics __instance) => Readiness.InvalidateBubble(__instance);

    private static void BeforeInvalidation(ClutterEcotypePhysicalData __instance) => Readiness.Invalidate(__instance);

    private static void BeforeUnload(ClutterEcotypePhysicalData __instance, ReadOnlySpan<CubeCellGrid.Cell> cellsToUnload)
    {
        if (cellsToUnload.Length > MaximumAdditionalCells) { Readiness.Invalidate(__instance); return; }
        foreach (var cell in cellsToUnload) Readiness.Invalidate(__instance, cell);
    }

    private static void BeforePendingReallocation(ClutterEcotypePhysicalData __instance, int frameIndex)
    {
        try
        {
            if (_pendingReallocations?.GetValue(__instance) is not int[] pending
                || (uint)frameIndex >= (uint)pending.Length || pending[frameIndex] != -1)
                Readiness.Invalidate(__instance);
        }
        catch { Readiness.Invalidate(); }
    }

    private static void CheckBubbleLayout()
    {
        var bubble = typeof(BubbleClutterStatics);
        var key = AccessTools.Inner(bubble, "CellKey");
        var instances = AccessTools.Field(bubble, "_instanceData");
        var desired = AccessTools.Field(bubble, "_desiredCells");
        if (key == null || !key.IsValueType || AccessTools.Field(key, "EcotypeIndex")?.FieldType != typeof(int)
            || AccessTools.Field(key, "Cell")?.FieldType != typeof(CubeCellGrid.Cell)
            || instances == null || !typeof(IDictionary).IsAssignableFrom(instances.FieldType)
            || !instances.FieldType.IsGenericType || instances.FieldType.GetGenericArguments()[0] != key
            || desired == null || !desired.FieldType.IsGenericType
            || desired.FieldType.GetGenericTypeDefinition() != typeof(HashSet<>)
            || desired.FieldType.GetGenericArguments()[0] != key)
            throw new InvalidOperationException("Unsupported bubble clutter cell layout.");
        var constructor = key.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(int), typeof(CubeCellGrid.Cell)], null);
        var contains = desired.FieldType.GetMethod("Contains", [key]);
        if (constructor == null || contains?.ReturnType != typeof(bool))
            throw new InvalidOperationException("Unsupported bubble clutter cell operations.");
        var update = BubbleTarget();
        var addCell = AccessTools.Method(bubble, "AddCell");
        var overlapping = AccessTools.Method(typeof(MultiCubeCellGrid), nameof(MultiCubeCellGrid.GetOverlappingCellsLoaded), [typeof(double3), typeof(double)]);
        var instanceContains = instances.FieldType.GetMethod("ContainsKey", [key]);
        var codes = PatchProcessor.GetOriginalInstructions(update);
        if (addCell == null || overlapping == null || instanceContains == null || codes.Count(c => c.Calls(addCell)) != 1
            || codes.Count(c => c.Calls(overlapping)) != 1 || codes.Count(c => c.Calls(instanceContains)) != 1)
            throw new InvalidOperationException("Unsupported bubble clutter installation loop.");
        _bubbleInstances = instances; _bubbleDesired = desired; _bubbleCellKey = constructor; _desiredContains = contains;
    }

    // Run only for the owner's bubble and the exact ecotype array used for its
    // immutable preview. Missing stamps are unknown, never guessed from loadedCells.
    private static void BeforeBubble(BubbleClutterStatics __instance, ClutterEcotypePhysicalData[] __2,
        List<VehicleUpdateState> __3, out BubbleObservation? __state)
    {
        __state = null;
        var interest = Volatile.Read(ref _interest);
        if (!Installed || interest == null || __2.Length != interest.Expected.Length
            || !MatchesFrame(interest.Owner, interest.Owner.Parent, Universe.CurrentSystem,
                interest.Owner, interest.Parent, interest.Scene)
            || !__3.Any(state => ReferenceEquals(state.ReadOnlyVehicle, interest.Owner))) return;
        try
        {
            if (_bubbleInstances?.GetValue(__instance) is not IDictionary instances || _bubbleCellKey == null) return;
            Volatile.Write(ref _coverage, null);
            long revision = Readiness.Revision;
            int expectedCount = 0;
            var entries = new List<BubbleEntry>();
            for (int i = 0; i < __2.Length; i++)
            {
                var expected = interest.Expected[i];
                if (!ReferenceEquals(__2[i], expected.Ecotype)) return;
                foreach (var cell in expected.Cells)
                {
                    expectedCount++;
                    object key = _bubbleCellKey.Invoke([i, cell]);
                    int id = expected.Ecotype.VesselGrid.GetBufferId(cell);
                    if (Readiness.BeginBubble(__instance, expected.Ecotype, cell, id, instances.Contains(key), out var ticket))
                        entries.Add(new BubbleEntry(key, ticket));
                }
            }
            __state = new BubbleObservation(interest, entries, expectedCount, revision);
        }
        catch { Readiness.Invalidate(); }
    }

    // Successful native update only. A missing ticket or dictionary mismatch leaves
    // coverage unknown; the ledger checks the same buffer and generation again.
    private static void AfterBubble(BubbleClutterStatics __instance, BubbleObservation? __state)
    {
        if (!Installed || __state == null || !ReferenceEquals(__state.Interest, Volatile.Read(ref _interest))
            || !MatchesFrame(__state.Interest.Owner, __state.Interest.Owner.Parent, Universe.CurrentSystem,
                __state.Interest.Owner, __state.Interest.Parent, __state.Interest.Scene)) return;
        try
        {
            if (_bubbleInstances?.GetValue(__instance) is not IDictionary instances
                || _bubbleDesired?.GetValue(__instance) is not { } desired || _desiredContains == null) return;
            int resident = 0;
            foreach (var entry in __state.Entries)
            {
                var ticket = entry.Ticket;
                var ecotype = (ClutterEcotypePhysicalData)ticket.Ecotype;
                bool contains = _desiredContains.Invoke(desired, [entry.Key]) is true;
                if (Readiness.FinishBubble(ticket, ecotype.VesselGrid.GetBufferId(ticket.Cell), contains, instances.Contains(entry.Key)))
                    resident++;
            }
            long revision = Readiness.Revision;
            if (revision != __state.Revision || !ReferenceEquals(__state.Interest, Volatile.Read(ref _interest))) return;
            bool complete = __state.Expected > 0 && resident == __state.Expected && __state.Entries.Count == __state.Expected;
            Volatile.Write(ref _coverage, new CoverageReport(__state.Interest, __state.Expected, __state.Entries.Count, resident, complete, revision));
        }
        catch { Readiness.Invalidate(); }
    }

    private static MethodInfo RenderTarget() => AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.OnFrame),
        [typeof(ReadOnlySpan<IViewport>), typeof(CommandBuffer), typeof(int)])
        ?? throw new MissingMethodException("GroundClutterRenderer.OnFrame");

    private static MethodInfo BubbleTarget() => AccessTools.Method(typeof(BubbleClutterStatics), nameof(BubbleClutterStatics.Update),
        [typeof(ConstraintSim), typeof(Celestial), typeof(ClutterEcotypePhysicalData[]), typeof(List<VehicleUpdateState>), typeof(BubbleOrigin).MakeByRefType()])
        ?? throw new MissingMethodException("BubbleClutterStatics.Update");

    internal static IEnumerable<CodeInstruction> ReplaceBound(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        var getter = AccessTools.PropertyGetter(typeof(Vehicle), nameof(Vehicle.BoundingSphereRadiusBody))
            ?? throw new MissingMethodException("Vehicle.BoundingSphereRadiusBody");
        var replacement = AccessTools.Method(typeof(SliceClutterCoverageHooks), nameof(ClutterInterestBound));
        var indices = Enumerable.Range(0, codes.Count).Where(i => codes[i].Calls(getter)).ToArray();
        if (indices.Length != 1 || (codes[indices[0]].opcode != OpCodes.Call && codes[indices[0]].opcode != OpCodes.Callvirt)
            || indices[0] > 0 && codes[indices[0] - 1].opcode.OpCodeType == OpCodeType.Prefix)
            throw new InvalidOperationException("Unexpected clutter vehicle-radius call site.");
        codes[indices[0]].opcode = OpCodes.Call;
        codes[indices[0]].operand = replacement;
        return codes;
    }

    private static float ClutterInterestBound(Vehicle vehicle)
    {
        float native = vehicle.BoundingSphereRadiusBody;
        var interest = Volatile.Read(ref _interest);
        return Installed && interest != null && MatchesFrame(vehicle, vehicle.Parent, Universe.CurrentSystem,
                interest.Owner, interest.Parent, interest.Scene)
            ? Math.Max(native, interest.Radius) : native;
    }

    internal static bool MatchesFrame(object? owner, object? parent, object? scene,
        object? expectedOwner, object? expectedParent, object? expectedScene)
        => owner != null && parent != null && scene != null && ReferenceEquals(owner, expectedOwner)
            && ReferenceEquals(parent, expectedParent) && ReferenceEquals(scene, expectedScene);

    internal static bool SameEcotypes(ClutterEcotypePhysicalData[]? previous, ClutterEcotypePhysicalData[] current)
    {
        if (previous == null || previous.Length != current.Length) return false;
        for (int i = 0; i < current.Length; i++) if (!ReferenceEquals(previous[i], current[i])) return false;
        return true;
    }

    /// <summary>Bounded copy of the cell enumeration in MultiCubeCellGrid.Update.
    /// The caller supplies the exact ecotypes for the owner's celestial, its native
    /// normalized CCF direction, and a radius already including current, recent and
    /// pending endpoints. Never call during a concurrent grid mutation.</summary>
    internal static bool TryPublish(Vehicle owner, long epoch, double3 direction,
        IReadOnlyList<ClutterEcotypePhysicalData> ecotypes, double radius, out string diagnostic)
    {
        var previous = Volatile.Read(ref _interest);
        var parent = owner.Parent; var scene = Universe.CurrentSystem;
        if (previous != null && (!MatchesFrame(owner, parent, scene, previous.Owner, previous.Parent, previous.Scene)
            || previous.Epoch != epoch))
        { Clear(); previous = null; }
        Volatile.Write(ref _requested, radius);
        if (!Installed || parent == null || scene == null || !GameSettings.GenerateGroundClutter() || !GameSettings.GetGroundClutterCollisions())
        { Clear(); diagnostic = "Clutter coverage unavailable."; return false; }
        if (!double.IsFinite(radius) || radius < 0 || radius > MaximumRequestedRadius
            || !double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || !double.IsFinite(direction.Z)
            || Math.Abs(direction.LengthSquared() - 1) > 1e-6 || ecotypes.Count == 0 || ecotypes.Count > MaximumCandidates)
        { MarkLimited(); diagnostic = "Clutter coverage limited or unknown."; return false; }
        float original = owner.BoundingSphereRadiusBody;
        if (!float.IsFinite(original) || original < 0)
        { MarkLimited(); diagnostic = "Clutter coverage unknown."; return false; }
        int candidates = 0, additional = 0;
        var expected = new List<ExpectedEcotype>(ecotypes.Count);
        foreach (var ecotype in ecotypes)
        {
            if (ecotype == null || !PreviewEcotype(ecotype, direction, original,
                    Math.Max(original, radius), ref candidates, ref additional, out var cells))
            { MarkLimited(); diagnostic = "Clutter coverage limited or unknown."; return false; }
            expected.Add(new ExpectedEcotype(ecotype, cells));
        }
        var next = new Interest(owner, parent, scene, epoch, (float)radius, expected.ToArray());
        var report = Volatile.Read(ref _coverage);
        bool sameCells = previous != null && ReferenceEquals(previous.Owner, owner) && previous.Epoch == epoch
            && SameExpected(previous.Expected, next.Expected);
        Volatile.Write(ref _interest, next);
        if (sameCells && report != null && ReferenceEquals(report.Interest, previous) && report.Revision == Readiness.Revision)
            Volatile.Write(ref _coverage, report with { Interest = next });
        else Volatile.Write(ref _coverage, null);
        Volatile.Write(ref _limited, 0);
        diagnostic = Status.State switch
        {
            "ready" => "Clutter coverage is ready.",
            "pending" => "Clutter coverage is waiting for generated cells or physics installation.",
            "limited" => "Clutter coverage is limited by the request or preview bounds.",
            _ => "Clutter coverage readiness is unknown."
        };
        return true;
    }

    private static bool SameExpected(ExpectedEcotype[] left, ExpectedEcotype[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (!ReferenceEquals(left[i].Ecotype, right[i].Ecotype) || !left[i].Cells.AsSpan().SequenceEqual(right[i].Cells)) return false;
        return true;
    }

    internal static bool Preview(MultiCubeCellGrid grid, double3 direction, double original, double extended,
        ref int candidates, ref int additional)
        => Preview(grid, direction, original, extended, ref candidates, ref additional, out _);

    // Native physical generation returns immediately for non-collideable
    // ecotypes. Keep their array slots for index validation, but do not wait
    // for readbacks or resident cells that native code will never generate.
    internal static bool PreviewEcotype(ClutterEcotypePhysicalData ecotype, double3 direction, double original,
        double extended, ref int candidates, ref int additional, out CubeCellGrid.Cell[] expected)
    {
        expected = [];
        if (ecotype.EcotypeReference == null) return false;
        if (!ecotype.EcotypeReference.Collideable) return true;
        return Preview(ecotype.VesselGrid, direction, original, extended, ref candidates, ref additional, out expected);
    }

    private static bool Preview(MultiCubeCellGrid grid, double3 direction, double original, double extended,
        ref int candidates, ref int additional, out CubeCellGrid.Cell[] expected)
    {
        expected = [];
        if (grid.GridResolution <= 0 || grid.GridResolution > int.MaxValue - 31 || !double.IsFinite(grid.SphereRadius) || grid.SphereRadius <= 0
            || !double.IsFinite(grid.InstanceReach) || grid.InstanceReach < 0) return false;
        int kernel = grid.GetKernelSize(extended);
        // Check before multiplying or entering a loop: pathological native values
        // must never turn this preview into unbounded work or allocation.
        if (kernel < 0 || kernel > 31) return false;
        int side = 2 * kernel + 1;
        if (side * side > MaximumCandidates - candidates) return false;
        double2 uv = CubeCellGrid.DirectionToQscUv(direction, out int face);
        if (face < 0 || face >= 6 || !double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return false;
        int2 center = grid.GetCellOnFace(uv);
        if (center.X < 0 || center.X >= grid.GridResolution || center.Y < 0 || center.Y >= grid.GridResolution) return false;
        double3 position = direction * grid.SphereRadius;
        var seen = new HashSet<CubeCellGrid.Cell>();
        var inRange = new List<CubeCellGrid.Cell>();
        for (int x = -kernel; x <= kernel; x++)
        for (int y = -kernel; y <= kernel; y++)
        {
            candidates++;
            var cell = new CubeCellGrid.Cell(center.X + x, center.Y + y, face);
            if (cell.X < 0 || cell.X >= grid.GridResolution || cell.Y < 0 || cell.Y >= grid.GridResolution)
            {
                if (!CubeCellGrid.ResolveAdjacentCell(cell, grid.GridResolution, out var adjacent)) continue;
                cell = adjacent;
            }
            if (!seen.Add(cell) || !grid.CellInRange(cell, position, extended)) continue;
            inRange.Add(cell);
            if (!grid.CellInRange(cell, position, original) && ++additional > MaximumAdditionalCells) return false;
        }
        expected = inRange.ToArray();
        return true;
    }
}
