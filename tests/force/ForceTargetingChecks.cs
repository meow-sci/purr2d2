using Brutal.Numerics;
using KSA;
using Purr2D2.Force;

internal static class ForceTargetingChecks
{
    public static void Main(string[] arguments)
    {
        var checks = new (string Name, Action Run)[]
        {
            ("orbital", OrbitalTranslation),
            ("ground", GroundPauseAndCameraMovement),
            ("frames", FollowingCameraFrameEquivalence),
            ("range", ZoomedCameraAndOwnerRange),
            ("marker", VesselMarkerFollowsSurface),
            ("preview", NativeHoverPreview),
            ("clutter", ClutterUsesRebasedRayAndImmutableMarker),
            ("ground-rotation", RotatingGroundMarkerUsesBubbleEpoch),
            ("stale", StaleSnapshotsAndMarkers),
            ("selection", NativeSelectionPolicy),
            ("near-clutter", NearClutterFallbackAndBounds),
            ("near-priority", NearClutterKeepsExactSelectionPriority),
            ("near-terrain", NearClutterRespectsTerrain),
        };
        foreach (var check in checks)
            if (arguments.Length == 0 || arguments.Contains(check.Name, StringComparer.Ordinal))
            { check.Run(); Console.WriteLine("PASS: Force targeting " + check.Name); }
        Console.WriteLine("Force targeting checks passed (production snapshots/picking, native hover previews, orbital drift, frame rotations, camera movement/range, following markers and stale identities).");
    }

    private static void OrbitalTranslation()
    {
        foreach (var rotation in new[] { doubleQuat.Identity,
            doubleQuat.CreateFromAxisAngle(double3.NormalizeOrZero(new(1, 2, 3)), .83) })
        {
            var f = new Fixture(rotation: rotation);
            var ray = f.AimRay();
            Check(ForceTargeting.TrySnapshotRay(f.Owner, ray, out var snapshot), "UI snapshots a valid orbital ray");
            Check(f.Part.RayCalls == 0 && ClutterForceTargets.QueryCalls == 0,
                "snapshotting cannot query vessel meshes or physical clutter on the UI thread");
            // This is one simulation step at orbital speed, large compared with the 1 m hull.
            var commonMotion = new double3(128, -64, 31);
            f.Owner.PositionCci += commonMotion;
            f.Target.PositionCci += commonMotion;
            f.Camera.PositionCce += commonMotion.Transform(f.Parent.GetCci2Cce());
            var pick = Pick(f, snapshot, "an orbital step cannot leave the ray behind the nearby vessel");
            Check(ReferenceEquals(pick.Vehicle, f.Target) && ReferenceEquals(pick.Part, f.Part),
                "production candidate traversal still chooses the aimed vessel");
            var directionCci = double3.NormalizeOrZero(f.TargetOffsetCci - f.CameraOffsetCci);
            Equal(ClutterForceTargets.LastRay.Origin, f.Owner.PositionCci + f.CameraOffsetCci,
                "native clutter query receives a ray rebased to the current owner position");
            Equal(ClutterForceTargets.LastRay.Direction, directionCci, "parent rotation preserves inertial aim");
            Equal(pick.HitBody, -directionCci, "the native surface hit retains the same body-local point");
        }
    }

    private static void GroundPauseAndCameraMovement()
    {
        var parent = new Celestial();
        var f = new Fixture(parent: parent, ownerPosition: new(1100, 0, 0));
        f.Camera.NearbyCelestial = parent;
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "ground ray snapshots while paused");
        var before = Pick(f, snapshot, "a paused ground scene keeps its surface pick");
        var repeated = Pick(f, snapshot, "repeated paused resolution does not advance or displace the ray");
        Equal(before.HitBody, repeated.HitBody, "paused ground picks remain stable");
        // The frozen UI ray must survive an independent camera movement before the settled query.
        f.Camera.PositionCce += new double3(43, -27, 15);
        var movedCamera = Pick(f, snapshot, "camera movement cannot reinterpret the already-snapshotted ray");
        Equal(before.HitBody, movedCamera.HitBody, "camera movement preserves the chosen surface point");
        Equal(before.HitCci, movedCamera.HitCci, "a stationary vessel keeps its immutable capture position");
    }

    private static void ZoomedCameraAndOwnerRange()
    {
        var f = new Fixture(cameraOffset: new(0, -2000, 0), targetOffset: new(0, 20, 0));
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "a distant orbital camera can snapshot aim");
        var pick = Pick(f, snapshot, "camera-to-surface distance above 1 km cannot reject a surface near the kitten");
        Check((pick.HitCci - f.Owner.PositionCci).Length() < 25,
            "long camera rays still resolve within the owner-to-surface Force range");

        // Point backwards from a camera ahead of the target. Its ray hit is less
        // than 1 km from the camera, but more than 1 km from the owner.
        f = new Fixture(cameraOffset: new(0, 2000, 0), targetOffset: new(0, 1002, 0));
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out snapshot), "out-of-range aim still snapshots safely");
        Check(!ForceTargeting.TryPick(f.Owner, snapshot, out pick, out var reason) && pick == null
            && reason.Contains("beyond Furce range", StringComparison.Ordinal),
            "camera-relative proximity cannot bypass the final owner-to-surface range guard");
    }

    private static void FollowingCameraFrameEquivalence()
    {
        foreach (bool following in new[] { false, true })
        {
            var f = new Fixture(rotation: doubleQuat.CreateFromAxisAngle(double3.UnitY, .63));
            if (following)
            {
                f.Camera.Following = f.Owner;
                f.Camera.FollowOffsetCce = f.CameraOffsetCci.Transform(f.Parent.GetCci2Cce());
            }
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "either camera mode can snapshot its current aim");
            var baseline = Pick(f, snapshot, "either camera mode picks the nearby hull before movement");
            var parentMotion = new double3(5e8, -2e8, 7e8);
            var orbitalMotion = new double3(128, -64, 31);
            f.Parent.PositionCce += parentMotion;
            f.Owner.PositionCci += orbitalMotion; f.Target.PositionCci += orbitalMotion;
            if (!following) f.Camera.PositionCce += parentMotion + orbitalMotion.Transform(f.Parent.GetCci2Cce());
            var picked = Pick(f, snapshot, "parent and common orbital translation preserve either camera mode's pick");
            Equal(picked.HitBody, baseline.HitBody,
                "following and absolute cameras resolve the same physical surface in the current owner-relative frame");
            if (following)
                Equal(f.Camera.GetPositionEgo(f.Owner), -f.Camera.FollowOffsetCce,
                    "the following camera dynamically includes current parent/owner translation rather than freezing world position");
        }
    }

    private static void VesselMarkerFollowsSurface()
    {
        var rotation = doubleQuat.CreateFromAxisAngle(double3.UnitZ, -.61);
        var f = new Fixture(rotation: rotation);
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "marker fixture snapshots aim");
        var pick = Pick(f, snapshot, "marker fixture picks the native hull");
        var immutableHitCci = pick.HitCci;
        f.Target.PositionCci += new double3(128, 6, -3);
        f.Target.Body2Cci = doubleQuat.CreateFromAxisAngle(double3.UnitX, .9);
        f.Camera.PositionCce += new double3(-19, 5, 11);
        Check(ForceTargeting.TryHitEgo(pick, f.Camera, out var marker), "a live vessel can reconstruct its current marker");
        var currentPointCci = f.Target.PositionCci + pick.HitBody.Transform(f.Target.Body2Cci);
        var expected = f.Parent.PositionCce - f.Camera.PositionCce + currentPointCci.Transform(rotation);
        Equal(marker, expected, "the marker follows both vessel translation and body rotation in a rotated parent frame");
        Check((marker - (f.Parent.PositionCce - f.Camera.PositionCce + immutableHitCci.Transform(rotation))).Length() > 100,
            "the marker is not projected from the stale absolute CCI capture position");
        Equal(pick.HitCci, immutableHitCci, "following the vessel does not mutate the settled capture snapshot");
    }

    private static void NativeHoverPreview()
    {
        var f = new Fixture();
        var ray = f.AimRay();
        Check(ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 12, ray, f.Camera, out var first),
            "a live native candidate produces a rendered hover preview");
        Check(ReferenceEquals(first.Vehicle, f.Target) && ReferenceEquals(first.Part, f.Part),
            "the preview returns the canonical structural part and its vehicle");
        Equal(first.HitEgo, ray.Origin + double3.NormalizeOrZero(ray.Direction) * 12,
            "the preview reconstructs the current ray point from its supplied distance");

        var movedRay = new Ray { Origin = ray.Origin + new double3(3, -2, 5), Direction = ray.Direction * 4 };
        Check(ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 19, movedRay, f.Camera, out var moved),
            "a later rendered ray and distance update the same candidate preview");
        Equal(moved.HitEgo, movedRay.Origin + double3.NormalizeOrZero(movedRay.Direction) * 19,
            "the preview follows the latest rendered ray and distance");
        Check((moved.HitEgo - first.HitEgo).Length() > 1,
            "successive frames do not reuse a stale hover point");

        int meshReads = f.Part.RayCalls;
        int clutterReads = ClutterForceTargets.QueryCalls + ClutterForceTargets.NearQueryCalls;
        Check(meshReads == 0 && clutterReads == 0,
            "render previews do not raycast meshes or query physical clutter");

        var rangeRay = new Ray { Origin = f.Camera.GetPositionEgo(f.Owner), Direction = new(0, 7, 0) };
        Check(ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, ForceTargeting.MaximumRange,
                rangeRay, f.Camera, out var atLimit)
            && Math.Abs((atLimit.HitEgo - f.Camera.GetPositionEgo(f.Owner)).LengthSquared()
                - ForceTargeting.MaximumRange * ForceTargeting.MaximumRange) < 1e-6,
            "a rendered preview exactly at the owner range limit remains valid");
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, ForceTargeting.MaximumRange + .01,
                rangeRay, f.Camera, out _),
            "a rendered preview past the owner range limit is rejected");

        foreach (double distance in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
            Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, distance, ray, f.Camera, out _),
                "nonfinite or negative ray distances are rejected");
        foreach (var invalidRay in new[]
        {
            new Ray { Origin = new(double.NaN, 0, 0), Direction = double3.UnitX },
            new Ray { Origin = new(double.PositiveInfinity, 0, 0), Direction = double3.UnitX },
            new Ray { Origin = double3.Zero, Direction = double3.Zero },
            new Ray { Origin = double3.Zero, Direction = new(double.NaN, 0, 0) },
            new Ray { Origin = double3.Zero, Direction = new(0, double.PositiveInfinity, 0) },
        })
            Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 1, invalidRay, f.Camera, out _),
                "nonfinite origins and zero or nonfinite directions are rejected");
        Check(ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 3,
                new Ray { Origin = ray.Origin, Direction = new(0, 25, 0) }, f.Camera, out var normalized)
            && normalized.HitEgo == ray.Origin + new double3(0, 3, 0),
            "finite non-unit directions are normalized before applying the rendered hit distance");

        f.Part.IsAttachedInternal = true;
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 1, ray, f.Camera, out _),
            "internal parts cannot produce a preview");
        f.Part.IsAttachedInternal = false;
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, new Part(), 1, ray, f.Camera, out _),
            "a stale part without a live vehicle tree cannot produce a preview");

        var ownerPart = new Part { Tree = f.Owner.Parts };
        f.Owner.Parts.Parts.Add(ownerPart);
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, ownerPart, 1, ray, f.Camera, out _),
            "the controlled kitten itself cannot be previewed as a target");

        var kittenTarget = new KittenEva(f.System, f.Parent, "other kitten", f.Owner.PositionCci + new double3(0, 5, 0));
        var kittenPart = new Part { Tree = kittenTarget.Parts };
        kittenTarget.Parts.Parts.Add(kittenPart);
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, kittenPart, 1, ray, f.Camera, out _),
            "another kitten cannot be previewed as a target");

        var otherControlled = new KittenEva(f.System, f.Parent, "other controlled kitten", f.Owner.PositionCci);
        KSA.Program.ControlledVehicle = otherControlled;
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 1, ray, f.Camera, out _),
            "a preview owner must still be the controlled kitten");
        KSA.Program.ControlledVehicle = f.Owner;
        KSA.Program.EditorFlag = true;
        Check(!ForceTargeting.TryPreviewNativeHit(f.Owner, f.Part, 1, ray, f.Camera, out _),
            "previews are disabled in the editor");
        KSA.Program.EditorFlag = false;

        var staleOwner = new Fixture();
        staleOwner.System.All.Items.Remove(staleOwner.Owner);
        Check(!ForceTargeting.TryPreviewNativeHit(staleOwner.Owner, staleOwner.Part, 1,
                staleOwner.AimRay(), staleOwner.Camera, out _),
            "an unregistered controlled kitten cannot produce a preview");

        var disposedOwner = new Fixture();
        disposedOwner.Owner.IsDisposed = true;
        Check(!ForceTargeting.TryPreviewNativeHit(disposedOwner.Owner, disposedOwner.Part, 1,
                disposedOwner.AimRay(), disposedOwner.Camera, out _),
            "a disposed controlled kitten cannot produce a preview");

        var wrongOwnerSystem = new Fixture();
        wrongOwnerSystem.Owner.System = new CelestialSystem();
        Check(!ForceTargeting.TryPreviewNativeHit(wrongOwnerSystem.Owner, wrongOwnerSystem.Part, 1,
                wrongOwnerSystem.AimRay(), wrongOwnerSystem.Camera, out _),
            "an owner outside the current system cannot produce a preview");

        var unregisteredTarget = new Fixture();
        unregisteredTarget.System.All.Items.Remove(unregisteredTarget.Target);
        Check(!ForceTargeting.TryPreviewNativeHit(unregisteredTarget.Owner, unregisteredTarget.Part, 1,
                unregisteredTarget.AimRay(), unregisteredTarget.Camera, out _),
            "an unregistered target cannot produce a preview");

        var disposedTarget = new Fixture();
        disposedTarget.Target.IsDisposed = true;
        Check(!ForceTargeting.TryPreviewNativeHit(disposedTarget.Owner, disposedTarget.Part, 1,
                disposedTarget.AimRay(), disposedTarget.Camera, out _),
            "a disposed target cannot produce a preview");

        var wrongParent = new Fixture();
        wrongParent.Target.Parent = new ParentBody();
        Check(!ForceTargeting.TryPreviewNativeHit(wrongParent.Owner, wrongParent.Part, 1,
                wrongParent.AimRay(), wrongParent.Camera, out _),
            "a target in a different parent frame cannot produce a preview");

        var wrongSystem = new Fixture();
        wrongSystem.Target.System = new CelestialSystem();
        Check(!ForceTargeting.TryPreviewNativeHit(wrongSystem.Owner, wrongSystem.Part, 1,
                wrongSystem.AimRay(), wrongSystem.Camera, out _),
            "a target outside the current system cannot produce a preview");

        var noQuery = new Fixture();
        _ = ForceTargeting.TryPreviewNativeHit(noQuery.Owner, noQuery.Part, 1, noQuery.AimRay(), noQuery.Camera, out _);
        Check(noQuery.Part.RayCalls == 0 && ClutterForceTargets.QueryCalls == 0 && ClutterForceTargets.NearQueryCalls == 0,
            "all preview guards leave native mesh and clutter query counters unchanged");

        var allocation = new Fixture();
        var allocationRay = new Ray
        {
            Origin = allocation.Camera.GetPositionEgo(allocation.Owner),
            Direction = double3.UnitX,
        };
        for (int i = 0; i < 16; i++)
            Check(ForceTargeting.TryPreviewNativeHit(allocation.Owner, allocation.Part, 10 + i,
                    allocationRay, allocation.Camera, out _),
                "the repeated preview fixture remains valid during warmup");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            if (!ForceTargeting.TryPreviewNativeHit(allocation.Owner, allocation.Part, 10 + i,
                    allocationRay, allocation.Camera, out _))
                throw new InvalidOperationException("Force targeting regression: warmed native preview unexpectedly failed.");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0,
            $"warmed native hover previews allocate no frame memory in a stable scene; allocated {allocated} bytes");
    }

    private static void ClutterUsesRebasedRayAndImmutableMarker()
    {
        var f = new Fixture(rotation: doubleQuat.CreateFromAxisAngle(double3.UnitZ, .4));
        f.System.All.Items.Remove(f.Target);
        var clutter = new ClutterForceTarget(f.Owner, "native physical object", f.Owner.PositionCci + f.TargetOffsetCci);
        ClutterForceTargets.Candidates = [clutter];
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "clutter aim snapshots in the owner frame");
        var motion = new double3(128, -64, 31);
        f.Owner.PositionCci += motion; clutter.PositionCci += motion;
        f.Camera.PositionCce += motion.Transform(f.Parent.GetCci2Cce());
        var pick = Pick(f, snapshot, "physical clutter receives the current absolute CCI ray");
        Check(ReferenceEquals(pick.Clutter, clutter) && pick.Vehicle == null, "production selection accepts the native clutter result");
        var frozenPoint = pick.HitCci;
        clutter.PositionCci += new double3(30, 40, 50);
        clutter.Live = false;
        clutter.ThrowOnNativeRead = true;
        int nativeReads = clutter.NativeReads;
        f.Camera.PositionCce += new double3(4, -9, 1);
        Check(ForceTargeting.TryHitEgo(pick, f.Camera, out var marker), "clutter marker remains UI-safe without live native state");
        Equal(marker, f.Parent.PositionCce - f.Camera.PositionCce + frozenPoint.Transform(f.Parent.GetCci2Cce()),
            "physical clutter marker projects its immutable CCI hit rather than querying moving/disposed native state");
        Equal(pick.HitCci, frozenPoint, "clutter marker reconstruction preserves its immutable hit");
        Check(clutter.NativeReads == nativeReads, "clutter UI projection does not touch native bubble state");
    }

    private static void RotatingGroundMarkerUsesBubbleEpoch()
    {
        var parent = new Celestial { MeanRadius = 6_371_000, SpinRate = 7.2921159e-5 };
        var fixedOwner = new double3(parent.MeanRadius + 100, 0, 0);
        var fixedSurface = fixedOwner + new double3(0, 20, 0);
        var ownerCci = fixedOwner.Transform(parent.GetCcf2Cci(new(5.05)));
        var colliderCci = fixedSurface.Transform(parent.GetCcf2Cci(new(5)));
        var f = new Fixture(parent: parent, rotation: doubleQuat.CreateFromAxisAngle(double3.UnitX, .37),
            ownerPosition: ownerCci, targetOffset: colliderCci - ownerCci);
        Universe.Time = 5.05;
        f.Camera.NearbyCelestial = parent;
        f.System.All.Items.Remove(f.Target);
        var clutter = new ClutterForceTarget(f.Owner, "Earth physical tree", colliderCci);
        clutter.NativeBubble.Origin = new(new(5));
        ClutterForceTargets.Candidates = [clutter];
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "rotating ground aim snapshots");
        var pick = Pick(f, snapshot, "the native bubble epoch supplies a rotating-ground surface hit");
        var expectedFixed = pick.HitCci.Transform(parent.GetCci2Ccf(new(5)));
        Check(pick.HitCcf.HasValue, "ground picks retain a fixed surface render anchor");
        Equal(pick.HitCcf!.Value, expectedFixed, "the fixed anchor uses the native bubble epoch rather than current render time");
        var frozenCci = pick.HitCci;
        clutter.ThrowOnNativeRead = true;
        int nativeReads = clutter.NativeReads;
        Universe.Time = 5.10;
        f.Owner.PositionCci = fixedOwner.Transform(parent.GetCcf2Cci(new(Universe.Time)));
        f.Camera.PositionCce = parent.PositionCce + (f.Owner.PositionCci + f.CameraOffsetCci).Transform(parent.GetCci2Cce());
        Check(ForceTargeting.TryHitEgo(pick, f.Camera, out var marker), "the cached marker projects after the planet rotates");
        var expected = f.Camera.GetPositionEgo(parent) + expectedFixed.Transform(parent.GetCcf2Cce());
        Equal(marker, expected, "the marker follows the fixed Earth surface between throttled pick queries");
        var inertialMarker = f.Camera.GetPositionEgo(parent) + frozenCci.Transform(parent.GetCci2Cce());
        Check((marker - inertialMarker).Length() > 20,
            "the test exposes the tens of metres of marker drift caused by retaining an inertial ground anchor");
        Check(clutter.NativeReads == nativeReads, "rotating ground marker projection never reads native clutter on the UI thread");
        Equal(pick.HitCci, frozenCci, "the rotating render anchor preserves the immutable settled hit position");
    }

    private static void StaleSnapshotsAndMarkers()
    {
        foreach (string stale in new[] { "system", "parent", "owner", "disposed", "unregistered", "editor", "camera" })
        {
            var f = new Fixture();
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "stale fixture initially snapshots");
            switch (stale)
            {
                case "system": Universe.CurrentSystem = new(); break;
                case "parent": f.Owner.Parent = new ParentBody(); break;
                case "owner": KSA.Program.ControlledVehicle = f.Target; break;
                case "disposed": f.Owner.IsDisposed = true; break;
                case "unregistered": f.System.All.Items.Remove(f.Owner); break;
                case "editor": KSA.Program.EditorFlag = true; break;
                case "camera": KSA.Program.Camera = null; break;
            }
            Check(!ForceTargeting.TryPick(f.Owner, snapshot, out var pick, out _) && pick == null,
                "stale " + stale + " state rejects a queued ray");
            Check(ClutterForceTargets.QueryCalls == 0 && f.Part.RayCalls == 0,
                "stale " + stale + " state is rejected before native geometry queries");
        }
        foreach (string stale in new[] { "system", "parent", "disposed", "part", "unregistered" })
        {
            var f = new Fixture();
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "marker guard fixture snapshots");
            var pick = Pick(f, snapshot, "marker guard fixture picks");
            switch (stale)
            {
                case "system": Universe.CurrentSystem = new(); break;
                case "parent": f.Target.Parent = new ParentBody(); break;
                case "disposed": f.Target.IsDisposed = true; break;
                case "part": f.Part.Tree = f.Owner.Parts; break;
                case "unregistered": f.System.All.Items.Remove(f.Target); break;
            }
            Check(!ForceTargeting.TryHitEgo(pick, f.Camera, out _), "stale " + stale + " state hides the vessel surface marker");
        }
        var valid = new Fixture();
        Check(!ForceTargeting.TrySnapshotRay(valid.Owner, new Ray { Origin = new(double.NaN, 0, 0), Direction = double3.UnitY }, out _)
            && !ForceTargeting.TrySnapshotRay(valid.Owner, new Ray { Origin = default, Direction = default }, out _),
            "invalid camera rays cannot enter the settled query queue");
    }

    private static void NativeSelectionPolicy()
    {
        var f = new Fixture(cameraOffset: new(0, -40, 0), targetOffset: new(0, 20, 0));
        var nearer = new Vehicle(f.System, f.Parent, "nearer vessel", f.Owner.PositionCci + new double3(0, 10, 0));
        var nearerPart = new Part { Tree = nearer.Parts };
        nearer.Parts.Parts.Add(nearerPart);
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "nearest-surface fixture snapshots");
        Check(ReferenceEquals(Pick(f, snapshot, "native nearest hit policy executes").Part, nearerPart),
            "production traversal chooses the closest part rather than the first registered vessel");
        _ = new KittenEva(f.System, f.Parent, "blocking kitten", f.Owner.PositionCci + new double3(0, 5, 0));
        Check(!ForceTargeting.TryPick(f.Owner, snapshot, out _, out var reason) && reason.Contains("Kittens cannot", StringComparison.Ordinal),
            "a nearer kitten blocks a vessel capture rather than becoming a capturable target");
    }

    private static void NearClutterFallbackAndBounds()
    {
        foreach (bool compatibilityRayOverload in new[] { false, true })
        {
            var f = new Fixture(cameraOffset: new(0, -40, 0));
            f.System.All.Items.Remove(f.Target);
            var near = new ClutterForceTarget(f.Owner, "thin physical trunk", f.Owner.PositionCci + new double3(1.15, 20, 0));
            ClutterForceTargets.Candidates = [near];
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "near-contact aim snapshots");
            ForcePick? pick;
            string reason;
            bool found = compatibilityRayOverload
                ? ForceTargeting.TryPick(f.Owner, f.AimRay(), out pick, out reason)
                : ForceTargeting.TryPick(f.Owner, snapshot, out pick, out reason);
            Check(found && ReferenceEquals(pick?.Clutter, near), "the default tolerance catches a physical surface 0.15 m beside the ray: " + reason);
            Check(ClutterForceTargets.NearQueryCalls == 1 && ClutterForceTargets.LastTolerance == .2,
                "both production entry points use a 0.2 m default only after their exact sweep misses");
            Equal(pick!.HitCci, near.PositionCci - double3.UnitX,
                "leniency retains an actual contact point rather than a point on an inflated tolerance shell");
        }

        var miss = new Fixture(cameraOffset: new(0, -40, 0));
        miss.System.All.Items.Remove(miss.Target);
        ClutterForceTargets.Candidates = [new(miss.Owner, "too far beside ray", miss.Owner.PositionCci + new double3(1.201, 20, 0))];
        Check(ForceTargeting.TrySnapshotRay(miss.Owner, miss.AimRay(), out var missedSnapshot)
            && !ForceTargeting.TryPick(miss.Owner, missedSnapshot, out _, out _)
            && ClutterForceTargets.NearQueryCalls == 1, "surfaces beyond the default tolerance remain misses");

        var disabled = new Fixture(cameraOffset: new(0, -40, 0));
        disabled.System.All.Items.Remove(disabled.Target);
        ClutterForceTargets.Candidates = [new(disabled.Owner, "near but disabled", disabled.Owner.PositionCci + new double3(1.1, 20, 0))];
        Check(ForceTargeting.TrySnapshotRay(disabled.Owner, disabled.AimRay(), out var disabledSnapshot)
            && !ForceTargeting.TryPick(disabled.Owner, disabledSnapshot, out _, out _, 0)
            && ClutterForceTargets.NearQueryCalls == 0, "zero tolerance disables the native near-contact fallback entirely");
        disabled.System.All.Items.Add(disabled.Target);
        Check(ForceTargeting.TryPick(disabled.Owner, disabledSnapshot, out var exactAtZero, out _, 0)
            && ReferenceEquals(exactAtZero?.Vehicle, disabled.Target) && ClutterForceTargets.NearQueryCalls == 0,
            "zero assistance still permits an exact native vessel pick");

        var maximum = new Fixture(cameraOffset: new(0, -40, 0));
        maximum.System.All.Items.Remove(maximum.Target);
        ClutterForceTargets.Candidates = [new(maximum.Owner, "wider configured tolerance", maximum.Owner.PositionCci + new double3(1.4, 20, 0))];
        Check(ForceTargeting.TrySnapshotRay(maximum.Owner, maximum.AimRay(), out var maximumSnapshot)
            && ForceTargeting.TryPick(maximum.Owner, maximumSnapshot, out _, out _, .5)
            && ClutterForceTargets.LastTolerance == .5, "the supported maximum tolerance reaches the native near-contact query");

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -.01, .50001 })
        {
            var f = new Fixture(cameraOffset: new(0, -40, 0));
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "invalid-tolerance fixture snapshots safely");
            Check(!ForceTargeting.TryPick(f.Owner, snapshot, out _, out _, invalid)
                && !ForceTargeting.TryPick(f.Owner, f.AimRay(), out _, out _, invalid)
                && ClutterForceTargets.QueryCalls == 0 && ClutterForceTargets.NearQueryCalls == 0 && f.Part.RayCalls == 0,
                "nonfinite or unsupported tolerances reject both entry points before any native geometry query");
        }

        var far = new Fixture(cameraOffset: new(0, 2000, 0), targetOffset: new(0, 1002, 0));
        far.System.All.Items.Remove(far.Target);
        ClutterForceTargets.Candidates = [new(far.Owner, "near ray but out of owner range", far.Owner.PositionCci + new double3(1.1, 1002, 0))];
        Check(ForceTargeting.TrySnapshotRay(far.Owner, far.AimRay(), out var farSnapshot)
            && !ForceTargeting.TryPick(far.Owner, farSnapshot, out _, out _),
            "leniency cannot capture a physical surface beyond the owner-to-surface Force range");
    }

    private static void NearClutterKeepsExactSelectionPriority()
    {
        var f = new Fixture(cameraOffset: new(0, -40, 0));
        var near = new ClutterForceTarget(f.Owner, "nearer off-axis trunk", f.Owner.PositionCci + new double3(1.1, 5, 0));
        ClutterForceTargets.Candidates = [near];
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "exact-vessel priority fixture snapshots");
        Check(ReferenceEquals(Pick(f, snapshot, "an exact vessel beats a nearer off-axis physical contact").Vehicle, f.Target)
            && ClutterForceTargets.NearQueryCalls == 0, "near-contact fallback cannot steal an exact vessel pick");

        f = new Fixture(cameraOffset: new(0, -40, 0));
        f.System.All.Items.Remove(f.Target);
        near = new(f.Owner, "nearer off-axis trunk", f.Owner.PositionCci + new double3(1.1, 5, 0));
        var exact = new ClutterForceTarget(f.Owner, "exact physical rock", f.Owner.PositionCci + new double3(0, 30, 0));
        ClutterForceTargets.Candidates = [near, exact];
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out snapshot), "exact-clutter priority fixture snapshots");
        Check(ReferenceEquals(Pick(f, snapshot, "an exact physical rock beats a nearer off-axis contact").Clutter, exact)
            && ClutterForceTargets.NearQueryCalls == 0, "near-contact fallback cannot replace an exact clutter hit");

        f = new Fixture(cameraOffset: new(0, -40, 0));
        f.System.All.Items.Remove(f.Target);
        _ = new KittenEva(f.System, f.Parent, "blocking kitten", f.Owner.PositionCci + new double3(0, 10, 0));
        ClutterForceTargets.Candidates = [new(f.Owner, "off-axis tree", f.Owner.PositionCci + new double3(1.1, 20, 0))];
        Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out snapshot)
            && !ForceTargeting.TryPick(f.Owner, snapshot, out _, out var reason)
            && reason.Contains("Kittens cannot", StringComparison.Ordinal) && ClutterForceTargets.NearQueryCalls == 0,
            "an exact kitten obstruction blocks lenient capture behind it");
    }

    private static void NearClutterRespectsTerrain()
    {
        foreach (bool beforeTerrain in new[] { true, false })
        {
            var parent = new Celestial { MeanRadius = 1000 };
            double distance = beforeTerrain ? 95 : 200;
            var f = new Fixture(parent: parent, ownerPosition: new(1100, 0, 0),
                cameraOffset: default(double3), targetOffset: new(-distance, 0, 0));
            f.Camera.NearbyCelestial = parent;
            f.System.All.Items.Remove(f.Target);
            var near = new ClutterForceTarget(f.Owner, "near terrain contact", f.Owner.PositionCci + new double3(-distance, 1.1, 0));
            ClutterForceTargets.Candidates = [near];
            Check(ForceTargeting.TrySnapshotRay(f.Owner, f.AimRay(), out var snapshot), "terrain near-contact fixture snapshots");
            bool found = ForceTargeting.TryPick(f.Owner, snapshot, out var pick, out var reason);
            if (beforeTerrain)
                Check(found && ReferenceEquals(pick?.Clutter, near) && ClutterForceTargets.NearQueryCalls == 1,
                    "a visible near contact before terrain is picked even though the ray would hit the planet later: " + reason);
            else
                Check(!found && pick == null && reason.Contains("Terrain cannot", StringComparison.Ordinal),
                    "terrain still occludes a lenient physical contact beyond the surface");
        }

        // A ridge lies only on the short lateral segment to the assisted contact.
        // The original pointer ray stays clear up to that depth, so testing that
        // ray alone would incorrectly capture through the ridge.
        int ridgeSamples = 0;
        var ridgeBody = new Celestial
        {
            MeanRadius = 1000,
            TerrainHeight = direction =>
            {
                if (direction.Y <= 5e-5) return 0;
                ridgeSamples++;
                return 110;
            },
        };
        var ridge = new Fixture(parent: ridgeBody, ownerPosition: new(1100, 0, 0),
            cameraOffset: default(double3), targetOffset: new(-95, 0, 0));
        ridge.Camera.NearbyCelestial = ridgeBody;
        ridge.System.All.Items.Remove(ridge.Target);
        ClutterForceTargets.Candidates = [new(ridge.Owner, "contact behind off-axis ridge", new(1005, 1.1, 0))];
        Check(ForceTargeting.TrySnapshotRay(ridge.Owner, ridge.AimRay(), out var ridgeSnapshot)
            && !ForceTargeting.TryPick(ridge.Owner, ridgeSnapshot, out _, out var ridgeReason)
            && ridgeReason.Contains("Terrain cannot", StringComparison.Ordinal)
            && ClutterForceTargets.NearQueryCalls == 1 && ridgeSamples > 0,
            "the actual assisted contact segment cannot pass through off-axis terrain even when the original pointer ray is clear");
    }

    private sealed class Fixture
    {
        internal readonly CelestialSystem System = new();
        internal readonly ParentBody Parent;
        internal readonly KittenEva Owner;
        internal readonly Vehicle Target;
        internal readonly Part Part;
        internal readonly Camera Camera = new();
        internal readonly double3 CameraOffsetCci, TargetOffsetCci;

        internal Fixture(doubleQuat? rotation = null, ParentBody? parent = null, double3? ownerPosition = null,
            double3? cameraOffset = null, double3? targetOffset = null)
        {
            Parent = parent ?? new ParentBody();
            Parent.Cci2Cce = rotation ?? doubleQuat.Identity;
            Parent.PositionCce = new(80000, -90000, 60000);
            CameraOffsetCci = cameraOffset ?? new double3(3, -40, 8);
            TargetOffsetCci = targetOffset ?? new double3(0, 20, 0);
            Owner = new(System, Parent, "owner", ownerPosition ?? new double3(4000000, -8000000, 16000000));
            Target = new(System, Parent, "target", Owner.PositionCci + TargetOffsetCci);
            Part = new() { Tree = Target.Parts };
            Target.Parts.Parts.Add(Part);
            Camera.PositionCce = Parent.PositionCce + (Owner.PositionCci + CameraOffsetCci).Transform(Parent.GetCci2Cce());
            Universe.CurrentSystem = System;
            Universe.Time = 0;
            KSA.Program.ControlledVehicle = Owner;
            KSA.Program.EditorFlag = false;
            KSA.Program.Camera = Camera;
            ClutterForceTargets.Candidates = [];
            ClutterForceTargets.QueryCalls = 0;
            ClutterForceTargets.NearQueryCalls = 0;
            ClutterForceTargets.LastTolerance = double.NaN;
            ClutterForceTargets.LastRay = default;
        }

        internal Ray AimRay() => new()
        {
            Origin = Camera.GetPositionEgo(Owner) + CameraOffsetCci.Transform(Parent.GetCci2Cce()),
            Direction = double3.NormalizeOrZero(TargetOffsetCci - CameraOffsetCci).Transform(Parent.GetCci2Cce()),
        };
    }

    private static ForcePick Pick(Fixture fixture, ForcePickRay snapshot, string message)
    {
        Check(ForceTargeting.TryPick(fixture.Owner, snapshot, out var pick, out var reason), message + ": " + reason);
        return pick!;
    }

    private static void Equal(double3 actual, double3 expected, string message) =>
        Check((actual - expected).Length() < 1e-6, message + $"; actual {actual}; expected {expected}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Force targeting regression: " + message);
    }
}
