using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using BepuPhysics;
using BepuPhysics.Collidables;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace Purr2D2.Force;

/// <summary>Checked access to native clutter ownership. No replacement shapes, placement system or simulation.</summary>
internal static partial class ClutterForceNative
{
    internal delegate bool DisplaceDelegate(BubbleClutterStatics native, ConstraintSim sim,
        in BubbleClutterStatics.ClutterInstanceKey key, float mass, out GroundClutterPlacementData.DisplacedObject displaced);

    private sealed class Epoch { internal int Value; }
    private static readonly ConditionalWeakTable<BubbleClutterStatics, Epoch> Epochs = new();
    private static readonly AccessTools.FieldRef<Vehicle, VehicleUpdateState> Worker =
        AccessTools.FieldRefAccess<Vehicle, VehicleUpdateState>(RequireField(typeof(Vehicle), "_threadWorkerUpdateState", typeof(VehicleUpdateState)));
    private static readonly FieldInfo StaticsField = RequireDictionary("_statics", typeof(StaticHandle), "ClutterStaticInfo");
    private static readonly FieldInfo DynamicsField = RequireDictionary("_dynamicBodies", typeof(BodyHandle), "ClutterDynamicInstance");
    private static readonly AccessTools.FieldRef<BubbleClutterStatics, Dictionary<BubbleClutterStatics.ClutterInstanceKey, BodyHandle>> DisplacedHandles =
        AccessTools.FieldRefAccess<BubbleClutterStatics, Dictionary<BubbleClutterStatics.ClutterInstanceKey, BodyHandle>>(
            RequireField(typeof(BubbleClutterStatics), "_displacedBodyHandles", typeof(Dictionary<BubbleClutterStatics.ClutterInstanceKey, BodyHandle>)));
    private static readonly AccessTools.FieldRef<BubbleClutterStatics, List<BubbleClutterStatics.ClutterRemoval>> Exclusions =
        AccessTools.FieldRefAccess<BubbleClutterStatics, List<BubbleClutterStatics.ClutterRemoval>>(
            RequireField(typeof(BubbleClutterStatics), "_pendingExclusions", typeof(List<BubbleClutterStatics.ClutterRemoval>)));
    private static readonly AccessTools.FieldRef<BubbleClutterStatics, double3> DescribedOrigin =
        AccessTools.FieldRefAccess<BubbleClutterStatics, double3>(RequireField(typeof(BubbleClutterStatics), "_describedOriginPositionBub", typeof(double3)));
    private static readonly AccessTools.FieldRef<BubbleClutterStatics, Celestial> DescribedCelestial =
        AccessTools.FieldRefAccess<BubbleClutterStatics, Celestial>(RequireField(typeof(BubbleClutterStatics), "_describedCelestial", typeof(Celestial)));
    private static readonly AccessTools.FieldRef<BubbleClutterStatics, ClutterEcotypePhysicalData[]> Ecotypes =
        AccessTools.FieldRefAccess<BubbleClutterStatics, ClutterEcotypePhysicalData[]>(RequireField(typeof(BubbleClutterStatics), "_describedEcotypes", typeof(ClutterEcotypePhysicalData[])));
    private static readonly Func<object, BubbleClutterStatics.ClutterInstanceKey> StaticKey = ReadField<BubbleClutterStatics.ClutterInstanceKey>(StaticsField.FieldType.GetGenericArguments()[1], "Key");
    private static readonly Func<object, float> StaticMass = ReadField<float>(StaticsField.FieldType.GetGenericArguments()[1], "Mass");
    private static readonly Func<object, BubbleClutterStatics.ClutterInstanceKey> DynamicKey = ReadField<BubbleClutterStatics.ClutterInstanceKey>(DynamicsField.FieldType.GetGenericArguments()[1], "Key");
    private static readonly Func<object, TypedIndex> DynamicShape = ReadField<TypedIndex>(DynamicsField.FieldType.GetGenericArguments()[1], "Shape");
    private static readonly Type CellType = typeof(BubbleClutterStatics).GetNestedType("CellKey", BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Native clutter cell metadata changed.");
    private static readonly Type InstanceType = typeof(BubbleClutterStatics).GetNestedType("ClutterPhysicalInstance", BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Native clutter instance metadata changed.");
    private static readonly FieldInfo InstancesField = RequireField(typeof(BubbleClutterStatics), "_instanceData",
        typeof(Dictionary<,>).MakeGenericType(CellType, typeof(List<>).MakeGenericType(InstanceType)));
    private static readonly Func<BubbleClutterStatics.ClutterInstanceKey, object> CellFor = CreateCellKey();
    private static readonly Func<object, StaticHandle> InstanceHandle = ReadField<StaticHandle>(InstanceType, "Handle");
    private static readonly Func<object, TypedIndex> InstanceShape = ReadField<TypedIndex>(InstanceType, "Shape");
    private static readonly Func<object, uint> InstanceObject = ReadField<uint>(InstanceType, "ObjectId");
    private static readonly Func<object, uint> InstanceScale = ReadField<uint>(InstanceType, "ScaleId");
    private static readonly Func<object, uint> InstanceColor = ReadField<uint>(InstanceType, "PackedColor");
    private static readonly Func<object, uint> InstanceSubCell = ReadField<uint>(InstanceType, "SubCellId");
    private static readonly DisplaceDelegate Displace = CreateDisplace();

    static ClutterForceNative() { }

    internal static readonly MethodInfo ClearMethod = AccessTools.Method(typeof(BubbleClutterStatics), nameof(BubbleClutterStatics.Clear), [typeof(ConstraintSim)])
        ?? throw new MissingMethodException("Native clutter clear hook changed.");

    internal static int Generation(BubbleClutterStatics native) => Volatile.Read(ref Epochs.GetValue(native, static _ => new Epoch()).Value);
    internal static void Invalidate(BubbleClutterStatics native)
    {
        Interlocked.Increment(ref Epochs.GetValue(native, static _ => new Epoch()).Value);
        ClutterForceHold.NativeClearing(native);
    }

    internal static PhysicsBubble? OwnerBubble(Vehicle owner) => Worker(owner)?.Bubble;
    internal static IDictionary Statics(BubbleClutterStatics native) => (IDictionary)StaticsField.GetValue(native)!;
    internal static IDictionary Dynamics(BubbleClutterStatics native) => (IDictionary)DynamicsField.GetValue(native)!;
    internal static double3 Origin(BubbleClutterStatics native) => DescribedOrigin(native);
    internal static bool Describes(BubbleClutterStatics native, Celestial parent) => ReferenceEquals(DescribedCelestial(native), parent);
    internal static bool TryStatic(BubbleClutterStatics native, StaticHandle handle,
        out BubbleClutterStatics.ClutterInstanceKey key, out float mass)
    {
        var value = Statics(native)[handle];
        key = value == null ? default : StaticKey(value); mass = value == null ? 0 : StaticMass(value);
        return value != null;
    }
    internal static bool TryDynamic(BubbleClutterStatics native, BodyHandle handle,
        out BubbleClutterStatics.ClutterInstanceKey key, out TypedIndex shape)
    {
        var value = Dynamics(native)[handle];
        key = value == null ? default : DynamicKey(value); shape = value == null ? default : DynamicShape(value);
        return value != null;
    }
    internal static bool TryBody(BubbleClutterStatics native, in BubbleClutterStatics.ClutterInstanceKey key, out BodyHandle handle) =>
        DisplacedHandles(native).TryGetValue(key, out handle);

    internal static bool Promote(ClutterForceTarget target, out BodyHandle handle, out string reason)
    {
        handle = default; reason = "The ground object changed before capture.";
        if (!target.TryResolve(out var pose, out _, out var staticHandle, out var bodyHandle)) return false;
        if (bodyHandle.HasValue) { handle = bodyHandle.Value; return true; }
        if (!staticHandle.HasValue || !TryStatic(target.Native, staticHandle.Value, out var key, out var mass)
            || !key.Equals(target.Key) || !float.IsFinite(mass) || mass <= 0) return false;
        // Current KSA consolidates each object's primitive colliders into one native shape. Verify the
        // shape/placement prerequisites before its displacement routine makes its first mutation.
        if (!ValidatePromotionShape(target, pose, out var fallback)) { reason = "This clutter collider layout is unsupported."; return false; }
        GroundClutterPlacementData.DisplacedObject displaced;
        bool displacedOk;
        try { displacedOk = Displace(target.Native, target.Sim, in key, mass, out displaced); }
        catch
        {
            // Finish a completed native ownership transfer even if later placement reconstruction
            // threw. The resulting unheld dynamic body retains its native inertia and collision rules.
            if (target.TryResolveBody(out _))
                Exclusions(target.Native).Add(new BubbleClutterStatics.ClutterRemoval(key, in fallback));
            // An allocation failure inside Bepu before the native maps publish ownership is beyond
            // this bridge's recovery contract. Never infer a body from a recycled/awakened handle.
            throw;
        }
        if (!displacedOk)
        {
            if (!target.TryResolveBody(out _))
            { reason = "KSA could not displace the selected ground object."; return false; }
            // A native false return can occur after transfer if its placement reconstruction failed.
            // The preflight captured that exact placement before mutation, so preserve native ownership.
            displaced = fallback;
        }
        // This is the native transaction's other half: SyncGroundClutter excludes the original render
        // instance, registers its displaced rendering placement and keeps its body pose synchronized.
        if (!target.TryResolveBody(out var promoted))
            throw new InvalidOperationException("Native clutter displacement did not retain its identified body.");
        handle = promoted.Handle;
        Exclusions(target.Native).Add(new BubbleClutterStatics.ClutterRemoval(key, in displaced));
        return true;
    }

    private static bool ValidatePromotionShape(ClutterForceTarget target, RigidPose pose,
        out GroundClutterPlacementData.DisplacedObject placement)
    {
        placement = default;
        var data = Ecotypes(target.Native);
        if (data == null || (uint)target.Key.EcotypeIndex >= (uint)data.Length) return false;
        var instances = (IDictionary)InstancesField.GetValue(target.Native)!;
        if (instances[CellFor(target.Key)] is not IList list || list.Count > ClutterForceTargets.MaximumCandidates) return false;
        // Native promotion loops by SubCellId. Multiple rows would violate the one-body transaction
        // contract, even if the ecotype's current collider list is consolidated.
        int matchingRows = 0;
        foreach (var item in list) if (InstanceSubCell(item!) == target.Key.SubCellId) matchingRows++;
        if (matchingRows != 1) return false;
        // Search only the identified residency cell, using checked cached field readers. A shared
        // shape does not identify an object or scale, so its precise metadata comes from this row.
        for (int index = 0; index < list.Count; index++)
        {
            var instance = list[index]!;
            if (!target.MatchesStatic(InstanceHandle(instance))) continue;
            if (InstanceSubCell(instance) != target.Key.SubCellId) return false;
            uint objectId = InstanceObject(instance!), scaleId = InstanceScale(instance!);
            var colliders = data[target.Key.EcotypeIndex].GetColliders(objectId, scaleId);
            if (colliders.Length != 1 || colliders[0].Handle != target.Shape || InstanceShape(instance!) != target.Shape) return false;
            var colliderRotation = DoubleQuatEx.FromBepu(pose.Orientation);
            var rotation = doubleQuat.Concatenate(colliders[0].LocalRotation.Inverse(), colliderRotation);
            var position = Origin(target.Native) + Double3Ex.FromBepu(pose.Position) - colliders[0].LocalPosition.Transform(rotation);
            if (!ForceMath.Finite(position) || !ForceMath.Finite(rotation)) return false;
            placement = new GroundClutterPlacementData.DisplacedObject
            {
                ObjectId = objectId, ScaleId = scaleId, PackedColor = InstanceColor(instance!),
                PositionCcf = position, RotationCcf = rotation
            };
            return true;
        }
        return false;
    }

    private static Func<BubbleClutterStatics.ClutterInstanceKey, object> CreateCellKey()
    {
        var constructor = CellType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [typeof(int), typeof(CubeCellGrid.Cell)], null)
            ?? throw new MissingMethodException("Native clutter cell constructor changed.");
        var method = new DynamicMethod("Purr2D2_ClutterCell", typeof(object),
            [typeof(BubbleClutterStatics.ClutterInstanceKey)], typeof(ClutterForceNative).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarga_S, (byte)0); il.Emit(OpCodes.Ldfld, RequireField(typeof(BubbleClutterStatics.ClutterInstanceKey), "EcotypeIndex", typeof(int)));
        il.Emit(OpCodes.Ldarga_S, (byte)0); il.Emit(OpCodes.Ldfld, RequireField(typeof(BubbleClutterStatics.ClutterInstanceKey), "Cell", typeof(CubeCellGrid.Cell)));
        il.Emit(OpCodes.Newobj, constructor); il.Emit(OpCodes.Box, CellType); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<BubbleClutterStatics.ClutterInstanceKey, object>>();
    }

    internal static string Name(BubbleClutterStatics native, in BubbleClutterStatics.ClutterInstanceKey key)
    {
        var data = Ecotypes(native);
        return data != null && (uint)key.EcotypeIndex < (uint)data.Length
            ? data[key.EcotypeIndex].EcotypeReference.Name + " ground object" : "Ground object";
    }

    private static DisplaceDelegate CreateDisplace()
    {
        var pending = typeof(BubbleClutterStatics).GetNestedType("PendingHit", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Native clutter pending-hit type changed.");
        var method = typeof(BubbleClutterStatics).GetMethod("TryDisplaceInstance", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ConstraintSim), typeof(BubbleClutterStatics.ClutterInstanceKey).MakeByRefType(), typeof(float), pending.MakeByRefType(), typeof(GroundClutterPlacementData.DisplacedObject).MakeByRefType()], null);
        if (method == null || method.ReturnType != typeof(bool)) throw new MissingMethodException("Native clutter displacement signature changed.");
        var bridge = new DynamicMethod("Purr2D2_DisplaceClutter", typeof(bool),
            [typeof(BubbleClutterStatics), typeof(ConstraintSim), typeof(BubbleClutterStatics.ClutterInstanceKey).MakeByRefType(), typeof(float), typeof(GroundClutterPlacementData.DisplacedObject).MakeByRefType()], typeof(ClutterForceNative).Module, true);
        var il = bridge.GetILGenerator();
        var hit = il.DeclareLocal(pending);
        il.Emit(OpCodes.Ldloca, hit); il.Emit(OpCodes.Initobj, pending);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloca, hit); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        // The zeroed hit has zero separation velocity: no energy threshold trick and no reaction body.
        return bridge.CreateDelegate<DisplaceDelegate>();
    }

    private static FieldInfo RequireDictionary(string name, Type key, string valueName)
    {
        var value = typeof(BubbleClutterStatics).GetNestedType(valueName, BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Native clutter metadata changed: " + valueName);
        return RequireField(typeof(BubbleClutterStatics), name, typeof(Dictionary<,>).MakeGenericType(key, value));
    }
    private static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        var field = AccessTools.Field(type, name);
        if (field?.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static Func<object, T> ReadField<T>(Type owner, string name)
    {
        var field = RequireField(owner, name, typeof(T));
        var method = new DynamicMethod("Purr2D2_Clutter_" + name, typeof(T), [typeof(object)], typeof(ClutterForceNative).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, owner); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, T>>();
    }
}
