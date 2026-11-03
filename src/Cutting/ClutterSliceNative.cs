using System.Collections;
using BepuPhysics;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal sealed record ClutterSliceDescription(ClutterForceTarget Target, ClutterEcotypePhysicalData Ecotype,
    ClutterObjectTemplate Object, uint ObjectId, uint ScaleId, uint PackedColor, double Scale, float MassKg, bool Tree);

internal readonly record struct ClutterSliceFrame(double3 PositionCci, doubleQuat Object2Cci,
    double3 VelocityCci, double3 OmegaCci);

/// <summary>Uses the native collider-to-object transform, never the collider centroid as the visual origin.</summary>
internal static partial class ClutterForceNative
{
    // Partial-file field initializer order is unspecified. Delay these readers
    // until the original native bridge has initialized its checked dictionary fields.
    private static readonly Lazy<Func<object, uint>> DynamicObject = new(() => ReadField<uint>(SliceDynamicMetadataType(), "ObjectId"));
    private static readonly Lazy<Func<object, uint>> DynamicScale = new(() => ReadField<uint>(SliceDynamicMetadataType(), "ScaleId"));
    private static Type SliceDynamicMetadataType() => (DynamicsField
        ?? throw new MissingFieldException("Native clutter dynamic metadata is unavailable.")).FieldType.GetGenericArguments()[1];
    internal static void ValidateSliceLayout() { _ = DynamicObject.Value; _ = DynamicScale.Value; }

    internal static bool TryDescribeSlice(ClutterForceTarget target, out ClutterSliceDescription? source)
    {
        source = null;
        if (!target.TryResolve(out var pose, out _, out var nativeStatic, out var nativeBody)) return false;
        var data = Ecotypes(target.Native);
        if (data == null || (uint)target.Key.EcotypeIndex >= data.Length) return false;
        var ecotype = data[target.Key.EcotypeIndex];
        uint objectId, scaleId, packedColor;
        if (nativeStatic.HasValue)
        {
            if (!ValidatePromotionShape(target, pose, out var placement)) return false;
            objectId = placement.ObjectId; scaleId = placement.ScaleId; packedColor = placement.PackedColor;
        }
        else if (nativeBody.HasValue && Dynamics(target.Native)[nativeBody.Value] is { } entry
            && ecotype.PlacementData.TryGetDisplacedObject(in target.Key, out var placement))
        {
            objectId = DynamicObject.Value(entry); scaleId = DynamicScale.Value(entry); packedColor = placement.PackedColor;
            if (objectId != placement.ObjectId || scaleId != placement.ScaleId) return false;
        }
        else return false;
        if (objectId >= ecotype.EcotypeReference.ClutterObjects.Count) return false;
        var obj = ecotype.EcotypeReference.ClutterObjects[(int)objectId].Get();
        double scale = ecotype.GetScale(scaleId);
        float mass = (float)ecotype.GetMass(objectId, scaleId);
        if (!double.IsFinite(scale) || scale <= 0 || !float.IsFinite(mass) || mass <= .001f || obj.Lods.Count == 0) return false;
        // Substances identify authored/modded trees too; names cover older content with generic substances.
        string name = obj.Id + " " + ecotype.EcotypeReference.Name + " " + string.Join(" ", obj.Substances.Select(s => s.Id));
        bool tree = new[] { "tree", "wood", "trunk", "pine", "palm", "oak", "birch", "conifer" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        source = new(target, ecotype, obj, objectId, scaleId, packedColor, scale, mass, tree);
        return true;
    }

    internal static bool TrySliceFrame(ClutterSliceDescription source, out ClutterSliceFrame frame)
    {
        frame = default;
        var target = source.Target;
        if (!target.TryResolve(out var pose, out var velocity, out _, out _)) return false;
        var colliders = source.Ecotype.GetColliders(source.ObjectId, source.ScaleId);
        if (colliders.Length != 1 || colliders[0].Handle != target.Shape) return false;
        var rotationCcf = colliders[0].LocalRotation.Inverse().Concatenate(DoubleQuatEx.FromBepu(pose.Orientation));
        var delta = colliders[0].LocalPosition.Transform(rotationCcf);
        var positionCcf = Origin(target.Native) + Double3Ex.FromBepu(pose.Position) - delta;
        var omegaCcf = Double3Ex.FromBepu(velocity.Angular);
        var linearCcf = Double3Ex.FromBepu(velocity.Linear) - double3.Cross(omegaCcf, delta);
        var ccf2Cci = VehicleReferenceFrameEx.CcfStatesToCci(target.Parent, target.Bubble.Origin.Time,
            positionCcf, linearCcf, out var positionCci, out var velocityCci);
        var rotation = rotationCcf.Concatenate(ccf2Cci).NormalizedOrZero();
        var omega = omegaCcf.Transform(ccf2Cci) + target.Parent.GetAngularVelocityCci();
        if (!ForceMath.Finite(positionCci) || !ForceMath.Finite(velocityCci) || !ForceMath.Finite(omega) || rotation == default) return false;
        frame = new(positionCci, rotation, velocityCci, omega); return true;
    }

    // This method is entered only after every replacement is a usable native vehicle.
    // Shared clutter shapes remain owned by the ecotype and are never disposed here.
    internal static bool RemoveSlicedOriginal(ClutterSliceDescription source, out string reason)
    {
        reason = "The ground object changed before replacement.";
        var target = source.Target;
        if (ClutterForceHold.IsHeld(target))
        { reason = "The ground object is currently held by Furce."; return false; }
        if (!TryDescribeSlice(target, out var current) || current!.ObjectId != source.ObjectId || current.ScaleId != source.ScaleId
            || !ReferenceEquals(current.Object, source.Object) || !target.TryResolve(out _, out _, out _, out var body)) return false;
        var renderer = Program.GetPlanetRenderer()?.GroundClutterRenderer;
        if (renderer == null || !renderer.PlanetPlacementData.TryGetValue(target.Key.CelestialHash, out var placements)
            || (uint)target.Key.EcotypeIndex >= placements.Length || !ReferenceEquals(placements[target.Key.EcotypeIndex], source.Ecotype.PlacementData)) return false;
        bool removing = false;
        try
        {
            // Publish the visual exclusion while the native object still exists. The
            // call only changes placement bits and queues uploads; no GPU resource is retired.
            renderer.ExcludeInstance(target.Key.CelestialHash, (uint)target.Key.EcotypeIndex,
                target.Key.Cell, target.Key.SubCellId, BubbleClutterStatics.ClutterExclusionType.Destroyed);
            removing = true;
            // An earlier Furce promotion may have queued an addition not yet synchronized.
            QueueSlicedExclusion(target);
            source.Ecotype.PlacementData.RemoveDisplacedObject(in target.Key);
            target.Native.RemoveInstance(target.Sim, in target.Key);
            if (body.HasValue)
            {
                Dynamics(target.Native).Remove(body.Value);
                DisplacedHandles(target.Native).Remove(target.Key);
                if (target.Sim.Simulation.Bodies.BodyExists(body.Value)) target.Sim.Simulation.Bodies.Remove(body.Value);
            }
            reason = ""; return true;
        }
        catch (Exception error)
        {
            // Once exclusion starts, retain usable replacements and finish collision
            // removal; rolling them back would leave an invisible native original.
            // ExcludeInstance may have changed the placement bit before its upload
            // queue threw. That bit is the irreversible boundary, even on a throw.
            if (!removing && source.Ecotype.PlacementData.IsInstanceIncluded(target.Key.Cell, target.Key.SubCellId))
            { reason = error.Message; return false; }
            FinishRemoval(() => renderer.QueueExclusionUpload(target.Key.CelestialHash, target.Key.EcotypeIndex, target.Key.Cell));
            FinishRemoval(() => QueueSlicedExclusion(target));
            FinishRemoval(() => target.Native.RemoveInstance(target.Sim, in target.Key));
            if (body.HasValue)
            {
                FinishRemoval(() => Dynamics(target.Native).Remove(body.Value));
                FinishRemoval(() => DisplacedHandles(target.Native).Remove(target.Key));
                FinishRemoval(() => { if (target.Sim.Simulation.Bodies.BodyExists(body.Value)) target.Sim.Simulation.Bodies.Remove(body.Value); });
            }
            FinishRemoval(() => source.Ecotype.PlacementData.RemoveDisplacedObject(in target.Key));
            reason = "Native visual exclusion reported: " + error.Message; return true;
        }
    }

    private static void QueueSlicedExclusion(ClutterForceTarget target)
    {
        var pending = Exclusions(target.Native);
        pending.RemoveAll(e => e.Key.Equals(target.Key));
        // The next native SyncGroundClutter fans this destruction out to every
        // bubble, including another bubble resident in the same terrain cell.
        pending.Add(new(target.Key, BubbleClutterStatics.ClutterExclusionType.Destroyed));
    }

    private static void FinishRemoval(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { Console.WriteLine("[purr2d2] Ground original finalization: " + error.Message); }
    }

    internal static bool ToppleClutter(ClutterSliceDescription source, double3 motionCci, out string reason)
    {
        reason = "The ground object could not enter native physics.";
        if (ClutterForceHold.IsHeld(source.Target))
        { reason = "The ground object is currently held by Furce."; return false; }
        if (!TrySliceFrame(source, out var frame) || !Promote(source.Target, out _, out reason)) return false;
        if (!source.Target.TryResolveBody(out var body))
        { reason = "The ground object lost its native body before toppling."; return false; }
        if (body.Kinematic || !float.IsFinite(body.LocalInertia.InverseMass) || body.LocalInertia.InverseMass <= 0)
        { reason = "The ground object has no movable native body."; return false; }
        var cci2Ccf = source.Target.Parent.GetCci2Ccf(source.Target.Bubble.Origin.Time);
        var up = frame.PositionCci.NormalizeOrZero().Transform(cci2Ccf);
        var direction = motionCci.Transform(cci2Ccf);
        direction = (direction - up * double3.Dot(direction, up)).NormalizeOrZero();
        if (direction.LengthSquared() < .5) direction = double3.Cross(up, Math.Abs(up.X) < .8 ? double3.UnitX : double3.UnitY).NormalizeOrZero();
        // A small impulse high above the native centre supplies torque, while KSA
        // retains inertia, angular damping, gravity, rendering and save ownership.
        var impulse = (direction * (source.MassKg * .18)).ToBepu();
        var offset = (up * (source.Tree ? Math.Clamp(source.Scale * 1.5, .5, 5) : .2)).ToBepu();
        body.ApplyImpulse(impulse, offset); body.Awake = true;
        reason = source.Tree ? "The tree entered native physics and is toppling." : "The rock entered native physics.";
        return true;
    }
}
