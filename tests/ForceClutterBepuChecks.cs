using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using Purr2D2.Force;
using Brutal.Numerics;

/// <summary>Exercise the actual host Bepu query/mobility algorithms without starting KSA or its workers.</summary>
internal static class ForceClutterBepuChecks
{
    internal static void Run()
    {
        var pool = new BufferPool();
        using (var sim = Simulation.Create(pool, new Contacts(), new Integrator(), new SolveDescription(1, 1)))
        {
            TypedIndex sphere = sim.Shapes.Add(new Sphere(1));
            TypedIndex box = sim.Shapes.Add(new Box(2, 2, 2));
            TypedIndex capsule = sim.Shapes.Add(new Capsule(1, 4.5f));
            Vector3[] corners = [new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
                new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)];
            TypedIndex convex = sim.Shapes.Add(new ConvexHull(corners, pool, out _));
            pool.Take<CompoundChild>(2, out var children);
            children[0] = new CompoundChild(new RigidPose(new(-3, 0, 0)), sphere);
            children[1] = new CompoundChild(new RigidPose(new(3, 0, 0)), sphere);
            TypedIndex compound = sim.Shapes.Add(new Compound(children));
            foreach (var shape in new[] { sphere, box, convex, compound, capsule })
                Check(sim.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.TypeId, shape.Type) != null,
                    "production sphere-overlap support guard accepts native shape type " + shape.Type);
            foreach (var (shape, name) in new[] { (sphere, "sphere"), (box, "box"), (convex, "convex hull"), (capsule, "capsule") })
            foreach (float radius in new[] { 1f, 2, 10, 50 })
            {
                var handle = sim.Statics.Add(new StaticDescription(new Vector3(radius + 1, 0, 0), shape));
                Check(Overlap(sim, pool, Vector3.Zero, radius).Contains(handle), $"{name} touching the exact {radius} m surface boundary is included");
                sim.Statics.ApplyDescription(handle, new StaticDescription(new Vector3(radius + 1.01f, 0, 0), shape));
                Check(!Overlap(sim, pool, Vector3.Zero, radius).Contains(handle), $"{name} beyond the {radius} m surface boundary is excluded");
                sim.Statics.Remove(handle);
            }
            var compoundHandle = sim.Statics.Add(new StaticDescription(Vector3.Zero, compound));
            var compoundHits = Overlap(sim, pool, new(5, 0, 0));
            Check(compoundHits.Contains(compoundHandle) && compoundHits.Count == 1,
                "compound overlap reaches its offset child and yields one parent identity");
            Check(!Overlap(sim, pool, new(6.01f, 0, 0)).Contains(compoundHandle),
                "compound broad bounds do not admit a surface beyond two metres");
            sim.Statics.Remove(compoundHandle);

            TypedIndex longBox = sim.Shapes.Add(new Box(8, .5f, .5f));
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
            var rotated = sim.Statics.Add(new StaticDescription(Vector3.Zero, rotation, longBox));
            Check(Overlap(sim, pool, new(0, 5, 0)).Contains(rotated)
                && !Overlap(sim, pool, new(3, 0, 0)).Contains(rotated),
                "native sphere overlap respects collider rotation and transformed surface shape");
            sim.Statics.Remove(rotated);
            TreeCapsulePicking(sim, pool);
            ForgivingSurfacePicking(sim, pool, sphere, box, convex, capsule);
            DistantCameraPicking(sim, pool, sphere);
            NearestOverlapOverflow(sim, pool, sphere);

            var inertia = new Box(2, 3, 4).ComputeInertia(7);
            var bodyHandle = sim.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new(1, 2, 3)),
                new BodyVelocity(new(4, 5, 6), new(0, 1, 2)), inertia, new CollidableDescription(box), new BodyActivityDescription(.005f)));
            var body = sim.Bodies[bodyHandle];
            var originalPose = body.Pose; var originalVelocity = body.Velocity;
            body.BecomeKinematic(); body.Velocity = default; body.Awake = true; body.UpdateBounds();
            sim.Timestep(.1f);
            Check(body.Kinematic && body.Pose.Position == originalPose.Position && body.Collidable.Shape == box,
                "kinematic zero-velocity hold keeps the native body and collider stationary through a real solver step");
            body.SetLocalInertia(in inertia); body.Velocity = originalVelocity; body.Awake = true; body.UpdateBounds();
            Check(!body.Kinematic && body.LocalInertia.Equals(inertia) && body.Collidable.Shape == box
                && body.Velocity.Linear == originalVelocity.Linear && body.Velocity.Angular == originalVelocity.Angular,
                "release restores the complete native inertia tensor, shape, linear velocity and angular velocity");
        }
        pool.Clear();
    }

    private static void TreeCapsulePicking(Simulation sim, BufferPool pool)
    {
        // Native Earth trees consolidate their narrow trunk/root capsules into one
        // compound body. Leaf cards themselves have no collision surface to pick.
        TypedIndex trunk = sim.Shapes.Add(new Capsule(.35f, 4.5f));
        var single = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 0), trunk));
        var hit = Cast(new(0, 0, 0), Vector3.UnitX);
        Check(hit.Hit == single && Math.Abs(hit.Distance - 9.65f) < .001f,
            "native raycast picks the exact narrow capsule trunk surface");
        sim.Statics.Remove(single);
        pool.Take<CompoundChild>(2, out var branches);
        branches[0] = new CompoundChild(new RigidPose(new(0, 5, 0)), trunk);
        branches[1] = new CompoundChild(new RigidPose(new(0, 14, 0)), trunk);
        TypedIndex tree = sim.Shapes.Add(new Compound(branches));
        var parent = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 0), tree));
        foreach (float height in new[] { 5f, 14 })
        {
            hit = Cast(new(0, height, 0), Vector3.UnitX);
            Check(hit.Hit == parent && Math.Abs(hit.Distance - 9.65f) < .001f && hit.ChildTests > 0,
                "native compound child ray hit returns its parent clutter identity and exact distance");
        }
        Check(!Cast(new(0, 5, 2), Vector3.UnitX).Hit.HasValue,
            "a ray through canopy-only space cannot falsely hit the narrow physical trunk");
        sim.Statics.ApplyDescription(parent, new StaticDescription(new Vector3(110, 0, 0), tree));
        hit = Cast(new(100, 5, 0), Vector3.UnitX);
        Check(hit.Hit == parent && Math.Abs(hit.Distance - 9.65f) < .001f,
            "common ray/tree translation leaves the exact native hit distance unchanged");
        sim.Statics.ApplyDescription(parent, new StaticDescription(new Vector3(10, 0, 0),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), tree));
        hit = Cast(new(5, 0, -10), Vector3.UnitZ);
        Check(hit.Hit == parent && Math.Abs(hit.Distance - 9.65f) < .001f,
            "native compound raycast follows the current rotated trunk geometry");
        sim.Statics.Remove(parent);

        RayQuery Cast(Vector3 origin, Vector3 direction)
        {
            var query = new RayQuery();
            sim.RayCast(origin, direction, 1000, pool, ref query);
            return query;
        }
    }

    private static void ForgivingSurfacePicking(Simulation sim, BufferPool pool,
        TypedIndex sphere, TypedIndex box, TypedIndex convex, TypedIndex capsule)
    {
        foreach (var (shape, name) in new[] { (sphere, "sphere rock"), (box, "box"), (convex, "convex hull"),
                     (capsule, "capsule trunk"), (sim.Shapes.Add(new Cylinder(1, 3)), "cylinder") })
        {
            var handle = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 1.1f), shape));
            var query = Sweep(Vector3.Zero);
            Check(query.Hit?.StaticHandle == handle && !query.Unsupported && !query.OriginOverlap
                && query.Contact.Z > 0 && query.Contact.Z < .21f,
                "forgiving production sweep catches a near-miss " + name + " and returns its off-ray surface contact");
            if (shape.Type == Sphere.TypeId || shape.Type == Capsule.TypeId || shape.Type == Cylinder.TypeId)
                Check(Math.Abs(new Vector2(query.Contact.X - 10, query.Contact.Z - 1.1f).Length() - 1) < .003f,
                    "forgiving " + name + " contact lies on the native collider, not at the swept sphere centre or ray tip");
            sim.Statics.ApplyDescription(handle, new StaticDescription(new Vector3(10, 0, 1.21f), shape));
            Check(Sweep(Vector3.Zero).Hit == null,
                "forgiving tolerance rejects " + name + " outside the configured radius");
            sim.Statics.Remove(handle);
        }
        pool.Take<CompoundChild>(2, out var branches);
        branches[0] = new CompoundChild(new RigidPose(Vector3.Zero), capsule);
        branches[1] = new CompoundChild(new RigidPose(new(0, 10, 0)), capsule);
        var compound = sim.Shapes.Add(new Compound(branches));
        var compoundHandle = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 1.1f), compound));
        var childHit = Sweep(new(0, 10, 0));
        Check(childHit.Hit?.StaticHandle == compoundHandle && !childHit.Unsupported
            && Math.Abs(new Vector2(childHit.Contact.X - 10, childHit.Contact.Z - 1.1f).Length() - 1) < .003f,
            "forgiving compound capsule sweep returns the parent identity and real child surface contact");
        sim.Statics.Remove(compoundHandle);
        var far = sim.Statics.Add(new StaticDescription(new Vector3(20, 0, 1.1f), sphere));
        var near = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 1.1f), sphere));
        Check(Sweep(Vector3.Zero).Hit?.StaticHandle == near,
            "forgiving sweep selects the first eligible contact regardless of broad-phase candidate order");
        var filtered = Sweep(Vector3.Zero, near);
        Check(filtered.Hit?.StaticHandle == far,
            "non-clutter filtering cannot hide a farther eligible forgiving pick");
        sim.Statics.Remove(far); sim.Statics.Remove(near);
        var wide = sim.Statics.Add(new StaticDescription(new Vector3(10, 0, 1.49f), sphere));
        Check(Sweep(Vector3.Zero).Hit == null && Sweep(Vector3.Zero, tolerance: .5f).Hit?.StaticHandle == wide,
            "the configured maximum tolerance expands the actual native sweep without changing the smaller default");
        sim.Statics.Remove(wide);
        var unsupportedShape = sim.Shapes.Add(new Triangle(new(10, -2, .1f), new(10, 2, .1f), new(10, 0, 3)));
        var unsupported = sim.Statics.Add(new StaticDescription(Vector3.Zero, unsupportedShape));
        var guarded = Sweep(Vector3.Zero);
        Check(guarded.Unsupported && guarded.Hit == null,
            "unexpected non-clutter shape geometry fails closed instead of being silently omitted");
        sim.Statics.Remove(unsupported);
        var overlap = sim.Statics.Add(new StaticDescription(new Vector3(0, 0, 1.1f), sphere));
        var initial = Sweep(Vector3.Zero);
        Check(initial.OriginOverlap && initial.Hit == null,
            "an initial sphere overlap fails closed instead of inventing an arbitrary surface contact");
        sim.Statics.Remove(overlap);
        Check(!ClutterForceTargets.TryPickNear(null!, default, 0, out _, out _, out _, out var disabledReason)
            && disabledReason == "", "zero tolerance disables forgiving picking before any host context read");
        foreach (double invalid in new[] { -.01, .501, double.NaN, double.PositiveInfinity })
            Check(!ClutterForceTargets.TryPickNear(null!, default, invalid, out _, out _, out _, out var reason)
                && reason.Length > 0, "invalid forgiving tolerance rejects before reading native state");

        ClutterForceTargets.NearPickQuery<StaticPickFilter> Sweep(Vector3 origin, StaticHandle? excluded = null, float tolerance = .2f)
        {
            var query = new ClutterForceTargets.NearPickQuery<StaticPickFilter>(sim, new(excluded), origin, Vector3.UnitX, tolerance);
            var pose = new RigidPose(origin); var velocity = new BodyVelocity(Vector3.UnitX);
            sim.Sweep(new Sphere(tolerance), in pose, in velocity, 1000, pool, ref query, .0001f, .00001f, 32);
            return query;
        }
    }

    private readonly struct StaticPickFilter(StaticHandle? excluded) : ClutterForceTargets.IClutterPickFilter
    {
        public bool Accept(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static
            && collidable.StaticHandle != excluded;
    }

    private static void DistantCameraPicking(Simulation sim, BufferPool pool, TypedIndex sphere)
    {
        Check(ClutterForceTargets.TryPickRange(new double3(-1500, 0, 0), out float range) && range == 2500,
            "native clutter query range includes the camera offset plus the kitten's one kilometre reach");
        foreach (var invalid in new[] { new double3(double.NaN), new double3(double.MaxValue), new double3(float.MaxValue) })
            Check(!ClutterForceTargets.TryPickRange(invalid, out _), "unrepresentable native query distances fail closed");
        var exactRock = sim.Statics.Add(new StaticDescription(new Vector3(1520, 0, 0), sphere));
        var shortRay = new RayQuery();
        sim.RayCast(Vector3.Zero, Vector3.UnitX, 1000, pool, ref shortRay);
        var longRay = new RayQuery();
        sim.RayCast(Vector3.Zero, Vector3.UnitX, range, pool, ref longRay);
        Check(shortRay.Hit == null && longRay.Hit == exactRock && Math.Abs(longRay.Distance - 1519) < .001f,
            "extended native ray reaches a rock 20 m from the kitten with the camera 1500 m away");
        sim.Statics.ApplyDescription(exactRock, new StaticDescription(new Vector3(1520, 0, 1.1f), sphere));
        var shortSweep = CastNear(1000); var longSweep = CastNear(range);
        Check(shortSweep.Hit == null && longSweep.Hit?.StaticHandle == exactRock
            && !longSweep.Unsupported && !longSweep.OriginOverlap
            && (longSweep.Contact - new Vector3(1500, 0, 0)).Length() < 21,
            "extended production sphere sweep reaches a nearby off-ray rock beyond one kilometre from the camera");
        sim.Statics.Remove(exactRock);

        ClutterForceTargets.NearPickQuery<StaticPickFilter> CastNear(float maximumDistance)
        {
            var query = new ClutterForceTargets.NearPickQuery<StaticPickFilter>(sim, new(null), Vector3.Zero, Vector3.UnitX, .2f);
            var pose = new RigidPose(Vector3.Zero); var velocity = new BodyVelocity(Vector3.UnitX);
            sim.Sweep(new Sphere(.2f), in pose, in velocity, maximumDistance, pool, ref query, .0001f, .00001f, 32);
            return query;
        }
    }

    private static void NearestOverlapOverflow(Simulation sim, BufferPool pool, TypedIndex sphere)
    {
        foreach (bool nearestFirst in new[] { false, true })
        {
            var placements = nearestFirst ? new[] { 2f, 5, 8 } : new[] { 8f, 5, 2 };
            var handles = placements.Select(position =>
                sim.Statics.Add(new StaticDescription(new Vector3(position, 0, 0), sphere))).ToArray();
            var expected = handles.Zip(placements).OrderBy(pair => pair.Second).Take(2)
                .Select(pair => pair.First).ToHashSet();
            var hits = new List<ClutterForceTargets.ProximityHit>();
            var query = new ClutterForceTargets.NearQuery<StaticPickFilter>(sim, new(null),
                Vector3.Zero, hits, ClutterForceTargets.MaximumCandidates);
            var pose = new RigidPose(Vector3.Zero); var velocity = default(BodyVelocity);
            sim.Sweep(new Sphere(10), in pose, in velocity, 0, pool, ref query, .0001f, .00001f, 32);
            ClutterForceTargets.SortNearest(hits);
            Check(!query.Exceeded && !query.Unsupported && hits.Count == 3
                && hits.Take(2).Select(hit => hit.Collidable.StaticHandle).ToHashSet().SetEquals(expected)
                && Math.Abs(hits[0].DistanceSquared - 4) < .001f
                && Math.Abs(hits[1].DistanceSquared - 25) < .001f,
                "native overlap overflow keeps exactly the closest reference origins regardless of broad-phase insertion order");
            foreach (var handle in handles) sim.Statics.Remove(handle);
        }
    }

    private static HashSet<StaticHandle> Overlap(Simulation sim, BufferPool pool, Vector3 center, float radius = 2)
    {
        var query = new OverlapQuery();
        var pose = new RigidPose(center); var velocity = default(BodyVelocity);
        sim.Sweep(new Sphere(radius), in pose, in velocity, 0, pool, ref query, .0001f, .00001f, 32);
        return query.Hits;
    }

    private struct OverlapQuery() : ISweepHitHandler
    {
        internal readonly HashSet<StaticHandle> Hits = [];
        public bool AllowTest(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static;
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;
        public void OnHit(ref float maximumT, float t, Vector3 location, Vector3 normal, CollidableReference collidable)
        { if (t == 0) Hits.Add(collidable.StaticHandle); }
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) => Hits.Add(collidable.StaticHandle);
    }

    private struct RayQuery() : IRayHitHandler
    {
        internal StaticHandle? Hit;
        internal float Distance = float.PositiveInfinity;
        internal int ChildTests;
        public bool AllowTest(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static;
        public bool AllowTest(CollidableReference collidable, int childIndex) { ChildTests++; return true; }
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal,
            CollidableReference collidable, int childIndex)
        { if (t < Distance) { Hit = collidable.StaticHandle; Distance = t; maximumT = t; } }
    }

    private struct Contacts : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation) { }
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float margin) => false;
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childA, int childB) => false;
        public bool ConfigureContactManifold<T>(int workerIndex, CollidablePair pair, ref T manifold,
            out PairMaterialProperties material) where T : unmanaged, IContactManifold<T> { material = default; return false; }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childA, int childB, ref ConvexContactManifold manifold) => false;
        public void Dispose() { }
    }

    private struct Integrator : IPoseIntegratorCallbacks
    {
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt) { }
        public void IntegrateVelocity(Vector<int> indices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide inertia, Vector<int> mask, int worker, Vector<float> dt, ref BodyVelocityWide velocity) { }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Force native Bepu check failed: " + message); Console.WriteLine("PASS: " + message); }
}
