using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal sealed record ForcePick(Vehicle Vehicle, Part Part, IParentBody Parent, CelestialSystem System, double3 HitBody);

/// <summary>Read-only flight picking. A sphere is only broad phase; native part geometry determines the hit.</summary>
internal static partial class ForceTargeting
{
    internal const double MaximumRange = 1000;
    internal const int MaximumVehicles = 2048;
    internal const int MaximumParts = 32768;
    internal const string ClutterUnsupported = "Select a vessel or debris. Ground clutter cannot be captured yet.";

    internal static bool IsLive(Vehicle vehicle) => Universe.CurrentSystem is { } system && !vehicle.IsDisposed
        && !vehicle.IsEditedVehicle && ReferenceEquals(vehicle.System, system)
        && system.All.TryGet(vehicle.Id, out var registered) && ReferenceEquals(registered, vehicle);

    internal static bool Valid(KittenEva owner, ForcePick pick) => IsLive(owner) && IsLive(pick.Vehicle)
        && ReferenceEquals(owner, Program.ControlledVehicle) && !Program.EditorFlag && pick.Vehicle is not KittenEva
        && ReferenceEquals(pick.System, Universe.CurrentSystem) && ReferenceEquals(pick.Parent, owner.Parent)
        && ReferenceEquals(pick.Parent, pick.Vehicle.Parent) && ReferenceEquals(pick.Part.Tree?.OwningVehicle, pick.Vehicle)
        && ForceMath.Finite(pick.HitBody);

    internal static bool TryPick(KittenEva owner, Ray ray, out ForcePick? pick, out string reason)
    {
        pick = null; reason = "Point at a vessel or debris.";
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || Program.GetMainCamera() is not { } camera || !ForceMath.Finite(ray.Origin)
            || !ForceMath.Finite(ray.Direction) || ray.Direction.LengthSquared() < .5) return false;
        double best = MaximumRange;
        Part? bestPart = null;
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
                { best = distance; bestPart = null; kittenBlocked = true; }
                continue;
            }
            var matrix = vehicle.GetMatrixAsmb2Ego(camera);
            foreach (var part in vehicle.Parts.Parts)
            {
                if (++parts > MaximumParts) { reason = "Nearby geometry exceeds the Force pick budget."; return false; }
                if (part.IsAttachedInternal || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle)) continue;
                if (part.RayCastEgo(in matrix, ray, out double distance, out _, out _, out _, out _, out _, out _, out _)
                    && distance >= 0 && distance < best)
                { best = distance; bestPart = part; kittenBlocked = false; }
            }
        }
        if (owner.Parent is Celestial body && ReferenceEquals(camera.NearbyCelestial, body)
            && TerrainBlocks(camera, ray, body, best))
        { reason = "Terrain cannot be captured; point at a vessel or debris. Ground clutter is unsupported."; return false; }
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
        reason = vehicle.Id;
        return true;
    }

    internal static bool TryHitEgo(ForcePick pick, Camera camera, out double3 hitEgo)
    {
        hitEgo = default;
        if (!IsLive(pick.Vehicle) || !ReferenceEquals(pick.Vehicle.Parent, pick.Parent)
            || !ReferenceEquals(pick.Part.Tree?.OwningVehicle, pick.Vehicle)) return false;
        hitEgo = camera.GetPositionEgo(pick.Vehicle) + pick.HitBody.Transform(pick.Vehicle.Body2Ego);
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
