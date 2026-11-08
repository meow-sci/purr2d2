using System.Reflection;
using System.Xml.Serialization;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;

/// <summary>Validate the exact native facilities that own dropped-hilt gravity, collision and frames.</summary>
internal static class SaberDropHostChecks
{
    internal static void Run()
    {
        ReleaseSpinFrames();
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
        components.XmlArrayItems.Add(new XmlArrayItemAttribute("Collider", typeof(ColliderModule.Template)));
        overrides.Add(typeof(PartTemplate), nameof(PartTemplate.Components), components);
        var serializer = new XmlSerializer(typeof(AssetBundle), overrides);
        using var stream = File.OpenRead("Assets/purr2d2.xml");
        var assets = (AssetBundle)serializer.Deserialize(stream)!;
        var hilt = assets.Assets.OfType<PartTemplate>().Single(p => p.Id == "Purr2D2_DroppedHilt");
        // Resolve the authored tag through the native tag definition, without
        // starting the process-wide content library in this managed host check.
        foreach (var tag in hilt.EditorTagRefs)
        {
            tag.Value = new EditorTagDefinition { Id = tag.Ref };
            hilt.EditorTags.Add(tag.Value.ToTag());
        }
        Check(hilt.IsHidden && !hilt.IsSubPart && hilt.InertMasses.Count == 1
            && hilt.Components.Single() is ColliderModule.Template collider
            && collider.Colliders.Single() is BoxColliderTemplate,
            "the shipped hilt proxy loads hidden native mass and collision without duplicate render geometry");
        Check(!hilt.Components.Any(c => c is Control.Template or Battery.Template)
            && hilt.RocketCores.Count == 0 && hilt.RocketEngineControllers.Count == 0
            && hilt.RocketThrusterControllers.Count == 0,
            "the dropped hilt has no control, engine, power or thrust modules");
    }
    private static void ReleaseSpinFrames()
    {
        // Only the parent angular-rate query is substituted; conversion and
        // read/write operations below execute the actual game's PhysicsStates.
        var parent = DispatchProxy.Create<IParentBody, SpinParent>();
        var planetRate = new double3(0,0,.0007);
        ((SpinParent)(object)parent).Rate = planetRate;
        var hiltToPhysics = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX,.7),
            doubleQuat.CreateFromAxisAngle(double3.UnitY,-.4));
        var baseline = new double3(1.25,-2.5,.625);
        var inheritedVelocity = new double3(1000,-2000,3000);
        foreach (var frame in new[] { BubbleFrame.Ccf, BubbleFrame.Cci })
        foreach (float multiplier in new[] { 0f,1f,2f })
        {
            var origin = BubbleOrigin.CreateZero(parent,new UniverseTime(0)); origin.BubFrame = frame;
            var kinematic = new KinematicStates
            {
                PositionPhys = double3.Zero, Body2Phys = hiltToPhysics,
                VelocityPhys = inheritedVelocity, AngularVelocityPhys = baseline
            };
            var properties = default(VehicleProperties);
            var environment = new PhysicsEnvironment { ClosestParent = parent };
            var state = new PhysicsStates
            {
                Origin = ref origin, Kinematic = ref kinematic, Props = ref properties, Environment = ref environment
            };
            var physicalRate = SaberDropMath.ReleaseAngularVelocity(baseline,multiplier);
            var bodyRate = PhysicsStates.GetBodyRates(frame,physicalRate,hiltToPhysics,parent);
            state.SetBodyRates(bodyRate);
            Check((kinematic.AngularVelocityPhys - physicalRate).Length() < 1e-12
                && (state.GetBodyRates() - bodyRate).Length() < 1e-12,
                $"native {frame} conversion preserves consistent body and physics release rates at {multiplier}x spin");
            Check(kinematic.VelocityPhys == inheritedVelocity,
                $"native {frame} spin changes retain inherited linear motion at {multiplier}x");
            if (multiplier == 0)
                Check(frame == BubbleFrame.Cci ? bodyRate == double3.Zero
                    : (double3.Transform(bodyRate,hiltToPhysics) - planetRate).Length() < 1e-12,
                    $"zero initial spin in {frame} preserves only the native reference-frame rotation in its body-rate cache");
            if (multiplier == 1)
                Check(physicalRate == baseline, $"one-times spin preserves the exact preexisting {frame} release vector");
            if (multiplier == 2)
                Check(physicalRate == baseline * 2, $"two-times spin doubles the preexisting {frame} release vector");
        }
    }

    private class SpinParent : DispatchProxy
    {
        public SpinParent() { }
        internal double3 Rate;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IParentBody.GetAngularVelocityCci) => Rate,
            nameof(IParentBody.GetAngularVelocity) => Rate.Z,
            _ => throw new InvalidOperationException("Unexpected native parent query: " + targetMethod?.Name)
        };
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Drop host check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
