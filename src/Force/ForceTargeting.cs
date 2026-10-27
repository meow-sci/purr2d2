using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal readonly record struct ForcePickRay(CelestialSystem System, IParentBody Parent, Ray OwnerRelativeCci);
internal readonly record struct ForceHoverPreview(Vehicle Vehicle, Part Part, double3 HitEgo);

/// <summary>Capture position plus render anchors, so the UI never reads native clutter state.</summary>
internal sealed record ForcePick(Vehicle? Vehicle, Part? Part, ClutterForceTarget? Clutter,
    IParentBody Parent, CelestialSystem System, double3 HitBody, double3 HitCci)
{
    internal double3? HitCcf { get; init; }

    internal ForcePick(Vehicle vehicle, Part part, IParentBody parent, CelestialSystem system, double3 hitBody)
        : this(vehicle, part, null, parent, system, hitBody,
            vehicle.GetPositionCci() + hitBody.Transform(vehicle.GetBody2Cci())) { }

    internal string DisplayName => Vehicle?.Id ?? Clutter?.DisplayName ?? "Unavailable object";
}

/// <summary>Settled flight picking against native vessel meshes and physical ground-clutter colliders.</summary>
internal static partial class ForceTargeting
{
    internal const double MaximumRange = 1000;
    internal const int MaximumVehicles = 2048;
    internal const int MaximumParts = 32768;

    internal static bool IsLive(Vehicle vehicle) => Universe.CurrentSystem is { } system && !vehicle.IsDisposed
        && !vehicle.IsEditedVehicle && ReferenceEquals(vehicle.System, system)
        && system.All.TryGet(vehicle.Id, out var registered) && ReferenceEquals(registered, vehicle);

    internal static bool Valid(KittenEva owner, ForcePick pick) => IsLive(owner)
        && ReferenceEquals(owner, Program.ControlledVehicle) && !Program.EditorFlag
        && ReferenceEquals(pick.System, Universe.CurrentSystem) && ReferenceEquals(pick.Parent, owner.Parent)
        && ForceMath.Finite(pick.HitCci) && ForceMath.Finite(pick.HitBody)
        && (pick.HitCci - owner.GetPositionCci()).Length() <= MaximumRange
        && (pick.Vehicle is { } vehicle && pick.Clutter == null && pick.Part is { } part
            && IsLive(vehicle) && vehicle is not KittenEva && !ReferenceEquals(vehicle, owner)
            && ReferenceEquals(pick.Parent, vehicle.Parent) && ReferenceEquals(part.Tree?.OwningVehicle, vehicle)
            || pick.Vehicle == null && pick.Part == null && pick.Clutter is { IsValid: true } clutter
                && clutter.IsLocalTo(owner) && ReferenceEquals(clutter.Parent, pick.Parent)
                && ReferenceEquals(clutter.System, pick.System));

    /// <summary>UI-safe: freeze camera aim relative to the owner, with CCI-oriented axes.</summary>
    internal static bool TrySnapshotRay(KittenEva owner, Ray egoRay, out ForcePickRay snapshot)
    {
        snapshot = default;
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || Program.GetMainCamera() is not { } camera || !ForceMath.Finite(egoRay.Origin)
            || !ForceMath.Finite(egoRay.Direction) || egoRay.Direction.LengthSquared() < .5) return false;
        var rotation = owner.Parent.GetCce2Cci();
        // The query runs after the next physics apply. An absolute world ray is
        // left behind by orbital motion while the camera and nearby vessel follow
        // the kitten. Preserve the camera offset, but not its old world position.
        var origin = (egoRay.Origin - camera.GetPositionEgo(owner)).Transform(rotation);
        var direction = double3.NormalizeOrZero(egoRay.Direction.Transform(rotation));
        if (!ForceMath.Finite(origin) || !ForceMath.Finite(direction) || direction.LengthSquared() < .5) return false;
        snapshot = new(owner.System, owner.Parent, new Ray { Origin = origin, Direction = direction });
        return true;
    }

    /// <summary>
    /// Reconstruct a rendered native-surface hit for hover feedback. This uses only the
    /// current mesh candidate, ray and rendered camera frame; settled TryPick remains the
    /// authority for every capture and performs its own current native query.
    /// </summary>
    internal static bool TryPreviewNativeHit(KittenEva owner, Part part, double distance, Ray egoRay,
        Camera camera, out ForceHoverPreview preview)
    {
        preview = default;
        if (part is null || !double.IsFinite(distance) || distance < 0
            || !ForceMath.Finite(egoRay.Origin) || !TryNormalizeDirection(egoRay.Direction, out var direction)
            || !IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag)
            return false;

        Part? structural = part.StructuralPart;
        if (structural is null) return false;
        PartTree? tree = structural.Tree;
        if (part.IsAttachedInternal || structural.IsAttachedInternal
            || tree?.OwningVehicle is not { } vehicle
            || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle)
            || !IsLive(vehicle) || vehicle is KittenEva || ReferenceEquals(vehicle, owner)
            || !ReferenceEquals(vehicle.System, owner.System) || !ReferenceEquals(vehicle.Parent, owner.Parent))
            return false;

        double3 hitEgo = egoRay.Origin + direction * distance;
        double3 ownerEgo = camera.GetPositionEgo(owner);
        if (!ForceMath.Finite(hitEgo) || !ForceMath.Finite(ownerEgo)) return false;
        double3 ownerToHit = hitEgo - ownerEgo;
        double distanceSquared = ownerToHit.LengthSquared();
        if (!ForceMath.Finite(ownerToHit) || !double.IsFinite(distanceSquared)
            || distanceSquared > MaximumRange * MaximumRange)
            return false;

        preview = new(vehicle, structural, hitEgo);
        return true;
    }

    private static bool TryNormalizeDirection(double3 direction, out double3 normalized)
    {
        normalized = double3.Zero;
        if (!ForceMath.Finite(direction)) return false;
        double scale = Math.Max(Math.Abs(direction.X), Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
        if (!double.IsFinite(scale) || scale <= 0) return false;
        double3 scaled = direction / scale;
        double lengthSquared = scaled.LengthSquared();
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0) return false;
        normalized = scaled / Math.Sqrt(lengthSquared);
        return ForceMath.Finite(normalized) && double.IsFinite(normalized.LengthSquared())
            && normalized.LengthSquared() >= .5;
    }

    // Compatibility entry point; the actual native query must still be called only in the settled handoff.
    internal static bool TryPick(KittenEva owner, Ray ray, out ForcePick? pick, out string reason,
        double clutterTolerance = .2)
    {
        pick = null; reason = "Point at a vessel, debris, rock or tree trunk.";
        return TrySnapshotRay(owner, ray, out var snapshot) && TryPick(owner, snapshot, out pick, out reason, clutterTolerance);
    }

    /// <summary>Settled-thread query across native vessel meshes and physical clutter colliders.</summary>
    internal static bool TryPick(KittenEva owner, ForcePickRay snapshot, out ForcePick? pick, out string reason,
        double clutterTolerance = .2)
    {
        pick = null; reason = "Point at a vessel, debris, rock or tree trunk.";
        if (!double.IsFinite(clutterTolerance) || clutterTolerance < 0 || clutterTolerance > .5)
        { reason = "Clutter aim tolerance must be between 0 and 0.5 m."; return false; }
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || !ReferenceEquals(snapshot.System, Universe.CurrentSystem) || !ReferenceEquals(snapshot.Parent, owner.Parent)
            || Program.GetMainCamera() is not { } camera || !ForceMath.Finite(snapshot.OwnerRelativeCci.Origin)
            || !ForceMath.Finite(snapshot.OwnerRelativeCci.Direction) || snapshot.OwnerRelativeCci.Direction.LengthSquared() < .5) return false;
        var rotation = owner.Parent.GetCci2Cce();
        var rayCci = snapshot.OwnerRelativeCci;
        rayCci.Origin += owner.GetPositionCci();
        rayCci.Direction = double3.NormalizeOrZero(rayCci.Direction);
        var ray = new Ray
        {
            // Use one current owner-relative frame for every native mesh, rather
            // than coupling a deferred query to camera-follow/bubble shortcuts.
            Origin = snapshot.OwnerRelativeCci.Origin.Transform(rotation),
            Direction = double3.NormalizeOrZero(rayCci.Direction.Transform(rotation))
        };
        if (!ForceMath.Finite(ray.Origin) || !ForceMath.Finite(ray.Direction) || ray.Direction.LengthSquared() < .5) return false;
        // Ray distances start at the camera, but Force range starts at the kitten.
        // A zoomed-out orbital camera must still be able to pick a nearby vessel.
        // TryCciPick applies the actual owner-to-surface limit after the nearest hit.
        double best = snapshot.OwnerRelativeCci.Origin.Length() + MaximumRange;
        if (!double.IsFinite(best)) return false;
        Part? bestPart = null;
        ClutterForceTarget? bestClutter = null;
        double3 clutterHit = default;
        if (ClutterForceTargets.TryPick(owner, rayCci, out var clutter, out var clutterDistance, out var hitCci, out var clutterReason))
        {
            if (clutter != null && double.IsFinite(clutterDistance) && clutterDistance >= 0 && clutterDistance < best
                && ForceMath.Finite(hitCci) && (hitCci - owner.GetPositionCci()).Length() <= MaximumRange)
            { best = clutterDistance; bestClutter = clutter; clutterHit = hitCci; }
        }
        else if (!string.IsNullOrEmpty(clutterReason)) { reason = clutterReason; return false; }
        bool kittenBlocked = false;
        bool nearClutter = false;
        int vehicles = 0, parts = 0;
        foreach (var item in Universe.CurrentSystem!.All.AsSpan())
        {
            if (item is not Vehicle vehicle || ReferenceEquals(vehicle, owner) || !IsLive(vehicle)
                || !ReferenceEquals(vehicle.Parent, owner.Parent)) continue;
            if (++vehicles > MaximumVehicles) { reason = "Too many objects for a safe Furce pick."; return false; }
            var position = (vehicle.GetPositionCci() - owner.GetPositionCci()).Transform(rotation);
            double radius = vehicle.BoundingSphereRadiusBody;
            if (!ForceMath.Finite(position) || !double.IsFinite(radius) || radius <= 0
                || !ray.Raycast(new BoundingSphere3D(position, radius), out _, out _)) continue;
            if (vehicle is KittenEva)
            {
                if (ray.Raycast(new BoundingSphere3D(position, radius), out var distance, out _) && distance < best)
                { best = distance; bestPart = null; bestClutter = null; kittenBlocked = true; }
                continue;
            }
            var matrix = vehicle.GetMatrixAsmb2Ego(position);
            foreach (var part in vehicle.Parts.Parts)
            {
                if (++parts > MaximumParts) { reason = "Nearby geometry exceeds the Furce pick budget."; return false; }
                if (part.IsAttachedInternal || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle)) continue;
                if (part.RayCastEgo(in matrix, ray, out double distance, out _, out _, out _, out _, out _, out _, out _)
                    && distance >= 0 && distance < best)
                { best = distance; bestPart = part; bestClutter = null; kittenBlocked = false; }
            }
        }
        // Exact mesh/collider hits win. Only assist a clear near miss, so widening
        // the clutter query cannot steal a directly aimed vessel, rock or kitten.
        if (bestPart == null && bestClutter == null && !kittenBlocked && clutterTolerance > 0)
        {
            if (ClutterForceTargets.TryPickNear(owner, rayCci, clutterTolerance,
                out var nearTarget, out double nearDistance, out var nearHit, out string nearReason))
            {
                if (nearTarget != null && double.IsFinite(nearDistance) && nearDistance >= 0 && nearDistance < best
                    && ForceMath.Finite(nearHit) && (nearHit - owner.GetPositionCci()).Length() <= MaximumRange)
                { bestClutter = nearTarget; clutterHit = nearHit; best = nearDistance; nearClutter = true; }
            }
            else if (!string.IsNullOrEmpty(nearReason)) { reason = nearReason; return false; }
        }
        if (owner.Parent is Celestial body && ReferenceEquals(camera.NearbyCelestial, body)
            && (TerrainBlocks(rayCci, body, best) || nearClutter && TerrainBlocks(new Ray
            {
                Origin = rayCci.Origin,
                Direction = double3.NormalizeOrZero(clutterHit - rayCci.Origin)
            }, body, (clutterHit - rayCci.Origin).Length())))
        { reason = "Terrain cannot be captured; point at a vessel, debris or physical ground object."; return false; }
        if (bestClutter != null)
        {
            // Ground colliders rotate with the celestial. Freeze their surface
            // anchor in CCF, not an inertial position that trails Earth between
            // the throttled queries. Native bubble state is only read here.
            pick = new(null, null, bestClutter, owner.Parent, owner.System, default, clutterHit)
            {
                HitCcf = clutterHit.Transform(owner.Parent.GetCci2Ccf(bestClutter.Bubble.Origin.Time))
            };
            reason = pick.DisplayName;
            return true;
        }
        if (bestPart == null) { if (kittenBlocked) reason = "Kittens cannot be captured with the Furce."; return false; }
        return TryCciPick(owner, bestPart, rayCci.Origin + rayCci.Direction * best, out pick, out reason);
    }

    private static bool TryCciPick(KittenEva owner, Part part, double3 hitCci, out ForcePick? pick, out string reason)
    {
        pick = null; reason = "The selected object is no longer available.";
        if (part.StructuralPart.Tree?.OwningVehicle is not { } vehicle
            || !ForceMath.Finite(hitCci) || !IsLive(owner) || !IsLive(vehicle) || vehicle is KittenEva
            || ReferenceEquals(vehicle, owner) || !ReferenceEquals(vehicle.Parent, owner.Parent)
            || Program.EditorFlag || !ReferenceEquals(owner, Program.ControlledVehicle)) return false;
        if ((hitCci - owner.GetPositionCci()).Length() > MaximumRange)
        { reason = "The selected surface is beyond Furce range (1000 m)."; return false; }
        var local = (hitCci - vehicle.GetPositionCci()).Transform(vehicle.GetBody2Cci().Inverse());
        if (!ForceMath.Finite(local)) return false;
        pick = new(vehicle, part.StructuralPart, vehicle.Parent, vehicle.System, local);
        reason = pick.DisplayName;
        return true;
    }

    internal static bool TryHitEgo(ForcePick pick, Camera camera, out double3 hitEgo)
    {
        hitEgo = default;
        if (!ReferenceEquals(pick.System, Universe.CurrentSystem) || !ForceMath.Finite(pick.HitCci)) return false;
        if (pick.Vehicle is { } vehicle)
        {
            if (!IsLive(vehicle) || !ReferenceEquals(vehicle.Parent, pick.Parent)
                || !ReferenceEquals(pick.Part?.Tree?.OwningVehicle, vehicle) || !ForceMath.Finite(pick.HitBody)) return false;
            // Follow the picked surface between throttled queries instead of
            // leaving its marker at the previous orbital position. These are
            // applied render transforms; no live clutter physics is read here.
            hitEgo = camera.GetPositionEgo(vehicle) + pick.HitBody.Transform(vehicle.Body2Ego);
        }
        else if (pick.HitCcf is { } fixedHit)
            hitEgo = camera.GetPositionEgo(pick.Parent) + fixedHit.Transform(pick.Parent.GetCcf2Cce());
        else
            hitEgo = camera.GetPositionEgo(pick.Parent) + pick.HitCci.Transform(pick.Parent.GetCci2Cce());
        return ForceMath.Finite(hitEgo);
    }

    private static bool TerrainBlocks(Ray rayCci, Celestial body, double range)
    {
        // Use the same current CCI ray for clutter and terrain occlusion, without
        // a second camera-relative conversion or a dependency on its follow mode.
        var rotation = body.GetCci2Ccf();
        var origin = rayCci.Origin.Transform(rotation);
        var direction = rayCci.Direction.Transform(rotation);
        double previous = Depth(origin);
        if (!double.IsFinite(previous)) return false;
        if (previous <= 0) return true;
        for (int i = 1; i <= 128; i++)
        {
            double fraction = i / 128.0;
            double distance = range * fraction * fraction;
            double depth = Depth(origin + direction * distance);
            if (double.IsFinite(depth) && depth <= 0) return true;
        }
        return false;
        double Depth(double3 point)
        {
            double radius = point.Length();
            return radius > 0 && double.IsFinite(radius)
                ? radius - body.MeanRadius - body.GetTerrainHeightFromDirCcf(point / radius, accurate: true)
                : double.NaN;
        }
    }
}
