using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using KSA;

namespace Purr2D2.Force;

internal static partial class ClutterForceTargets
{
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

    private struct NearQuery(PhysicsBubble bubble, CelestialSystem system, List<ClutterForceTarget> destination, int maximum) : ISweepHitHandler
    {
        internal bool Exceeded, Unsupported;
        private int _candidates, _children;
        private readonly HashSet<ClutterForceIdentity> _seen = [];
        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded || Unsupported) return false;
            var native = bubble.GroundClutterStatics;
            bool clutter = collidable.Mobility == CollidableMobility.Static
                ? native.IsClutterStatic(collidable.StaticHandle) : native.IsClutterBody(collidable.BodyHandle);
            if (!clutter) return false;
            if (++_candidates > MaximumCandidates) { Exceeded = true; return false; }
            var sim = bubble.ConstraintSim!.Simulation;
            var shape = collidable.Mobility == CollidableMobility.Static ? sim.Statics[collidable.StaticHandle].Shape
                : sim.Bodies[collidable.BodyHandle].Collidable.Shape;
            if (sim.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.TypeId, shape.Type) == null)
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
            if (Exceeded || Unsupported) return;
            if (!TryTarget(bubble, system, collidable, out var target)) { Unsupported = true; return; }
            if (!_seen.Add(target!.StableKey)) return;
            if (destination.Count == maximum) { Exceeded = true; return; }
            destination.Add(target);
        }
    }
}
