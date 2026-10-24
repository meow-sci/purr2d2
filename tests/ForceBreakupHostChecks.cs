using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Force;
using Purr2D2.Rendering;

/// <summary>Use the real KSA serializer/types and native call contracts, without starting the game.</summary>
internal static class ForceBreakupHostChecks
{
    internal static void Run()
    {
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
        try { AuthoredDebrisAssets.Build(shape with { Extents = double3.Zero }); }
        catch (InvalidDataException) { Console.WriteLine("PASS: invalid debris dimensions refuse preparation"); return; }
        throw new InvalidOperationException("Invalid debris dimensions were accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
