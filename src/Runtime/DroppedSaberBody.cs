using System.Reflection;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

/// <summary>Owns a native inert vehicle, including its collider, bubble and orbital motion.</summary>
internal sealed class DroppedSaberBody : IDisposable
{
    private static readonly FieldInfo? Worker = typeof(Vehicle).GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Action<Vehicle, Vehicle, PhysicsBubble, double3>? Adopt = typeof(Vehicle)
        .GetMethod("AdoptSeparatedVehicle", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Vehicle), typeof(PhysicsBubble), typeof(double3)])?.CreateDelegate<Action<Vehicle, Vehicle, PhysicsBubble, double3>>();
    internal Vehicle Vehicle { get; }
    internal double4x4 EmitterToBody { get; }
    private DroppedSaberBody(Vehicle vehicle, double4x4 emitterToBody)
    {
        Vehicle = vehicle;
        EmitterToBody = emitterToBody;
    }
    internal static bool TryCreate(KittenEva owner, double4x4 emitter, float spinMultiplier,
        out DroppedSaberBody? result, out string reason)
    {
        result = null;
        reason = "The saber needs a fresh visible hand pose and native physics before it can be dropped.";
        if (!SaberDropMath.ValidFrame(emitter) || owner.IsDisposed || !owner.HasPhysicsBubble || Adopt is null
            || !ReferenceEquals(owner.System, Universe.CurrentSystem)
            || Worker?.GetValue(owner) is not VehicleUpdateState { Bubble: { } bubble }
            || ModLibrary.Get<PartTemplate>("Purr2D2_DroppedHilt") is not { } template) return false;
        var x = emitter.X.XYZ; var y = emitter.Y.XYZ; var z = emitter.Z.XYZ;
        var scale = new double3(x.Length(), y.Length(), z.Length());
        if (!Finite(emitter.Translation) || !Finite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return false;
        // Recover a rigid body frame. The authored hilt's emitter is 15cm above its COM.
        z /= scale.Z; x -= z * double3.Dot(x, z); x = x.NormalizeOrZero();
        y = double3.Cross(z, x).NormalizeOrZero();
        if (x.LengthSquared() < .5 || y.LengthSquared() < .5) return false;
        var rotation = doubleQuat.CreateFromRotationMatrix(new double4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1));
        var centerBody = emitter.Translation - z * (.15 * scale.Z);
        var rigidToOwner = double4x4.CreateFromQuaternion(rotation) * double4x4.CreateTranslation(centerBody);
        if (!double4x4.Invert(rigidToOwner, out var ownerToRigid)) return false;
        // Keep the complete observed affine attachment, including nonuniform-scale shear.
        var emitterToBody = emitter * ownerToRigid;
        var ex = emitterToBody.X.XYZ; var ey = emitterToBody.Y.XYZ; var ez = emitterToBody.Z.XYZ;
        // A conservative oriented box encloses the sheared authored hilt. Bounds, mass and
        // inertia must remain representable by the native single-precision collider system.
        var colliderScale = new double3(
            (Math.Abs(ex.X) * .03 + Math.Abs(ey.X) * .03 + Math.Abs(ez.X) * .15) / .03,
            (Math.Abs(ex.Y) * .03 + Math.Abs(ey.Y) * .03 + Math.Abs(ez.Y) * .15) / .03,
            (Math.Abs(ex.Z) * .03 + Math.Abs(ey.Z) * .03 + Math.Abs(ez.Z) * .15) / .15);
        double mass = .8 * colliderScale.X * colliderScale.Y * colliderScale.Z;
        double extent = Math.Max(.06 * colliderScale.X, Math.Max(.06 * colliderScale.Y, .30 * colliderScale.Z));
        if (!Finite(colliderScale) || !double.IsFinite(mass) || mass <= 0 || mass > float.MaxValue
            || !double.IsFinite(mass * extent * extent) || mass * extent * extent > float.MaxValue
            || (float)mass == 0 || (float)(1 / mass) == 0 || !float.IsFinite((float)(1 / mass))) return false;
        var ownerState = owner.GetPhysicsStates();
        var deltaPhys = centerBody.Transform(ownerState.Kinematic.Body2Phys);
        var releasePosition = ownerState.Kinematic.PositionPhys + deltaPhys;
        var releaseVelocity = SaberDropMath.ReleaseVelocity(ownerState.Kinematic.VelocityPhys,
            owner.BodyRates, centerBody, ownerState.Kinematic.Body2Phys);
        var releaseBody2Phys = doubleQuat.Concatenate(rotation, ownerState.Kinematic.Body2Phys);
        var releaseAngularVelocity = SaberDropMath.ReleaseAngularVelocity(ownerState.Kinematic.AngularVelocityPhys, spinMultiplier);
        // Native BodyRates is inertial even in a rotating surface bubble. Derive
        // the constructor's cache from the chosen physics rate, including CCF's
        // planetary offset, instead of scaling that offset or mutating the owner.
        var releaseBodyRates = PhysicsStates.GetBodyRates(ownerState.Origin.BubFrame,
            releaseAngularVelocity, releaseBody2Phys, owner.Parent);
        if (!NativeVector(releasePosition) || !NativeVector(releaseVelocity)
            || !NativeVector(ownerState.Kinematic.AngularVelocityPhys) || !NativeVector(owner.BodyRates)
            || !NativeVector(releaseAngularVelocity) || !NativeVector(releaseBodyRates)
            || !UnitRotation(owner.Body2Cce) || !UnitRotation(ownerState.Kinematic.Body2Phys)) return false;
        string id = "Purr2D2_DroppedHilt_" + Guid.NewGuid().ToString("N");
        Vehicle? created = null;
        Part? root = null;
        try
        {
            root = new Part(id, template) { Scale = colliderScale };
            root.RefreshScale();
            root.Tree = PartTree.CreateFromNewPartTree(root);
            created = Vehicle.CreateVehicle(owner.System, doubleQuat.Concatenate(rotation, owner.Body2Cce),
                releaseBodyRates, owner.Parent, id, root, owner.Orbit);
            Adopt(owner, created, bubble, deltaPhys.NormalizeOrZero());
            var state = created.GetPhysicsStatesMutable();
            state.Kinematic.PositionPhys = releasePosition;
            state.Kinematic.Body2Phys = releaseBody2Phys;
            state.Kinematic.VelocityPhys = releaseVelocity;
            state.Kinematic.AngularVelocityPhys = releaseAngularVelocity;
            state.Props.SetOnRails(false);
            // Keep this equipment out of the native debris eviction policy; ownership is explicit.
            created.SetFlightPlan(new FlightPlan(state.ComputeOrbit(created.OrbitColor), created.Hash));
            created.UpdatePerFrameData();
            result = new(created, emitterToBody); reason = "";
            return true;
        }
        catch (Exception error)
        {
            if (created is null && owner.System.All.TryGet(id, out Astronomical? registered)) created = registered as Vehicle;
            if (created is { IsDisposed: false })
            {
                if (Worker?.GetValue(created) is VehicleUpdateState) Universe.DestroyVehicle(created);
                else
                {
                    try { created.Dispose(endMission: false); }
                    catch { /* Constructor did not finish; remove its known registration and owned tree below. */ }
                    finally { owner.System.All.Deregister(created); }
                }
            }
            if (root?.Tree is { IsDisposed: false } tree) tree.Dispose();
            else if (created is null && root is not null) root.Dispose();
            reason = "Could not release the native hilt: " + error.Message;
            return false;
        }
    }
    internal bool TryFrame(out double4x4 emitterToEcl)
    {
        emitterToEcl = default;
        if (Vehicle.IsDisposed || !ReferenceEquals(Vehicle.System, Universe.CurrentSystem)) return false;
        var position = Vehicle.GetPositionEcl();
        if (!Finite(position)) return false;
        emitterToEcl = EmitterToBody * double4x4.CreateFromQuaternion(Vehicle.Body2Cce) * double4x4.CreateTranslation(position);
        return SaberDropMath.ValidFrame(emitterToEcl);
    }
    public void Dispose()
    {
        if (!Vehicle.IsDisposed) Universe.DestroyVehicle(Vehicle);
    }
    private static bool Finite(double3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static bool NativeVector(double3 p) => Finite(p) && float.IsFinite((float)p.X)
        && float.IsFinite((float)p.Y) && float.IsFinite((float)p.Z);
    private static bool UnitRotation(doubleQuat rotation)
    {
        double norm = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W;
        return double.IsFinite(norm) && Math.Abs(norm - 1) < .0001;
    }
}
