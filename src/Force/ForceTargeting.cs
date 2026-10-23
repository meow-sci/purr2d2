using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal readonly record struct ForcePickRay(CelestialSystem System, IParentBody Parent, Ray Cci);

/// <summary>The hit point is an immutable CCI snapshot, so the UI never reads native clutter state.</summary>
internal sealed record ForcePick(Vehicle? Vehicle, Part? Part, ClutterForceTarget? Clutter,
    IParentBody Parent, CelestialSystem System, double3 HitBody, double3 HitCci)
{
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

    /// <summary>UI-safe: freeze the ray in the owner's parent frame before the camera moves again.</summary>
    internal static bool TrySnapshotRay(KittenEva owner, Ray egoRay, out ForcePickRay snapshot)
    {
        snapshot = default;
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || Program.GetMainCamera() is not { } camera || !ForceMath.Finite(egoRay.Origin)
            || !ForceMath.Finite(egoRay.Direction) || egoRay.Direction.LengthSquared() < .5) return false;
        var rotation = owner.Parent.GetCce2Cci();
        var origin = owner.GetPositionCci() + (egoRay.Origin - camera.GetPositionEgo(owner)).Transform(rotation);
        var direction = double3.NormalizeOrZero(egoRay.Direction.Transform(rotation));
        if (!ForceMath.Finite(origin) || !ForceMath.Finite(direction) || direction.LengthSquared() < .5) return false;
        snapshot = new(owner.System, owner.Parent, new Ray { Origin = origin, Direction = direction });
        return true;
    }

    // Compatibility entry point; the actual native query must still be called only in the settled handoff.
    internal static bool TryPick(KittenEva owner, Ray ray, out ForcePick? pick, out string reason)
    {
        pick = null; reason = "Point at a vessel, debris or physical ground object.";
        return TrySnapshotRay(owner, ray, out var snapshot) && TryPick(owner, snapshot, out pick, out reason);
    }

    /// <summary>Settled-thread query across native vessel meshes and physical clutter colliders.</summary>
    internal static bool TryPick(KittenEva owner, ForcePickRay snapshot, out ForcePick? pick, out string reason)
    {
        pick = null; reason = "Point at a vessel, debris or physical ground object.";
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || !ReferenceEquals(snapshot.System, Universe.CurrentSystem) || !ReferenceEquals(snapshot.Parent, owner.Parent)
            || Program.GetMainCamera() is not { } camera || !ForceMath.Finite(snapshot.Cci.Origin)
            || !ForceMath.Finite(snapshot.Cci.Direction) || snapshot.Cci.Direction.LengthSquared() < .5) return false;
        var rotation = owner.Parent.GetCci2Cce();
        var rayCci = snapshot.Cci;
        rayCci.Direction = double3.NormalizeOrZero(rayCci.Direction);
        var ray = new Ray
        {
            Origin = camera.GetPositionEgo(owner) + (rayCci.Origin - owner.GetPositionCci()).Transform(rotation),
            Direction = double3.NormalizeOrZero(rayCci.Direction.Transform(rotation))
        };
        if (!ForceMath.Finite(ray.Origin) || !ForceMath.Finite(ray.Direction) || ray.Direction.LengthSquared() < .5) return false;
        double best = MaximumRange;
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
        int vehicles = 0, parts = 0;
        foreach (var item in Universe.CurrentSystem!.All.AsSpan())
        {
            if (item is not Vehicle vehicle || ReferenceEquals(vehicle, owner) || !IsLive(vehicle)
                || !ReferenceEquals(vehicle.Parent, owner.Parent)) continue;
            if (++vehicles > MaximumVehicles) { reason = "Too many objects for a safe Force pick."; return false; }
            var position = camera.GetPositionEgo(vehicle);
            double radius = vehicle.BoundingSphereRadiusBody;
            if (!ForceMath.Finite(position) || !double.IsFinite(radius) || radius <= 0
                || !ray.Raycast(new BoundingSphere3D(position, radius), out _, out _)) continue;
            if (vehicle is KittenEva)
            {
                if (ray.Raycast(new BoundingSphere3D(position, radius), out var distance, out _) && distance < best)
                { best = distance; bestPart = null; bestClutter = null; kittenBlocked = true; }
                continue;
            }
            var matrix = vehicle.GetMatrixAsmb2Ego(camera);
            foreach (var part in vehicle.Parts.Parts)
            {
                if (++parts > MaximumParts) { reason = "Nearby geometry exceeds the Force pick budget."; return false; }
                if (part.IsAttachedInternal || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle)) continue;
                if (part.RayCastEgo(in matrix, ray, out double distance, out _, out _, out _, out _, out _, out _, out _)
                    && distance >= 0 && distance < best)
                { best = distance; bestPart = part; bestClutter = null; kittenBlocked = false; }
            }
        }
        if (owner.Parent is Celestial body && ReferenceEquals(camera.NearbyCelestial, body)
            && TerrainBlocks(camera, ray, body, best))
        { reason = "Terrain cannot be captured; point at a vessel, debris or physical ground object."; return false; }
        if (bestClutter != null)
        {
            pick = new(null, null, bestClutter, owner.Parent, owner.System, default, clutterHit);
            reason = pick.DisplayName;
            return true;
        }
        if (bestPart == null) { if (kittenBlocked) reason = "Kittens cannot be captured with Force."; return false; }
        return TryNativePick(owner, bestPart, ray.Origin + ray.Direction * best, out pick, out reason);
    }

    internal static bool TryNativePick(KittenEva owner, Part part, double3 hitEgo, out ForcePick? pick, out string reason)
    {
        pick = null; reason = "The selected object is no longer available.";
        if (part.StructuralPart.Tree?.OwningVehicle is not { } vehicle || Program.GetMainCamera() is not { } camera
            || !ForceMath.Finite(hitEgo) || !IsLive(owner) || !IsLive(vehicle) || vehicle is KittenEva
            || ReferenceEquals(vehicle, owner) || !ReferenceEquals(vehicle.Parent, owner.Parent)
            || Program.EditorFlag || !ReferenceEquals(owner, Program.ControlledVehicle)) return false;
        if ((hitEgo - camera.GetPositionEgo(owner)).Length() > MaximumRange)
        { reason = "The selected surface is beyond Force range (1000 m)."; return false; }
        var local = (hitEgo - camera.GetPositionEgo(vehicle)).Transform(vehicle.Body2Ego.Inverse());
        if (!ForceMath.Finite(local)) return false;
        pick = new(vehicle, part.StructuralPart, vehicle.Parent, vehicle.System, local);
        reason = pick.DisplayName;
        return true;
    }

    internal static bool TryHitEgo(ForcePick pick, Camera camera, out double3 hitEgo)
    {
        hitEgo = default;
        if (!ReferenceEquals(pick.System, Universe.CurrentSystem) || !ForceMath.Finite(pick.HitCci)) return false;
        hitEgo = camera.GetPositionEgo(pick.Parent) + pick.HitCci.Transform(pick.Parent.GetCci2Cce());
        return ForceMath.Finite(hitEgo);
    }

    private static bool TerrainBlocks(Camera camera, Ray ray, Celestial body, double range)
    {
        var rotation = body.GetCce2Ccf();
        var origin = (ray.Origin - camera.GetPositionEgo(body)).Transform(rotation);
        var direction = ray.Direction.Transform(rotation);
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
