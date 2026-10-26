using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using KSA;

namespace Purr2D2.Force;

internal static partial class ClutterForceTargets
{
    internal interface IClutterPickFilter { bool Accept(CollidableReference collidable); }

    internal readonly record struct ProximityHit(CollidableReference Collidable, float DistanceSquared);

    private readonly struct NativeClutterPickFilter(BubbleClutterStatics native) : IClutterPickFilter
    {
        public bool Accept(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static
            ? native.IsClutterStatic(collidable.StaticHandle) : native.IsClutterBody(collidable.BodyHandle);
    }

    // The native simulation and this exact production handler are exercised by
    // host checks using a value-type filter, without constructing a game/world.
    internal struct NearPickQuery<TFilter>(Simulation simulation, TFilter filter, Vector3 origin,
        Vector3 direction, float tolerance) : ISweepHitHandler where TFilter : struct, IClutterPickFilter
    {
        internal CollidableReference? Hit;
        internal Vector3 Contact;
        internal float Time = float.PositiveInfinity;
        internal bool Exceeded, Unsupported, OriginOverlap;
        private int _candidates, _children, _shapeChildren;

        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded || Unsupported || OriginOverlap || !filter.Accept(collidable)) return false;
            if (++_candidates > MaximumCandidates) { Exceeded = true; return false; }
            var shape = collidable.Mobility == CollidableMobility.Static ? simulation.Statics[collidable.StaticHandle].Shape
                : simulation.Bodies[collidable.BodyHandle].Collidable.Shape;
            return Supports(shape, 0);
        }
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            if (++_children > MaximumChildTests) Exceeded = true;
            return !Exceeded && !Unsupported && !OriginOverlap;
        }
        private bool Supports(TypedIndex shape, int depth)
        {
            if (depth > 8 || ++_shapeChildren > MaximumChildTests) { Exceeded = true; return false; }
            if (simulation.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.TypeId, shape.Type) == null)
            { Unsupported = true; return false; }
            if (shape.Type == Compound.TypeId || shape.Type == BigCompound.TypeId)
            {
                var children = shape.Type == Compound.TypeId ? simulation.Shapes.GetShape<Compound>(shape.Index).Children
                    : simulation.Shapes.GetShape<BigCompound>(shape.Index).Children;
                for (int i = 0; i < children.Length; i++) if (!Supports(children[i].ShapeIndex, depth + 1)) return false;
                return true;
            }
            if (shape.Type == Sphere.TypeId || shape.Type == Box.TypeId || shape.Type == Capsule.TypeId
                || shape.Type == Cylinder.TypeId || shape.Type == ConvexHull.TypeId) return true;
            Unsupported = true; return false;
        }
        public void OnHit(ref float maximumT, float t, Vector3 hitLocation, Vector3 hitNormal, CollidableReference collidable)
        {
            if (Exceeded || Unsupported || OriginOverlap) return;
            // A zero-time overlap provides no reliable unique surface anchor.
            if (!float.IsFinite(t) || t <= 0 || !Finite(hitLocation) || !Finite(hitNormal))
            { Unsupported = true; return; }
            if (t >= Time) return;
            if ((hitLocation - (origin + direction * t)).LengthSquared() > (tolerance + .002f) * (tolerance + .002f))
            { Unsupported = true; return; }
            Hit = collidable; Contact = hitLocation; Time = t; maximumT = t;
        }
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        { OriginOverlap = true; Hit = null; maximumT = 0; }
        private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private struct PickQuery(PhysicsBubble bubble, CelestialSystem system) : IRayHitHandler
    {
        internal ClutterForceTarget? Target;
        internal double Distance = double.PositiveInfinity;
        internal bool Exceeded;
        private int _candidates, _children;
        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded) return false;
            var native = bubble.GroundClutterStatics;
            bool clutter = collidable.Mobility == CollidableMobility.Static
                ? native.IsClutterStatic(collidable.StaticHandle) : native.IsClutterBody(collidable.BodyHandle);
            if (!clutter) return false;
            if (++_candidates > MaximumCandidates) { Exceeded = true; return false; }
            return true;
        }
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            if (++_children > MaximumChildTests) Exceeded = true;
            return !Exceeded;
        }
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (!float.IsFinite(t) || t < 0 || t >= Distance) return;
            if (!TryTarget(bubble, system, collidable, out var target)) { Exceeded = true; return; }
            Target = target; Distance = t; maximumT = t;
        }
    }

    // Collect native overlaps up to the independent safety budget. Sorting once
    // after the sweep avoids quadratic list insertion in a dense clutter scene.
    internal struct NearQuery<TFilter>(Simulation simulation, TFilter filter, Vector3 center,
        List<ProximityHit> destination, int maximum, HashSet<CollidableReference> seen) : ISweepHitHandler where TFilter : struct, IClutterPickFilter
    {
        internal bool Exceeded, Unsupported;
        private int _candidates, _children;
        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded || Unsupported || !filter.Accept(collidable)) return false;
            if (++_candidates > MaximumCandidates) { Exceeded = true; return false; }
            var shape = collidable.Mobility == CollidableMobility.Static ? simulation.Statics[collidable.StaticHandle].Shape
                : simulation.Bodies[collidable.BodyHandle].Collidable.Shape;
            if (simulation.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.TypeId, shape.Type) == null)
            { Unsupported = true; return false; }
            return true;
        }
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            if (++_children > MaximumChildTests) Exceeded = true;
            return !Exceeded && !Unsupported;
        }
        public void OnHit(ref float maximumT, float t, Vector3 hitLocation, Vector3 hitNormal, CollidableReference collidable)
        {
            if (t == 0) Offer(collidable);
        }
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) => Offer(collidable);
        private void Offer(CollidableReference collidable)
        {
            if (Exceeded || Unsupported || !seen.Add(collidable)) return;
            var position = collidable.Mobility == CollidableMobility.Static
                ? simulation.Statics[collidable.StaticHandle].Pose.Position
                : simulation.Bodies[collidable.BodyHandle].Pose.Position;
            float distanceSquared = Vector3.DistanceSquared(center, position);
            if (!float.IsFinite(distanceSquared)) { Unsupported = true; return; }
            if (destination.Count >= maximum) { Exceeded = true; return; }
            destination.Add(new(collidable, distanceSquared));
        }
    }

    internal static void SortNearest(List<ProximityHit> hits) => hits.Sort(ProximityHitComparer.Instance);

    private sealed class ProximityHitComparer : IComparer<ProximityHit>
    {
        internal static readonly ProximityHitComparer Instance = new();
        public int Compare(ProximityHit x, ProximityHit y)
        {
            int distance = x.DistanceSquared.CompareTo(y.DistanceSquared);
            return distance != 0 ? distance : x.Collidable.Packed.CompareTo(y.Collidable.Packed);
        }
    }
}
