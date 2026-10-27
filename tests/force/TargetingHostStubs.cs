using Brutal.Numerics;
using System.Runtime.InteropServices;

// Substitute only the native host boundary. ForceTargeting's snapshot, candidate
// traversal, nearest-hit selection, range checks and marker reconstruction run unchanged.
namespace KSA
{
    internal readonly record struct UniverseTime(double Value);

    internal interface IParentBody
    {
        doubleQuat GetCci2Cce();
        doubleQuat GetCce2Cci();
        doubleQuat GetCci2Ccf();
        doubleQuat GetCci2Ccf(UniverseTime time);
        doubleQuat GetCcf2Cce();
    }

    internal class ParentBody : IParentBody
    {
        internal doubleQuat Cci2Cce = doubleQuat.Identity;
        internal double3 PositionCce;
        internal double SpinRate;
        public doubleQuat GetCci2Cce() => Cci2Cce;
        public doubleQuat GetCce2Cci() => doubleQuat.Inverse(Cci2Cce);
        internal doubleQuat GetCcf2Cci(UniverseTime time) => doubleQuat.CreateFromAxisAngle(double3.UnitZ, SpinRate * time.Value);
        public doubleQuat GetCci2Ccf() => GetCci2Ccf(new(Universe.Time));
        public doubleQuat GetCci2Ccf(UniverseTime time) => doubleQuat.Inverse(GetCcf2Cci(time));
        public doubleQuat GetCcf2Cce() => doubleQuat.Concatenate(GetCcf2Cci(new(Universe.Time)), Cci2Cce);
    }

    internal sealed class Celestial : ParentBody
    {
        internal double MeanRadius = 1000;
        internal Func<double3, double>? TerrainHeight;
        internal doubleQuat GetCce2Ccf() => doubleQuat.Concatenate(GetCce2Cci(), GetCci2Ccf());
        internal double GetTerrainHeightFromDirCcf(double3 direction, bool accurate) => TerrainHeight?.Invoke(direction) ?? 0;
    }

    internal sealed class CelestialSystem { internal readonly Registry All = new(); }
    internal sealed class Registry
    {
        internal readonly List<object> Items = [];
        internal ReadOnlySpan<object> AsSpan() => CollectionsMarshal.AsSpan(Items);
        internal bool TryGet(string id, out object? registered)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i] is Vehicle vehicle && vehicle.Id == id)
                { registered = vehicle; return true; }
            registered = null;
            return false;
        }
    }

    internal static class Universe
    {
        internal static CelestialSystem? CurrentSystem;
        internal static double Time;
    }
    internal static class Program
    {
        internal static Vehicle? ControlledVehicle;
        internal static bool EditorFlag;
        internal static Camera? Camera;
        internal static Camera? GetMainCamera() => Camera;
    }

    internal sealed class Camera
    {
        internal double3 PositionCce;
        internal Celestial? NearbyCelestial;
        internal Vehicle? Following;
        internal double3 FollowOffsetCce;
        internal int PositionReads;
        // Mirrors Camera.PositionEcl: following position is sampled dynamically,
        // while the camera retains only its configured offset from that object.
        internal double3 AbsolutePositionCce => Following is { } vehicle
            ? ((ParentBody)vehicle.Parent).PositionCce + vehicle.PositionCci.Transform(vehicle.Parent.GetCci2Cce()) + FollowOffsetCce
            : PositionCce;
        internal double3 GetPositionEgo(IParentBody parent)
        { PositionReads++; return ((ParentBody)parent).PositionCce - AbsolutePositionCce; }
        internal double3 GetPositionEgo(Vehicle vehicle)
        {
            PositionReads++;
            if (ReferenceEquals(vehicle, Following)) return -FollowOffsetCce;
            return ((ParentBody)vehicle.Parent).PositionCce - AbsolutePositionCce
                + vehicle.PositionCci.Transform(vehicle.Parent.GetCci2Cce());
        }
    }

    internal class Vehicle
    {
        internal readonly string Id;
        internal CelestialSystem System;
        internal IParentBody Parent;
        internal bool IsDisposed;
        internal bool IsEditedVehicle = false;
        internal double3 PositionCci;
        internal doubleQuat Body2Cci = doubleQuat.Identity;
        internal double BoundingSphereRadiusBody = 2;
        internal readonly PartTree Parts;
        internal Vehicle(CelestialSystem system, IParentBody parent, string id, double3 position)
        {
            System = system; Parent = parent; Id = id; PositionCci = position;
            Parts = new(this);
            system.All.Items.Add(this);
        }
        internal double3 GetPositionCci() => PositionCci;
        internal doubleQuat GetBody2Cci() => Body2Cci;
        internal doubleQuat Body2Ego => doubleQuat.Concatenate(Body2Cci, Parent.GetCci2Cce());
        internal double4x4 GetMatrixAsmb2Ego(Camera camera)
            => GetMatrixAsmb2Ego(camera.GetPositionEgo(this));
        internal double4x4 GetMatrixAsmb2Ego(double3 position)
        {
            var matrix = double4x4.CreateFromQuaternion(Body2Ego);
            matrix.Translation = position;
            return matrix;
        }
    }

    internal sealed class KittenEva(CelestialSystem system, IParentBody parent, string id, double3 position)
        : Vehicle(system, parent, id, position);

    internal sealed class PartTree(Vehicle owner)
    {
        internal Vehicle OwningVehicle = owner;
        internal readonly List<Part> Parts = [];
    }

    internal sealed class Part
    {
        internal PartTree? Tree;
        internal bool IsAttachedInternal = false;
        internal double3 SphereCenterAsmb = default;
        internal double SphereRadius = 1;
        internal int RayCalls;
        internal Part StructuralPart => this;
        internal bool RayCastEgo(in double4x4 vehicleAsmb2Ego, Ray ray, out double minDistance, out double maxDistance,
            out double3 nearPosition, out double3 nearNormal, out double3 farPosition, out double3 farNormal,
            out Part? closestSubPart, out Part? farthestSubPart)
        {
            RayCalls++;
            maxDistance = 0; nearPosition = nearNormal = farPosition = farNormal = default;
            closestSubPart = farthestSubPart = null;
            // The native mesh boundary alone is represented by an analytic sphere.
            return ray.Raycast(new BoundingSphere3D(SphereCenterAsmb.Transform(vehicleAsmb2Ego), SphereRadius),
                out minDistance, out _);
        }
    }

    internal readonly record struct BoundingSphere3D(double3 Position, double Radius)
    {
        internal double RadiusSq => Radius * Radius;
    }

    internal struct Ray
    {
        internal double3 Origin, Direction;
        internal bool Raycast(BoundingSphere3D sphere, out double distance, out bool inside)
        {
            // Same quadratic and positive-root policy as the host KSA.Ray.Raycast.
            inside = false; distance = -1;
            var offset = Origin - sphere.Position;
            double b = double3.Dot(offset, Direction);
            double discriminant = b * b - (double3.Dot(offset, offset) - sphere.RadiusSq);
            if (discriminant < 0) return false;
            double root = Math.Sqrt(discriminant);
            if (-b - root > 0) { distance = -b - root; return true; }
            if (-b + root > 0) { distance = -b + root; inside = true; return true; }
            return false;
        }
    }

    internal static class NumericExtensions
    {
        internal static doubleQuat Inverse(this doubleQuat value) => doubleQuat.Inverse(value);
        internal static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
        internal static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
    }
}

namespace Purr2D2.Force
{
    using KSA;
    internal readonly record struct BubbleOrigin(UniverseTime Time);
    internal sealed class PhysicsBubble { internal BubbleOrigin Origin = new(new(Universe.Time)); }
    internal sealed class ClutterForceTarget(KittenEva owner, string name, double3 position)
    {
        internal readonly IParentBody Parent = owner.Parent;
        internal readonly CelestialSystem System = owner.System;
        internal string DisplayName => name;
        internal double3 PositionCci = position;
        internal bool Live = true;
        internal readonly PhysicsBubble NativeBubble = new();
        internal bool ThrowOnNativeRead;
        internal int NativeReads;
        internal PhysicsBubble Bubble
        {
            get
            {
                NativeReads++;
                if (ThrowOnNativeRead) throw new InvalidOperationException("UI read live native clutter state");
                return NativeBubble;
            }
        }
        internal bool IsValid => Live && ReferenceEquals(System, Universe.CurrentSystem);
        internal bool IsLocalTo(KittenEva kitten) => IsValid && ReferenceEquals(kitten.Parent, Parent)
            && ReferenceEquals(kitten.System, System);
    }

    internal static class ClutterForceTargets
    {
        internal static ClutterForceTarget[] Candidates = [];
        internal static int QueryCalls;
        internal static Ray LastRay;
        internal static int NearQueryCalls;
        internal static double LastTolerance;
        internal static bool TryPick(KittenEva owner, Ray rayCci, out ClutterForceTarget? target,
            out double distance, out double3 hitCci, out string reason)
        {
            QueryCalls++; LastRay = rayCci;
            target = null; distance = double.PositiveInfinity; hitCci = default; reason = "";
            foreach (var candidate in Candidates)
                if (candidate.IsLocalTo(owner)
                    && rayCci.Raycast(new BoundingSphere3D(candidate.PositionCci, 1), out double found, out _)
                    && found < distance)
                { target = candidate; distance = found; hitCci = rayCci.Origin + rayCci.Direction * found; }
            return target != null;
        }

        internal static bool TryPickNear(KittenEva owner, Ray rayCci, double tolerance, out ClutterForceTarget? target,
            out double distance, out double3 hitCci, out string reason)
        {
            NearQueryCalls++; LastTolerance = tolerance; LastRay = rayCci;
            target = null; distance = double.PositiveInfinity; hitCci = default; reason = "";
            foreach (var candidate in Candidates)
            {
                if (!candidate.IsLocalTo(owner)) continue;
                double along = double3.Dot(candidate.PositionCci - rayCci.Origin, rayCci.Direction);
                if (along < 0 || along >= distance) continue;
                var rayPoint = rayCci.Origin + rayCci.Direction * along;
                var offset = rayPoint - candidate.PositionCci;
                double separation = offset.Length();
                // Native narrow-phase geometry alone is substituted. The result
                // is the actual sphere surface, not the inflated tolerance shell.
                if (separation <= 1 || separation > 1 + tolerance) continue;
                target = candidate; distance = along;
                hitCci = candidate.PositionCci + offset / separation;
            }
            return target != null;
        }
    }
}
