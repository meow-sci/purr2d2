using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal static partial class ClutterForceTargets
{
    internal readonly record struct CapsuleHit(CollidableReference Collidable, float Time,
        Vector3 Location, Vector3 Normal, bool InitialOverlap);

    // Do not shorten maximumT; every candidate remains eligible. Shape/task
    // validation is done before Bepu dispatches a possibly unsupported sweep.
    internal struct CapsuleQuery<TFilter>(Simulation simulation, TFilter filter,
        List<CapsuleHit> hits, int capacity) : ISweepHitHandler where TFilter : struct, IClutterPickFilter
    {
        internal bool Exceeded, Unsupported, Overflow;
        private int _tests;
        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded || !filter.Accept(collidable)) return false;
            if (++_tests > 4096) { Exceeded = true; return false; }
            var shape = collidable.Mobility == CollidableMobility.Static ? simulation.Statics[collidable.StaticHandle].Shape
                : simulation.Bodies[collidable.BodyHandle].Collidable.Shape;
            return Supports(shape, 0);
        }
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            if (++_tests > 4096) Exceeded = true;
            return !Exceeded;
        }
        private bool Supports(TypedIndex shape, int depth)
        {
            if (++_tests > 4096 || depth > 8) { Exceeded = true; return false; }
            if (simulation.NarrowPhase.SweepTaskRegistry.GetTask(Capsule.TypeId, shape.Type) == null)
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
        public void OnHit(ref float maximumT, float t, Vector3 location, Vector3 normal, CollidableReference collidable)
        {
            if (!float.IsFinite(t) || t < 0 || t > maximumT) { Unsupported = true; return; }
            Offer(new(collidable, t, location, normal, t <= 0));
        }
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
            => Offer(new(collidable, 0, default, default, true));
        private void Offer(CapsuleHit hit)
        {
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Collidable == hit.Collidable)
                {
                    if (hit.Time < hits[i].Time || hit.InitialOverlap) hits[i] = hit;
                    return;
                }
            if (hits.Count >= capacity) { Overflow = true; return; }
            hits.Add(hit);
        }
    }

    internal static bool SweepCapsule(PhysicsBubble bubble, double3 centerCcf, Vector3 axis,
        Vector3 centerDelta, Vector3 omega, float length, BufferPool pool, List<CapsuleHit> hits,
        out bool exceeded, out bool unsupported, out bool overflow)
    {
        hits.Clear(); exceeded = unsupported = overflow = false;
        var local = centerCcf - ClutterForceNative.Origin(bubble.GroundClutterStatics);
        if (!ForceMath.Finite(local) || local.LengthSquared() > 4096.0 * 4096 || !float.IsFinite(length)
            || length < .001f || !float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z)
            || !float.IsFinite(centerDelta.LengthSquared()) || !float.IsFinite(omega.LengthSquared())) return false;
        BepuUtilities.QuaternionEx.GetQuaternionBetweenNormalizedVectors(Vector3.UnitY, axis, out var orientation);
        var pose = new RigidPose(local.ToBepu(), orientation);
        var velocity = new BodyVelocity(centerDelta, omega);
        float speed = centerDelta.Length() + omega.Length() * (length * .5f + .01f);
        float progression = MathF.Min(.0001f, .00025f / MathF.Max(speed, .000001f));
        float convergence = MathF.Min(.00001f, .0001f / MathF.Max(speed, .000001f));
        var sim = bubble.ConstraintSim!.Simulation;
        var query = new CapsuleQuery<NativeClutterPickFilter>(sim, new(bubble.GroundClutterStatics), hits, 512);
        sim.Sweep(new Capsule(.01f, length), in pose, in velocity, 1, pool, ref query, progression, convergence, 64);
        exceeded = query.Exceeded; unsupported = query.Unsupported; overflow = query.Overflow;
        return true;
    }
}
