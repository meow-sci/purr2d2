using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;

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
            Vector3[] corners = [new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
                new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)];
            TypedIndex convex = sim.Shapes.Add(new ConvexHull(corners, pool, out _));
            pool.Take<CompoundChild>(2, out var children);
            children[0] = new CompoundChild(new RigidPose(new(-3, 0, 0)), sphere);
            children[1] = new CompoundChild(new RigidPose(new(3, 0, 0)), sphere);
            TypedIndex compound = sim.Shapes.Add(new Compound(children));
            foreach (var shape in new[] { sphere, box, convex, compound })
                Check(sim.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.TypeId, shape.Type) != null,
                    "production sphere-overlap support guard accepts native shape type " + shape.Type);
            foreach (var (shape, name) in new[] { (sphere, "sphere"), (box, "box"), (convex, "convex hull") })
            {
                var handle = sim.Statics.Add(new StaticDescription(new Vector3(3, 0, 0), shape));
                Check(Overlap(sim, pool, Vector3.Zero).Contains(handle), name + " touching the exact 2 m surface boundary is included");
                sim.Statics.ApplyDescription(handle, new StaticDescription(new Vector3(3.01f, 0, 0), shape));
                Check(!Overlap(sim, pool, Vector3.Zero).Contains(handle), name + " beyond the 2 m surface boundary is excluded");
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

    private static HashSet<StaticHandle> Overlap(Simulation sim, BufferPool pool, Vector3 center)
    {
        var query = new OverlapQuery();
        var pose = new RigidPose(center); var velocity = default(BodyVelocity);
        sim.Sweep(new Sphere(2), in pose, in velocity, 0, pool, ref query, .0001f, .00001f, 32);
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
