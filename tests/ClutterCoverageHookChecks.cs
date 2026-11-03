using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Runtime;

internal static class ClutterCoverageHookChecks
{
    private static void Check(bool value, string text)
    {
        if (!value) throw new Exception(text);
    }

    private static void Main()
    {
        var type = typeof(SliceClutterCoverageHooks);
        var getter = AccessTools.PropertyGetter(typeof(Vehicle), nameof(Vehicle.BoundingSphereRadiusBody));
        foreach (string targetName in new[] { "RenderTarget", "BubbleTarget" })
        {
            var target = (MethodInfo)type.GetMethod(targetName, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            var original = PatchProcessor.GetOriginalInstructions(target);
            Check(original.Count(x => x.Calls(getter)) == 1, targetName + " radius count");
            var replaced = SliceClutterCoverageHooks.ReplaceBound(original).ToArray();
            Check(replaced.Count(x => x.Calls(getter)) == 0, targetName + " replacement");
            Check(replaced.Length == original.Count, targetName + " instruction count");
        }
        try
        {
            SliceClutterCoverageHooks.ReplaceBound(new[] { new CodeInstruction(OpCodes.Nop) }).ToArray();
            throw new Exception("Missing call must be rejected");
        }
        catch (InvalidOperationException) { }
        try
        {
            SliceClutterCoverageHooks.ReplaceBound(new[] { new CodeInstruction(OpCodes.Callvirt, getter), new CodeInstruction(OpCodes.Callvirt, getter) }).ToArray();
            throw new Exception("Duplicate call must be rejected");
        }
        catch (InvalidOperationException) { }
        var grid = new MultiCubeCellGrid(600000, 30, 20);
        grid.Init();
        int candidates = 0, additional = 0;
        Check(SliceClutterCoverageHooks.Preview(grid, new double3(1, 0, 0), 2, 2, ref candidates, ref additional), "native grid preview");
        Check(candidates > 0 && candidates <= 4096 && additional == 0, "unchanged radius preview");
        candidates = 4096; additional = 0;
        Check(!SliceClutterCoverageHooks.Preview(grid, new double3(1, 0, 0), 2, 10, ref candidates, ref additional), "candidate cap");
        var fineGrid = new MultiCubeCellGrid(600000, 2, 0);
        fineGrid.Init();
        candidates = 0; additional = 32;
        Check(!SliceClutterCoverageHooks.Preview(fineGrid, new double3(1, 0, 0), 0, 64, ref candidates, ref additional), "additional-cell cap");
        var visualOnly = (ClutterEcotypePhysicalData)RuntimeHelpers.GetUninitializedObject(typeof(ClutterEcotypePhysicalData));
        var visualReference = (ClutterEcotypeReference)RuntimeHelpers.GetUninitializedObject(typeof(ClutterEcotypeReference));
        visualReference.CollisionType = new(ClutterEcotypePhysicalData.CollisionType.None);
        AccessTools.Field(typeof(ClutterEcotypePhysicalData), nameof(ClutterEcotypePhysicalData.EcotypeReference)).SetValue(visualOnly, visualReference);
        candidates = additional = 0;
        Check(SliceClutterCoverageHooks.PreviewEcotype(visualOnly, new double3(1, 0, 0), 2, 64,
                ref candidates, ref additional, out var visualCells)
            && visualCells.Length == 0 && candidates == 0 && additional == 0,
            "non-collideable ecotypes retain their index but require no unavailable physical readbacks");
        var actor = new object(); var parent = new object(); var scene = new object();
        Check(SliceClutterCoverageHooks.MatchesFrame(actor, parent, scene, actor, parent, scene)
            && !SliceClutterCoverageHooks.MatchesFrame(actor, new object(), scene, actor, parent, scene)
            && !SliceClutterCoverageHooks.MatchesFrame(actor, parent, new object(), actor, parent, scene)
            && !SliceClutterCoverageHooks.MatchesFrame(actor, parent, null, actor, parent, null),
            "interest is scoped to the exact owner, parent and live scene");
        var otherEcotype = (ClutterEcotypePhysicalData)RuntimeHelpers.GetUninitializedObject(typeof(ClutterEcotypePhysicalData));
        var nativeEcotypes = new[] { visualOnly, otherEcotype };
        var savedEcotypes = (ClutterEcotypePhysicalData[])nativeEcotypes.Clone();
        Check(SliceClutterCoverageHooks.SameEcotypes(savedEcotypes, nativeEcotypes), "unchanged ecotype identities retain interest");
        nativeEcotypes[0] = otherEcotype;
        Check(!SliceClutterCoverageHooks.SameEcotypes(savedEcotypes, nativeEcotypes), "in-place ecotype replacement invalidates interest");
        Check(!SliceClutterCoverageHooks.Installed && SliceClutterCoverageHooks.ReadinessKnown,
            "checked layout supports an explicitly requested installation");
        var interestField = AccessTools.Field(type, "_interest");
        var reportField = AccessTools.Field(type, "_coverage");
        var requestedField = AccessTools.Field(type, "_requested");
        interestField.SetValue(null, RuntimeHelpers.GetUninitializedObject(interestField.FieldType));
        reportField.SetValue(null, RuntimeHelpers.GetUninitializedObject(reportField.FieldType));
        requestedField.SetValue(null, 27d);
        SliceClutterCoverageHooks.MarkLimited();
        Check(interestField.GetValue(null) == null && reportField.GetValue(null) == null
            && SliceClutterCoverageHooks.Status is { State: "limited", Requested: 27, Admitted: 0 },
            "rejected coverage clears stale interest and report while retaining requested-distance diagnostics");
        SliceClutterCoverageHooks.Clear();
        var testHarmony = new Harmony("purr2d2.test.coverage");
        Check(SliceClutterCoverageHooks.Install(testHarmony, out var reason), "checked hooks install: " + reason);
        Check(SliceClutterCoverageHooks.Installed, "paired coverage hooks become active together");
        SliceClutterCoverageHooks.Uninstall();
        Check(!SliceClutterCoverageHooks.Installed, "coverage hooks uninstall cleanly");
        foreach (string name in new[] { "ReadbackTarget", "InvalidationTargets", "CheckBubbleLayout" })
        {
            var result = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            Check(name == "CheckBubbleLayout" || result != null, name + " checked binding");
        }
        var physical = typeof(ClutterEcotypePhysicalData);
        var generated = AccessTools.Field(physical, "_idsGeneratedThisFrame");
        Check(generated?.FieldType == typeof(HashSet<(CubeCellGrid.Cell, int)>[]), "readback generation layout");
        var readback = AccessTools.Method(physical, "ReadbackGroundClutter", [typeof(int)]);
        var getBuffer = AccessTools.Method(typeof(MultiCubeCellGrid), nameof(MultiCubeCellGrid.GetBufferId), [typeof(CubeCellGrid.Cell)]);
        Check(readback != null && PatchProcessor.GetOriginalInstructions(readback).Count(x => x.Calls(getBuffer)) == 1,
            "readback checks current buffer");
        foreach (var name in new[] { "UnloadGroundClutter", "InvalidateGeneratedClutter", "Reallocate", "Dispose" })
            Check(AccessTools.GetDeclaredMethods(physical).Count(x => x.Name == name) == 1, name + " invalidation seam");
        var bubbleType = typeof(BubbleClutterStatics);
        var instanceData = AccessTools.Field(bubbleType, "_instanceData");
        var desiredCells = AccessTools.Field(bubbleType, "_desiredCells");
        var cellKey = AccessTools.Inner(bubbleType, "CellKey");
        Check(cellKey != null && cellKey.IsValueType && AccessTools.Field(cellKey, "EcotypeIndex")?.FieldType == typeof(int)
            && AccessTools.Field(cellKey, "Cell")?.FieldType == typeof(CubeCellGrid.Cell), "bubble cell key layout");
        Check(instanceData?.FieldType.IsGenericType == true && instanceData.FieldType.GetGenericArguments()[0] == cellKey
            && desiredCells?.FieldType.IsGenericType == true && desiredCells.FieldType.GetGenericArguments()[0] == cellKey,
            "bubble cell collections layout");
        Check(SliceClutterCoverageHooks.ClassifyCoverage(2, 2, 2, true) == "ready"
            && SliceClutterCoverageHooks.ClassifyCoverage(2, 1, 1, true) == "pending"
            && SliceClutterCoverageHooks.ClassifyCoverage(0, 0, 0, true) == "pending"
            && SliceClutterCoverageHooks.ClassifyCoverage(2, 2, 2, false) == "unknown", "aggregate coverage status");
        var reach = new SliceClutterCoverageRadius(new double3(10, 0, 0));
        Check(reach.Include(new double3(13, 0, 0)) && reach.Include(new double3(10, 4, 0), 5)
            && reach.Reach == 11, "endpoint and pending bounds reach");
        Check(!reach.Include(new double3(double.NaN, 0, 0)) && !reach.Include(new double3(0, 0, 0), -1), "invalid bounds refused");
        var ledger = new SliceClutterReadiness();
        var ecotype = new object(); var bubble = new object(); var cell = new CubeCellGrid.Cell(1, 2, 0);
        Check(!ledger.RecordReadback(ecotype, cell, 3, 4, 1), "stale buffer refused");
        Check(ledger.RecordReadback(ecotype, cell, 3, 3, 0), "empty readback recorded");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, false, out var ticket)
            && ledger.FinishBubble(ticket, 3, true, false), "empty cell ready after update");
        Check(ledger.RecordReadback(ecotype, cell, 3, 3, 1), "nonempty readback recorded");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket)
            && !ledger.FinishBubble(ticket, 3, true, true), "old resident cell is unproven");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, false, out ticket)
            && ledger.FinishBubble(ticket, 3, true, true), "newly installed cell ready");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket)
            && ledger.FinishBubble(ticket, 3, true, true), "same generation stays proven");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket), "pending ticket");
        var otherCell = new CubeCellGrid.Cell(2, 2, 0);
        Check(ledger.RecordReadback(ecotype, otherCell, 4, 4, 0), "second cell");
        ledger.Invalidate(ecotype, otherCell);
        Check(!ledger.FinishBubble(ticket, 3, true, true), "in-flight ticket invalidated by concurrent change");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket)
            && ledger.FinishBubble(ticket, 3, true, true), "unrelated cell invalidation preserves installed proof");
        ledger.Invalidate(ecotype, cell);
        Check(!ledger.FinishBubble(ticket, 3, true, true), "invalidation rejects pending ticket");
        Check(ledger.RecordReadback(ecotype, cell, 3, 3, 1), "new generation");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket)
            && !ledger.FinishBubble(ticket, 3, true, true), "reused buffer is not proof");
        Check(ledger.BeginBubble(bubble, ecotype, cell, 3, false, out ticket)
            && ledger.FinishBubble(ticket, 3, true, true), "new generation installed");
        long revision = ledger.Revision;
        ledger.InvalidateBubble(bubble);
        Check(ledger.Revision != revision && ledger.BeginBubble(bubble, ecotype, cell, 3, true, out ticket)
            && !ledger.FinishBubble(ticket, 3, true, true), "bubble clear revokes installation proof");
        Console.WriteLine("Clutter coverage hook checks passed.");
    }
}
