using System.Reflection;
using System.Reflection.Emit;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Force;
using Purr2D2.Runtime;

/// <summary>Inspect real game IL without constructing a game, scene or native physics object.</summary>
internal static class ForceHostChecks
{
    public static void Main()
    {
        Run();
        ClutterHostContracts();
        NativeBridgeDelegates();
        ForceClutterBepuChecks.Run();
        Console.WriteLine("Force host checks passed (actual KSA handoff/patch composition and native clutter promotion, identity, frames and contact contracts).");
    }

    private static void ClutterHostContracts()
    {
        Type clutter = typeof(BubbleClutterStatics);
        Type key = typeof(BubbleClutterStatics.ClutterInstanceKey);
        Type physical = Nested("ClutterPhysicalInstance"), dynamic = Nested("ClutterDynamicInstance");
        Type staticInfo = Nested("ClutterStaticInfo"), hit = Nested("PendingHit"), cell = Nested("CellKey");
        Field(clutter, "_instanceData", typeof(Dictionary<,>).MakeGenericType(cell, typeof(List<>).MakeGenericType(physical)));
        Field(clutter, "_statics", typeof(Dictionary<,>).MakeGenericType(typeof(StaticHandle), staticInfo));
        Field(clutter, "_dynamicBodies", typeof(Dictionary<,>).MakeGenericType(typeof(BodyHandle), dynamic));
        Field(clutter, "_displacedBodyHandles", typeof(Dictionary<,>).MakeGenericType(key, typeof(BodyHandle)));
        Field(clutter, "_describedCelestial", typeof(Celestial));
        Field(clutter, "_describedOriginPositionBub", typeof(double3));
        Field(physical, "Handle", typeof(StaticHandle)); Field(physical, "Shape", typeof(TypedIndex));
        Field(physical, "UnitInertia", typeof(BodyInertia)); Field(physical, "SubCellId", typeof(uint));
        Field(physical, "PositionCcf", typeof(double3)); Field(physical, "RotationCcf", typeof(doubleQuat));
        Field(dynamic, "Shape", typeof(TypedIndex)); Field(dynamic, "Key", key);
        Field(staticInfo, "Key", key); Field(staticInfo, "Mass", typeof(float));
        foreach (string name in new[] { "ObjectId", "ScaleId" })
        { Field(physical, name, typeof(uint)); Field(dynamic, name, typeof(uint)); }
        Check(key.GetFields().Select(f => f.Name).Order().SequenceEqual(
            new[] { "CelestialHash", "Cell", "EcotypeIndex", "SubCellId" }.Order()),
            "the complete native instance key includes celestial, ecotype, cell and subcell identity");

        MethodInfo promote = Require(clutter, "TryDisplaceInstance", typeof(ConstraintSim), key.MakeByRefType(),
            typeof(float), hit.MakeByRefType(), typeof(GroundClutterPlacementData.DisplacedObject).MakeByRefType());
        Check(!promote.IsPublic && !promote.IsStatic && promote.ReturnType == typeof(bool)
            && promote.GetParameters()[4].IsOut,
            "native static promotion has the checked private signature and out displacement result");
        var promotion = PatchProcessor.GetOriginalInstructions(promote);
        Check(promotion.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(BodyDescription) && m.Name == "CreateDynamic")
            && promotion.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Bodies) && m.Name == "Add")
            && promotion.Any(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Statics) && m.Name == "Remove"),
            "the checked host promotes stock statics into actual Bepu dynamic bodies");
        Check(promotion.Any(c => c.operand is FieldInfo f && f.Name == "_dynamicBodies")
            && promotion.Any(c => c.operand is FieldInfo f && f.Name == "_displacedBodyHandles")
            && promotion.Any(c => c.operand is FieldInfo f && f.Name == "_describedOriginPositionBub"),
            "promotion updates both native identity maps and uses the described bubble origin");
        Require(typeof(BodyReference), nameof(BodyReference.BecomeKinematic));
        Require(typeof(BodyReference), nameof(BodyReference.SetLocalInertia), typeof(BodyInertia).MakeByRefType());
        Require(typeof(BodyReference), nameof(BodyReference.SetShape), typeof(TypedIndex));
        Field(typeof(PhysicsBubble), nameof(PhysicsBubble.Origin), typeof(BubbleOrigin));
        Field(typeof(PhysicsBubble), nameof(PhysicsBubble.ConstraintSim), typeof(ConstraintSim));
        Field(typeof(PhysicsBubble), nameof(PhysicsBubble.GroundClutterStatics), clutter);
        Check(typeof(BubbleOrigin).GetMethod(nameof(BubbleOrigin.GetBub2Cci))?.ReturnType == typeof(doubleQuat)
            && typeof(BubbleOrigin).GetMethod(nameof(BubbleOrigin.GetStatesCci)) is { } frame
            && frame.GetParameters().All(p => p.IsOut && p.ParameterType == typeof(double3).MakeByRefType()),
            "native bubble origin supplies both translated velocity and rotation for a CCI hold");

        Type callbacks = typeof(Universe).Assembly.GetType("KSA.NarrowPhaseCallbacks", throwOnError: true)!;
        MethodInfo contacts = Require(callbacks, "AllowContactGeneration", typeof(int), typeof(CollidableReference),
            typeof(CollidableReference), typeof(float).MakeByRefType());
        var contactIl = PatchProcessor.GetOriginalInstructions(contacts);
        Check(contacts.ReturnType == typeof(bool) && contactIl.Any(c => c.Calls(Require(clutter, nameof(BubbleClutterStatics.IsClutterBody), typeof(BodyHandle))))
            && contactIl.Any(c => c.Calls(Require(clutter, nameof(BubbleClutterStatics.IsClutterStatic), typeof(StaticHandle)))),
            "the contact hook targets the exact native pair overload that recognizes clutter bodies and statics");
        MethodInfo execute = Require(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), typeof(double), typeof(SimStep));
        var dispatch = PatchProcessor.GetOriginalInstructions(execute);
        int prepare = dispatch.FindIndex(c => c.Calls(Require(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), typeof(SimStep))));
        int sync = dispatch.FindIndex(c => c.Calls(Require(typeof(Universe), "SyncGroundClutter")));
        Check(prepare >= 0 && sync > prepare && dispatch.FindIndex(sync + 1,
            c => c.operand is MethodInfo m && m.Name == "Queue") > sync,
            "the existing settled handoff precedes native clutter synchronization and the next physics dispatch");

        Type Nested(string name) => clutter.GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new MissingMemberException(clutter.FullName, name);
        static void Field(Type owner, string name, Type type)
        {
            FieldInfo? field = AccessTools.Field(owner, name);
            Check(field?.FieldType == type, "native field contract: " + owner.Name + "." + name);
        }
    }

    private static void NativeBridgeDelegates()
    {
        var native = new BubbleClutterStatics();
        Check(ClutterForceNative.Generation(native) == 0,
            "production native bridge initializes its checked private fields and emitted delegates against actual KSA");
        var key = new BubbleClutterStatics.ClutterInstanceKey(default, 3, default, 7);
        Type bridge = typeof(ClutterForceNative), nativeType = typeof(BubbleClutterStatics);
        var cellFor = (Func<BubbleClutterStatics.ClutterInstanceKey, object>)AccessTools.Field(bridge, "CellFor").GetValue(null)!;
        object cell = cellFor(key);
        Check((int)AccessTools.Field(cell.GetType(), "EcotypeIndex").GetValue(cell)! == 3
            && Equals(AccessTools.Field(cell.GetType(), "Cell").GetValue(cell), key.Cell),
            "production emitted cell-key delegate constructs the actual private key with exact fields");
        Type staticInfo = nativeType.GetNestedType("ClutterStaticInfo", BindingFlags.NonPublic)!;
        object metadata = Activator.CreateInstance(staticInfo, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [key, 5f], culture: null)!;
        var staticHandle = new StaticHandle(17);
        ClutterForceNative.Statics(native).Add(staticHandle, metadata);
        Check(ClutterForceNative.TryStatic(native, staticHandle, out var observedKey, out var mass)
            && observedKey.Equals(key) && mass == 5,
            "production emitted private struct field readers recover actual static identity and mass");
        var bodyHandle = new BodyHandle(23); var shape = new TypedIndex(Sphere.TypeId, 42);
        Type dynamicInfo = nativeType.GetNestedType("ClutterDynamicInstance", BindingFlags.NonPublic)!;
        object dynamic = Activator.CreateInstance(dynamicInfo, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [shape, key, 2u, 4u, .25f], culture: null)!;
        ClutterForceNative.Dynamics(native).Add(bodyHandle, dynamic);
        var reverse = (Dictionary<BubbleClutterStatics.ClutterInstanceKey, BodyHandle>)AccessTools.Field(nativeType,
            "_displacedBodyHandles").GetValue(native)!;
        reverse.Add(key, bodyHandle);
        Check(ClutterForceNative.TryDynamic(native, bodyHandle, out var dynamicKey, out var dynamicShape)
            && dynamicKey.Equals(key) && dynamicShape == shape
            && ClutterForceNative.TryBody(native, in key, out var observedBody) && observedBody == bodyHandle,
            "production dynamic-field delegates and reverse lookup agree on the actual native body identity");
        Type physical = nativeType.GetNestedType("ClutterPhysicalInstance", BindingFlags.NonPublic)!;
        object row = Activator.CreateInstance(physical, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [staticHandle, shape, new double3(1, 2, 3), doubleQuat.Identity,
                7u, 2u, 4u, 0xABCDEFu, default(BodyInertia)], culture: null)!;
        foreach (string reader in new[] { "InstanceHandle", "InstanceShape", "InstanceObject", "InstanceScale", "InstanceColor", "InstanceSubCell" })
        {
            var accessor = (Delegate)AccessTools.Field(bridge, reader).GetValue(null)!;
            string field = reader switch { "InstanceHandle" => "Handle", "InstanceShape" => "Shape", "InstanceObject" => "ObjectId",
                "InstanceScale" => "ScaleId", "InstanceColor" => "PackedColor", _ => "SubCellId" };
            Check(Equals(accessor.DynamicInvoke(row), AccessTools.Field(physical, field).GetValue(row)),
                "production private physical-row accessor executes correctly: " + reader);
        }

        // Empty residency rejects before dereferencing a simulation. This invokes the real emitted
        // bridge and private game method, including its private zero-initialized PendingHit argument.
        var displace = (ClutterForceNative.DisplaceDelegate)AccessTools.Field(bridge, "Displace").GetValue(null)!;
        Check(!displace(native, null!, in key, 5, out _),
            "production displacement delegate invokes the actual private game method with a valid by-ref stack contract");
        ClutterForceNative.Invalidate(native);
        Check(ClutterForceNative.Generation(native) == 1,
            "native clear generation invalidates old identities without reading or recycling a Bepu handle");

        var harmony = new Harmony("purr2d2.force.host.clutter-install");
        try
        {
            ClutterForceTargets.Install(harmony);
            Type callbacks = typeof(Universe).Assembly.GetType("KSA.NarrowPhaseCallbacks", throwOnError: true)!;
            MethodInfo contact = Require(callbacks, "AllowContactGeneration", typeof(int), typeof(CollidableReference),
                typeof(CollidableReference), typeof(float).MakeByRefType());
            object? accepted = contact.Invoke(Activator.CreateInstance(callbacks), [0,
                new CollidableReference(CollidableMobility.Kinematic, new BodyHandle(1)),
                new CollidableReference(new StaticHandle(2)), 0f]);
            Check(accepted is false,
                "production Harmony contact prefix installs and injects the private readonly callback Sim field correctly");
            var empty = new BubbleClutterStatics();
            empty.Clear(null);
            Check(ClutterForceNative.Generation(empty) == 1,
                "production native Clear prefix invalidates generation when the actual game method runs");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    internal static void Run()
    {
        MethodInfo caller = Require(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), typeof(SimStep));
        MethodInfo take = Require(typeof(Vehicle), nameof(Vehicle.TakeUpdateStateIfOrphaned));
        MethodInfo native = Require(typeof(Vehicle), nameof(Vehicle.PrepareWorker), typeof(SimStep));
        MethodInfo wrapped = Require(typeof(SomersaultMotionController), nameof(SomersaultMotionController.PrepareWorkerAndHandoff),
            typeof(Vehicle), typeof(SimStep));
        MethodInfo filter = Require(typeof(ForcePhysicsPatches), nameof(ForcePhysicsPatches.FilterCapturedWorker), typeof(VehicleUpdateState));
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(caller);
        Check(caller.DeclaringType == typeof(Universe) && caller.Module.Assembly.GetName().Name == "KSA"
            && original.Count > 20 && original.Count(c => c.Calls(take)) == 1,
            "the fixture reads the actual KSA Universe method rather than a synthetic host seam");
        Check(!take.IsStatic && take.ReturnType == typeof(VehicleUpdateState) && take.GetParameters().Length == 0
            && filter.IsStatic && filter.ReturnType == take.ReturnType
            && filter.GetParameters().Select(p => p.ParameterType).SequenceEqual([take.ReturnType]),
            "the Force filter consumes exactly the native nullable worker reference and returns the same stack type");
        Check(wrapped.IsStatic && wrapped.ReturnType == native.ReturnType
            && wrapped.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                new[] { typeof(Vehicle) }.Concat(native.GetParameters().Select(p => p.ParameterType))),
            "the somersault wrapper preserves the native virtual receiver, arguments and return stack");

        List<CodeInstruction> forceOnly = ForcePhysicsPatches.FilterOrphanWorkers(original).ToList();
        Check(forceOnly.Count == original.Count + 1 && forceOnly.Count(c => c.Calls(filter)) == 1,
            "Force adds precisely one worker filter to the actual host method");
        List<CodeInstruction> forceFirst = SomersaultMotionController.InsertWorkerHandoff(forceOnly).ToList();
        List<CodeInstruction> motionFirst = ForcePhysicsPatches.FilterOrphanWorkers(
            SomersaultMotionController.InsertWorkerHandoff(original)).ToList();
        Check(forceFirst.Count == motionFirst.Count && forceFirst.Zip(motionFirst).All(pair => Same(pair.First, pair.Second)),
            "Force then somersault and somersault then Force produce identical actual-host IL");
        foreach (var transformed in new[] { forceFirst, motionFirst })
        {
            Check(transformed.Count == original.Count + 1 && transformed.Count(c => c.Calls(take)) == 1
                && transformed.Count(c => c.Calls(filter)) == 1 && transformed.Count(c => c.Calls(wrapped)) == 1
                && transformed.All(c => !c.Calls(native)),
                "composition retains one original getter, one native-preserving prepare wrapper and one filter");
            int nativeGetter = original.FindIndex(c => c.Calls(take));
            int emittedGetter = transformed.FindIndex(c => c.Calls(take));
            Check(emittedGetter == nativeGetter && transformed[emittedGetter + 1].opcode == OpCodes.Call
                && transformed[emittedGetter + 1].Calls(filter),
                "the filter runs immediately after the original getter and before its native null/adoption branch");
            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction before = original[i], after = transformed[i + (i > nativeGetter ? 1 : 0)];
                if (before.Calls(native))
                    Check(after.opcode == OpCodes.Call && after.Calls(wrapped), "only native preparation is replaced by the checked handoff");
                else Check(before.opcode == after.opcode && Equals(before.operand, after.operand),
                    "all remaining actual KSA instructions and operands survive composition");
                Check(before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
                    "all native branch labels and exception boundaries remain on their original instructions");
            }
        }

        Reject(codes => codes.RemoveAt(codes.FindIndex(c => c.Calls(take))), "a missing native orphan getter");
        Reject(codes => codes.Add(new CodeInstruction(OpCodes.Call, take)), "duplicate native orphan getters");
        Reject(codes =>
        {
            int index = codes.FindIndex(c => c.Calls(take));
            CodeInstruction getter = codes[index]; codes.RemoveAt(index); codes.Insert(0, getter);
        }, "a getter without its native receiver setup");
        Check(ForcePhysicsPatches.FilterCapturedWorker(null) is null,
            "the inserted filter preserves the native null result without touching game state");

        void Reject(Action<List<CodeInstruction>> mutate, string description)
        {
            foreach (bool afterMotion in new[] { false, true })
            {
                List<CodeInstruction> changed = afterMotion
                    ? SomersaultMotionController.InsertWorkerHandoff(original).ToList()
                    : original.Select(c => new CodeInstruction(c)).ToList();
                mutate(changed);
                try { ForcePhysicsPatches.FilterOrphanWorkers(changed).ToList(); }
                catch (InvalidOperationException)
                {
                    Console.WriteLine($"PASS: Force rejects {description} {(afterMotion ? "after" : "before")} somersault transformation");
                    continue;
                }
                throw new InvalidOperationException("Force host IL check accepted " + description + ".");
            }
        }
    }

    private static MethodInfo Require(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
    private static bool Same(CodeInstruction a, CodeInstruction b) => a.opcode == b.opcode && Equals(a.operand, b.operand)
        && a.labels.SequenceEqual(b.labels) && a.blocks.SequenceEqual(b.blocks);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Force host IL check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
