using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal static partial class ClutterForceTargets
{
    internal readonly record struct AllRayHit(CollidableReference Collidable, float Distance, Vector3 Normal);

    // This handler deliberately never reduces maximumT: a nearer surface must
    // not hide a second object farther along the same finite segment.
    internal struct AllRayQuery<TFilter>(TFilter filter, List<AllRayHit> hits, int capacity,
        CollidableReference? only = null) : IRayHitHandler where TFilter : struct, IClutterPickFilter
    {
        internal bool Exceeded, Overflow;
        private int _tests;
        public bool AllowTest(CollidableReference collidable)
        {
            if (Exceeded || (only.HasValue && collidable != only.Value) || !filter.Accept(collidable)) return false;
            if (++_tests > 4096) { Exceeded = true; return false; }
            return true;
        }
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            if (++_tests > 4096) Exceeded = true;
            return !Exceeded;
        }
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal,
            CollidableReference collidable, int childIndex)
        {
            if (!float.IsFinite(t) || t < 0 || t > maximumT) return;
            // Deduplicate compound children while preserving zero-time overlap.
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Collidable == collidable)
                {
                    if (t < hits[i].Distance) hits[i] = new(collidable, t, normal);
                    return;
                }
            if (hits.Count >= capacity) { Overflow = true; return; }
            hits.Add(new(collidable, t, normal));
        }
    }

    // Everything returned here is valid only during the settled game-thread
    // handoff. Coordinates supplied by the detector are already in CCF.
    internal static bool TryAllHitsContext(KittenEva owner, out PhysicsBubble? bubble, out string reason)
        => TryContext(owner, out bubble, out reason);

    internal static bool TryAllHitsTarget(PhysicsBubble bubble, CelestialSystem system,
        CollidableReference collidable, out ClutterForceTarget? target)
        => TryTarget(bubble, system, collidable, out target);

    internal static bool TryAllHitsIdentity(PhysicsBubble bubble, CollidableReference collidable,
        out ClutterForceIdentity identity) => TryIdentity(bubble.GroundClutterStatics, collidable, out identity);

    internal static bool CastAllHits(PhysicsBubble bubble, double3 fromCcf, double3 toCcf,
        List<AllRayHit> hits, CollidableReference? only, out bool exceeded, out bool overflow)
    {
        hits.Clear(); exceeded = overflow = false;
        var local = fromCcf - ClutterForceNative.Origin(bubble.GroundClutterStatics);
        var delta = toCcf - fromCcf;
        double length = delta.Length();
        if (!ForceMath.Finite(local) || !ForceMath.Finite(delta) || !double.IsFinite(length)
            || length < .001 || length > float.MaxValue || local.LengthSquared() > 4096.0 * 4096)
            return false;
        var query = new AllRayQuery<NativeClutterPickFilter>(new(bubble.GroundClutterStatics), hits, 512, only);
        bubble.ConstraintSim!.Simulation.RayCast(local.ToBepu(), (delta / length).ToBepu(), (float)length,
            bubble.ConstraintSim.BufferPool, ref query);
        exceeded = query.Exceeded; overflow = query.Overflow;
        return true;
    }
}
