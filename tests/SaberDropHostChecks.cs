using System.Reflection;
using System.Xml.Serialization;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

/// <summary>Validate the exact native facilities that own dropped-hilt gravity, collision and frames.</summary>
internal static class SaberDropHostChecks
{
    internal static void Run()
    {
        var worker = typeof(Vehicle).GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic);
        var adopt = AccessTools.Method(typeof(Vehicle), "AdoptSeparatedVehicle", [typeof(Vehicle), typeof(PhysicsBubble), typeof(double3)]);
        Check(worker?.FieldType == typeof(VehicleUpdateState) && adopt is { IsStatic: false, ReturnType: { } result } && result == typeof(void),
            "the dropped-body native worker/adoption signatures match the checked game snapshot");
        _ = adopt!.CreateDelegate<Action<Vehicle, Vehicle, PhysicsBubble, double3>>();
        var adoption = PatchProcessor.GetOriginalInstructions(adopt);
        Check(adoption.Any(c => c.operand is MethodInfo m && m.Name == "AddToBubble" && m.DeclaringType == typeof(Vehicle))
            && adoption.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(CollisionAvoidancePair) && m.Name == "Link"),
            "native adoption owns bubble registration and temporary source-overlap protection");
        var add = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ConstraintSim), nameof(ConstraintSim.AddVehicle)));
        Check(add.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(BepuPhysics.Bodies) && m.Name == "Add")
            && add.Any(c => c.operand is FieldInfo f && f.Name == nameof(ConstraintSim.HandleToState)),
            "a released native vehicle is a real collision body registered with the gravity integrator");
        var snap = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(PhysicsBubble), "UpdateBepuStatesForSnappedOrigin"));
        Check(snap.Any(c => c.operand is MethodInfo m && m.Name == nameof(ConstraintSim.UpdateSimForSnappedOrigin)),
            "native dropped-body ownership participates in bubble-origin collider updates");

        var overrides = new XmlAttributeOverrides(); var components = new XmlAttributes();
        components.XmlElements.Add(new XmlElementAttribute("Collider", typeof(ColliderModule.Template)));
        overrides.Add(typeof(PartTemplate), nameof(PartTemplate.Components), components);
        var serializer = new XmlSerializer(typeof(AssetBundle), overrides);
        using var stream = File.OpenRead("Assets/purr2d2.xml");
        var assets = (AssetBundle)serializer.Deserialize(stream)!;
        var hilt = assets.Assets.OfType<PartTemplate>().Single(p => p.Id == "Purr2D2_DroppedHilt");
        // The serializer retains raw tag references; native OnDataLoad resolves these
        // through VehicleEditor before IsHidden becomes meaningful.
        foreach (var tag in hilt.EditorTagsStrings) hilt.EditorTags.Add(VehicleEditor.RegisterTag(tag));
        Check(hilt.IsHidden && !hilt.IsSubPart && hilt.InertMasses.Count == 1
            && hilt.Components.Single() is ColliderModule.Template collider
            && collider.Colliders.Single() is BoxColliderTemplate,
            "the shipped hilt proxy loads hidden native mass and collision without duplicate render geometry");
        Check(hilt.Control == null && hilt.RocketCores.Count == 0 && hilt.RocketEngineControllers.Count == 0
            && hilt.RocketThrusterControllers.Count == 0 && hilt.Batteries.Count == 0,
            "the dropped hilt has no control, engine, power or thrust modules");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Drop host check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
