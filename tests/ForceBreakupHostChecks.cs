using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Xml.Linq;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Rendering;

/// <summary>Use the real KSA serializer/types and native call contracts, without starting the game.</summary>
internal static class ForceBreakupHostChecks
{
    private sealed record VisualNode(bool Visible, VisualNode[] Descendants);

    internal static void Run()
    {
        CheckPlanner();
        CheckCompoundPlacement();

        var shape = new AuthoredDebrisShape([new("native_hull", "native_paint", false), new("native_window", "native_glass", true)],
            new(1, -2, 3), new(4, 6, 8), 2, 100);
        var (id, xml) = AuthoredDebrisAssets.Build(shape);
        var again = AuthoredDebrisAssets.Build(shape);
        Check(id == again.Id && xml.SequenceEqual(again.Xml), "identical authored debris reuses a durable content identity");
        Check(AuthoredDebrisAssets.Build(shape with { MassKg = 101 }).Id != id
            && AuthoredDebrisAssets.Build(shape with { Scale = 3 }).Id != id
            && AuthoredDebrisAssets.Build(shape with { Center = double3.Zero }).Id != id,
            "mass and mesh placement participate in durable identity");
        var doc = XDocument.Parse(Encoding.UTF8.GetString(xml));
        Check(!doc.Descendants("MeshAtlas").Any() && doc.Descendants("Mesh").All(m => ((string?)m.Attribute("Id"))?.StartsWith("native_") == true),
            "authored debris references existing meshes without new GLBs or GPU mesh uploads");
        var bundle = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), xml, id, 2);
        var root = bundle.Assets.OfType<PartTemplate>().Single(p => p.Id == id);
        var visual = bundle.Assets.OfType<PartTemplate>().Single(p => p.Id == id + "_surface");
        Check(root.IsHidden && visual.IsHidden && !root.IsSubPart && visual.IsSubPart,
            "Force debris roots and visual subparts remain native parts but are hidden from both editor catalogs");
        var different = AuthoredDebrisAssets.Build(shape with { MassKg = 101 });
        var otherVisual = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), different.Xml, different.Id, 2)
            .Assets.OfType<PartTemplate>().Single(p => p.Id == different.Id + "_surface");
        Check(!visual.Components.Select(c => c.Id).Intersect(otherVisual.Components.Select(c => c.Id)).Any(),
            "unrelated debris has unique model IDs for KSA's global rendering cache");
        Check(root.SubPartInstances.Count == 1 && root.SubPartInstances[0].InstanceOf == visual.Id,
            "the native template reloads one independently pickable visual child");
        var transform = root.SubPartInstances[0].Transform!;
        Check(transform._positionRaw!.X == -2 && transform._positionRaw.Y == 4 && transform._positionRaw.Z == -6
            && transform._scaleRaw!.X == 2 && transform._scaleRaw.Y == 2 && transform._scaleRaw.Z == 2,
            "native XML preserves authored mesh scale and recenters the collider at the debris COM");
        Check(root.InertMasses.Single() is CustomMassTemplate && root.Components.Single() is ColliderModule.Template collider
            && collider.Colliders.Single() is BoxColliderTemplate,
            "each debris root reloads native dry mass, inertia and a finite collider");
        Check(visual.Components.OfType<PartModelModule.Template>().Single().Mesh!.Id == "native_hull"
            && visual.Components.OfType<PartModelGlassModule.Template>().Single().Mesh!.Id == "native_window"
            && visual.Components.OfType<MeshViewModule.Template>().Count() == 2,
            "native serializer retains opaque and glass rendering and picking for both meshes");
        Check(bundle.Assets.OfType<PartTemplate>().All(p => p.RocketCores.Count == 0 && p.RocketEngineControllers.Count == 0
            && p.RocketThrusterControllers.Count == 0 && p.Control == null && p.Batteries.Count == 0
            && p.Components.All(c => c is PartModelModule.Template or PartModelGlassModule.Template or MeshViewModule.Template or ColliderModule.Template)),
            "replacement templates contain no tanks, controls, crew, engines or other functional modules");
        var commit = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ForceVehicleBreakup), "Commit"));
        int drain = commit.FindIndex(c => c.Calls(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.DepleteConsumables))));
        int destroy = commit.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Universe) && m.Name == nameof(Universe.DestroyVehicle));
        Check(drain >= 0 && destroy > drain && !commit.Any(c => c.operand is MethodInfo m
            && (m.DeclaringType == typeof(ExplosionSystem) || m.DeclaringType == typeof(PartFailure) || m.DeclaringType == typeof(PartFailureEvent))),
            "native breakup empties stores before source deletion and invokes no explosive failure or debris lottery");
        Check(commit.Any(c => c.Calls(AccessTools.Method(typeof(CollisionAvoidancePair), nameof(CollisionAvoidancePair.Link)))),
            "replacement siblings receive native overlap protection before release");
        Check(commit.Any(c => c.operand is MethodInfo bodyMethod && bodyMethod.DeclaringType == typeof(Vehicle) && bodyMethod.Name == "GetBody2Cci")
            && commit.Any(c => c.operand is MethodInfo positionMethod && positionMethod.DeclaringType == typeof(Vehicle) && positionMethod.Name == "GetPositionCci")
            && commit.Any(c => c.operand is MethodInfo velocityMethod && velocityMethod.DeclaringType == typeof(Vehicle) && velocityMethod.Name == "GetVelocityCci"),
            "commit samples the source's current world frame and velocity only when replacements are released");
        var candidates = ForceVehicleBreakup.BuildSiblingCandidates([
            new(double3.Zero, 1), new(new(1.5, 0, 0), 1), new(new(10, 0, 0), 1), new(new(10.5, 0, 0), 1)
        ]);
        Check(candidates.SequenceEqual([new ForceVehicleBreakup.SiblingPair(0, 1), new ForceVehicleBreakup.SiblingPair(2, 3)]),
            "worker candidate generation retains near sibling pairs and rejects clearly separated spheres");
        var overlapCheck = AccessTools.Method(typeof(ForceVehicleBreakup), nameof(ForceVehicleBreakup.SphereBoundsOverlap));
        var candidateBuilder = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ForceVehicleBreakup), nameof(ForceVehicleBreakup.BuildSiblingCandidates)));
        Check(!commit.Any(c => c.Calls(overlapCheck)) && candidateBuilder.Any(c => c.Calls(overlapCheck)),
            "Commit links precomputed pair indices without an all-pairs geometric scan");
        Check(commit.Any(c => c.operand is string value && value.Contains("stored contents may already have been released", StringComparison.Ordinal)),
            "teardown failures report that source contents may already have been released");

        var constructor = PatchProcessor.GetOriginalInstructions(AccessTools.Constructor(typeof(ForceVehicleBreakup), [typeof(Vehicle)]));
        Check(!constructor.Any(c => c.operand is MethodInfo m && (m.DeclaringType == typeof(MeshReference)
                || m.DeclaringType == typeof(Part) || m.DeclaringType == typeof(PartTree))),
            "breakup construction records only source/thread identity and defers part and mesh capture");
        var worker = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ForceVehicleBreakup), "RunWorker"));
        Check(worker.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Task) && m.Name == nameof(Task.Run))
            && !worker.Any(c => c.operand is MethodInfo m && (m.Name == "StartNew" || m.Name == "Wait")),
            "background preparation uses an ordinary bounded Task.Run worker without LongRunning or joins");
        Check(!worker.Any(c => c.operand is MethodInfo m && (m.DeclaringType == typeof(Vehicle)
                || m.DeclaringType == typeof(Part) || m.DeclaringType == typeof(PartTree) || m.DeclaringType == typeof(Mod))),
            "worker grouping, XML and hash preparation stay detached from native KSA objects");
        var dispose = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ForceVehicleBreakup), nameof(ForceVehicleBreakup.Dispose)));
        Check(dispose.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(CancellationTokenSource) && m.Name == nameof(CancellationTokenSource.Cancel))
            && dispose.Any(c => c.operand is MethodInfo m && m.Name == nameof(Task.ContinueWith))
            && !dispose.Any(c => c.operand is MethodInfo m && m.Name is "Wait" or "Join"),
            "cancelled worker preparation observes completion without blocking the main thread");
        var poll = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ForceVehicleBreakup), "AdvanceWorker"));
        int completedCheck = poll.FindIndex(c => c.operand is MethodInfo m && m.Name == "get_IsCompleted");
        int consumeResult = poll.FindIndex(c => c.operand is MethodInfo m && m.Name == "GetResult");
        Check(completedCheck >= 0 && consumeResult > completedCheck
            && !poll.Any(c => c.operand is MethodInfo m && (m.Name == "Wait" || m.Name == "get_Result")),
            "worker results are consumed only after the completion poll and never through a blocking wait");

        var workerDelegate = typeof(AuthoredDebrisAssets).GetProperty("PrepareOnWorker", BindingFlags.Static | BindingFlags.NonPublic);
        var workerSignature = workerDelegate!.PropertyType.GetMethod("Invoke")!.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Check(workerSignature.SequenceEqual([typeof(AuthoredDebrisShape[]), typeof(string), typeof(CancellationToken)])
            && workerSignature.All(type => type != typeof(Vehicle) && type != typeof(Part) && type != typeof(MeshReference)),
            "worker input contains immutable debris DTOs and a path, with no live vehicle, part or mesh objects");
        bool cancellationObserved = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { AuthoredDebrisAssets.Build(shape, cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
        }
        Check(cancellationObserved, "pure XML preparation observes cancellation before building detached assets");
        bool candidateCancellationObserved = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { ForceVehicleBreakup.BuildSiblingCandidates([new(double3.Zero, 1), new(new(.5, 0, 0), 1)], cancelled.Token); }
            catch (OperationCanceledException) { candidateCancellationObserved = true; }
        }
        Check(candidateCancellationObserved, "worker sibling-candidate generation observes cancellation");
        try { AuthoredDebrisAssets.Build(shape with { Extents = double3.Zero }); }
        catch (InvalidDataException) { Console.WriteLine("PASS: invalid debris dimensions refuse preparation"); return; }
        throw new InvalidOperationException("Invalid debris dimensions were accepted.");
    }

    private static void CheckPlanner()
    {
        Check(ForceVehicleBreakup.DefaultMaximumPieces == 50,
            "per-source breakup defaults to a 50-piece ceiling");
        Check(ForceBreakupLimits.Clamp(int.MinValue) == ForceBreakupLimits.Minimum
            && ForceBreakupLimits.Clamp(int.MaxValue) == ForceBreakupLimits.Maximum,
            "configured breakup caps clamp to the two-piece and global 512-piece limits");

        int nextBranch = 0;
        int firstBranch = ForceVehicleBreakup.ResolveFirstLayerBranchIndex(true, -1, ref nextBranch);
        int siblingBranch = ForceVehicleBreakup.ResolveFirstLayerBranchIndex(true, -1, ref nextBranch);
        int nestedBranch = ForceVehicleBreakup.ResolveFirstLayerBranchIndex(false, firstBranch, ref nextBranch);
        Check(firstBranch != siblingBranch && nestedBranch == firstBranch && nextBranch == 2,
            "capture assigns sibling first-layer branches separately and keeps nested descendants in their branch");
        var nestedOnlyVisuals = new VisualNode(false,
            [new(false, [new(true, []), new(true, [])])]);
        Check(ForceVehicleBreakup.HasMultipleVisibleNodes<VisualNode>(nestedOnlyVisuals, node => node.Visible,
                node => node.Descendants.Length, (node, index) => node.Descendants[index]),
            "breakup eligibility finds multiple visuals below a single root's nested subparts");

        var manyNodes = new List<ForceBreakupSourceNode>
        {
            new(0, -1, true, Geometry(0, new(0, 0, 0), 1))
        };
        for (int i = 1; i <= 600; i++)
            manyNodes.Add(new(0, i, false, Geometry(i, new(i * .01, 0, 0), 1, glass: i % 3 == 0)));
        var grouped = ForceBreakupPlanner.Build(manyNodes, 50, 1000);
        var groupedNodes = grouped.SelectMany(piece => piece.Shape.Nodes!).ToArray();
        Check(grouped.Length is >= 2 and <= 50 && groupedNodes.Length == manyNodes.Count
            && groupedNodes.Select(node => node.SourceIndex).OrderBy(index => index).SequenceEqual(Enumerable.Range(0, manyNodes.Count)),
            "more than 512 raw visual nodes coarsen into at most 50 pieces without losing or duplicating nodes");
        Check(Math.Abs(grouped.Sum(piece => piece.Shape.MassKg) - 1000) < .001,
            "grouped debris conserves the source dry mass");
        var repeated = ForceBreakupPlanner.Build(manyNodes, 50, 1000);
        Check(grouped.Select(piece => AuthoredDebrisAssets.Build(piece.Shape).Id)
                .SequenceEqual(repeated.Select(piece => AuthoredDebrisAssets.Build(piece.Shape).Id)),
            "hierarchical chunk selection and durable identities are deterministic");

        var manyFullParts = Enumerable.Range(0, 70)
            .Select(index => new ForceBreakupSourceNode(index, -1, true,
                Geometry(index, new(index, 0, 0), 1))).ToArray();
        var coarsenedParts = ForceBreakupPlanner.Build(manyFullParts, 50, 700);
        var coarsenedNodes = coarsenedParts.SelectMany(piece => piece.Shape.Nodes!).ToArray();
        Check(coarsenedParts.Length == 50 && coarsenedNodes.Length == 70
            && coarsenedNodes.Select(node => node.SourceIndex).Distinct().Count() == 70,
            "more than 50 full parts keep every part in nearby whole-part clusters");
        var aboveDefaultParts = Enumerable.Range(0, 80)
            .Select(index => new ForceBreakupSourceNode(index, -1, true,
                Geometry(index, new(index, 0, 0), 1))).ToArray();
        var aboveDefault = ForceBreakupPlanner.Build(aboveDefaultParts, 75, 800);
        Check(aboveDefault.Length == 75
            && aboveDefault.SelectMany(piece => piece.Shape.Nodes!).Select(node => node.SourceIndex).Distinct().Count() == 80,
            "an explicit planner cap above 50 coarsens to 75 complete chunks");
        var extremeMassParts = Enumerable.Range(0, 50)
            .Select(index => new ForceBreakupSourceNode(index, -1, true,
                Geometry(index, new(index, 0, 0), index == 49 ? 1e-20 : 1))).ToArray();
        var extremeMassPlan = ForceBreakupPlanner.Build(extremeMassParts, 50, 1e12);
        Check(extremeMassPlan.Length == 50 && extremeMassPlan.All(piece => piece.Shape.MassKg > 0)
            && extremeMassPlan[0].Shape.MassKg > 1e10 && extremeMassPlan[^1].Shape.MassKg < .001
            && Math.Abs(extremeMassPlan.Sum(piece => (double)piece.Shape.MassKg) - 1e12) < 100_000,
            "largest-chunk residual preserves positive mass when many large chunks round above a tiny final share");
        var lowerClamp = ForceBreakupPlanner.Build(manyFullParts, 1, 700);
        Check(lowerClamp.Length == 2 && lowerClamp.SelectMany(piece => piece.Shape.Nodes!).Count() == 70,
            "the lower piece-budget clamp still produces two complete chunks");

        var smallParts = new ForceBreakupSourceNode[]
        {
            new(0, -1, true, Geometry(0, new(0, 0, 0), 10)),
            new(0, 101, false, Geometry(1, new(0, 2, 0), 2)),
            new(1, -1, true, Geometry(2, new(10, 0, 0), 10)),
            new(1, 102, false, Geometry(3, new(10, 2, 0), 2)),
        };
        var partFirst = ForceBreakupPlanner.Build(smallParts, 4, 200);
        Check(partFirst.Length == 4
            && partFirst.Any(piece => { var nodes = piece.Shape.Nodes!; return nodes.Any(node => node.SourceIndex == 0)
                && !nodes.Any(node => node.SourceIndex == 2); })
            && partFirst.Any(piece => { var nodes = piece.Shape.Nodes!; return nodes.Any(node => node.SourceIndex == 2)
                && !nodes.Any(node => node.SourceIndex == 0); }),
            "full-part chunks are established first, then substantial first-layer branches can peel off");

        var nested = new ForceBreakupSourceNode[]
        {
            new(0, -1, true, Geometry(10, new(0, 0, 0), 10)),
            new(0, 201, false, Geometry(11, new(1, 0, 0), 3)),
            new(0, 201, false, Geometry(12, new(2, 0, 0), 3)),
            new(0, 202, false, Geometry(13, new(0, 2, 0), 3)),
        };
        var nestedPlan = ForceBreakupPlanner.Build(nested, 4, 100);
        Check(nestedPlan.Any(piece => { var nodes = piece.Shape.Nodes!; return nodes.Any(node => node.SourceIndex == 11)
                && nodes.Any(node => node.SourceIndex == 12); }),
            "nested subpart descendants stay together when a first-layer branch is peeled");

        var internalFurniture = new ForceBreakupSourceNode[]
        {
            new(0, -1, true, Geometry(20, new(0, 0, 0), 10)),
            new(0, -1, true, Geometry(21, new(0, 1, 0), 10, attachedInternal: true)),
            new(0, 203, false, Geometry(22, new(0, 2, 0), 10)),
        };
        var internalPlan = ForceBreakupPlanner.Build(internalFurniture, 3, 100);
        Check(internalPlan.Length == 2 && internalPlan.Any(piece => { var nodes = piece.Shape.Nodes!; return nodes.Any(node => node.SourceIndex == 20)
                && nodes.Any(node => node.SourceIndex == 21); }),
            "attached internal furnishings stay with their structural parent when refining a chunk");

        var uncomplicated = ForceBreakupPlanner.Build(manyFullParts.Take(2).ToArray(), 50, 100);
        Check(uncomplicated.Length == 2,
            "simple sources keep their natural piece count instead of filling the available quota");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool planningCancelled = false;
        try { ForceBreakupPlanner.Build(manyNodes, 50, 1000, cancelled.Token); }
        catch (OperationCanceledException) { planningCancelled = true; }
        Check(planningCancelled, "detached grouping observes cancellation before producing replacement pieces");
    }

    private static void CheckCompoundPlacement()
    {
        var surfaces = new[]
        {
            new AuthoredDebrisSurface("native_hull", "native_paint", false, true),
            new AuthoredDebrisSurface("native_window", "native_glass", true),
        };
        var nodes = new[]
        {
            new AuthoredDebrisNode(0, [surfaces[0]], new(4, 5, 6), doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2),
                new(2, 2, 2), new(4, 5, 6), new(2, 2, 2), 8),
            new AuthoredDebrisNode(1, [surfaces[1]], new(-3, 2, 1), doubleQuat.CreateFromAxisAngle(double3.UnitY, -.4),
                new(.5, .5, .5), new(-3, 2, 1), new(1, 2, 3), 6),
        };
        var shape = new AuthoredDebrisShape(surfaces, new(1, -2, 3), new(14, 12, 10), 1, 42) { Nodes = nodes };
        var (id, xml) = AuthoredDebrisAssets.Build(shape);
        Check(AuthoredDebrisAssets.Build(shape with { Nodes = [nodes[0] with { Position = new(4, 6, 6) }, nodes[1]] }).Id != id
            && AuthoredDebrisAssets.Build(shape with { Nodes = [nodes[0] with { Rotation = doubleQuat.Identity }, nodes[1]] }).Id != id
            && AuthoredDebrisAssets.Build(shape with { Nodes = [nodes[0] with { Scale = new(3, 3, 3) }, nodes[1]] }).Id != id,
            "compound asset identity includes every constituent position, rotation and scale");

        var bundle = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), xml, id, surfaces.Length);
        var root = bundle.Assets.OfType<PartTemplate>().Single(part => part.Id == id);
        foreach (var instance in root.SubPartInstances) instance.Transform!.OnDataLoad(null!);
        var visualTemplates = bundle.Assets.OfType<PartTemplate>().Where(part => part.Id != id).ToDictionary(part => part.Id);
        Check(root.IsHidden && visualTemplates.Values.All(part => part.IsHidden),
            "compound Force debris hides every generated root and visual node from the editor");
        Check(root.SubPartInstances.Count == 2 && visualTemplates.Count == 2
            && visualTemplates.Values.Sum(visual => visual.Components.OfType<MeshViewModule.Template>().Count()) == 2
            && visualTemplates.Values.SelectMany(visual => visual.Components).OfType<PartModelGlassModule.Template>().Count() == 1,
            "compound XML creates separately placed pickable opaque and glass visuals");
        bool placementPreserved = true;
        for (int i = 0; i < nodes.Length; i++)
        {
            var transform = root.SubPartInstances[i].Transform!;
            var expectedPosition = nodes[i].Position - shape.Center;
            var expectedX = double3.Transform(double3.UnitX, nodes[i].Rotation);
            var expectedY = double3.Transform(double3.UnitY, nodes[i].Rotation);
            var expectedZ = double3.Transform(double3.UnitZ, nodes[i].Rotation);
            var actualX = double3.Transform(double3.UnitX, transform.RotationValue);
            var actualY = double3.Transform(double3.UnitY, transform.RotationValue);
            var actualZ = double3.Transform(double3.UnitZ, transform.RotationValue);
            placementPreserved &= Near(transform.PositionValue, expectedPosition)
                && Near(transform.ScaleValue, nodes[i].Scale) && Near(expectedX, actualX)
                && Near(expectedY, actualY) && Near(expectedZ, actualZ);
        }
        var hullTemplate = visualTemplates.Values.Single(visual => visual.Components.OfType<PartModelModule.Template>().Any());
        Check(placementPreserved && hullTemplate.Components.OfType<PartModelModule.Template>().Single().Internal,
            "rotated and scaled source-local transforms and internal model state survive native XML loading");

        static bool Near(double3 actual, double3 expected) => (actual - expected).Length() < 1e-9;
    }

    private static AuthoredDebrisNode Geometry(int index, double3 position, double volume, bool glass = false, bool attachedInternal = false)
    {
        double side = Math.Cbrt(volume);
        var dimensions = new double3(side);
        return new(index, [new("native_mesh_" + index, "native_material_" + index, glass)], position,
            doubleQuat.Identity, double3.One, position, dimensions, volume, attachedInternal);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
