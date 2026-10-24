using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal static partial class ForceTargeting
{
    private const int MaximumProximityTriangles = 250_000;

    /// <summary>
    /// Settled-thread query around the primary hit: vessel render triangles and native physical clutter colliders.
    /// Clutter uses collider surface distance, which can differ from its visible mesh. Nothing mutates until all queries pass.
    /// </summary>
    internal static bool Gather(KittenEva owner, ForcePick pick, bool angry, List<ForceTarget> destination, out string reason,
        double angryRadius = ForceMath.DefaultAngryRadius)
    {
        destination.Clear();
        reason = "The selected object changed; pick again.";
        if (!Valid(owner, pick)) return false;
        if (angry && !ForceMath.ValidAngryRadius(angryRadius))
        { reason = "Angry Force radius must be between 1 and 50 m; nothing was captured."; return false; }
        var vesselIds = new HashSet<string>(StringComparer.Ordinal);
        var clutterIds = new HashSet<ClutterForceIdentity>();
        if (pick.Vehicle is { } primaryVehicle) { destination.Add(new(primaryVehicle)); vesselIds.Add(primaryVehicle.Id); }
        else if (pick.Clutter is { } primaryClutter) { destination.Add(new(primaryClutter)); clutterIds.Add(primaryClutter.StableKey); }
        else return false;
        if (!angry) return true;
        var hit = pick.HitCci;
        int triangles = 0, parts = 0, vehicles = 0;
        var meshes = new List<MeshReference>();
        foreach (var item in Universe.CurrentSystem!.All.AsSpan())
        {
            if (item is not Vehicle vehicle || vehicle is KittenEva || vesselIds.Contains(vehicle.Id)
                || !IsLive(vehicle) || !ReferenceEquals(vehicle.Parent, owner.Parent)) continue;
            if (++vehicles > MaximumVehicles) { reason = "Too many objects for a safe Angry Force capture."; return false; }
            var origin = vehicle.GetPositionCci();
            if (!ForceMath.Finite(origin) || (origin - hit).Length() > vehicle.BoundingSphereRadiusBody + angryRadius) continue;
            var query = (hit - origin).Transform(vehicle.GetBody2Cci().Inverse()) + vehicle.CenterOfMassAsmb;
            bool found = false;
            foreach (var full in vehicle.Parts.Parts)
            {
                if (full.IsAttachedInternal) continue;
                if (SurfaceNear(full)) { found = true; break; }
                foreach (var sub in full.SubParts)
                    if (SurfaceNear(sub)) { found = true; break; }
                if (found) break;
                if (triangles > MaximumProximityTriangles || parts > MaximumParts) break;
            }
            if (triangles > MaximumProximityTriangles || parts > MaximumParts)
            { reason = "Nearby geometry exceeds the Angry Force surface budget; nothing was captured."; return false; }
            if (!found) continue;
            if (destination.Count >= ForceController.MaximumCaptured)
            { reason = $"More than 32 objects are within {angryRadius:0.#} m; nothing was captured."; return false; }
            destination.Add(new(vehicle));
            vesselIds.Add(vehicle.Id);

            bool SurfaceNear(Part part)
            {
                if (++parts > MaximumParts || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle)) return false;
                var matrix = part.MatrixAsmb2VehicleAsmb;
                Collect(part, meshes);
                foreach (var mesh in meshes)
                {
                    if (mesh.PositionsCompare is not { } primitives) continue;
                    foreach (var vertices in primitives)
                    {
                        if (vertices == null) continue;
                        for (int i = 0; i + 2 < vertices.Length; i += 3)
                        {
                            if (++triangles > MaximumProximityTriangles) return false;
                            double distance = ForceMath.SurfaceDistanceSquared(query, vertices[i].Transform(matrix),
                                vertices[i + 1].Transform(matrix), vertices[i + 2].Transform(matrix));
                            if (distance <= angryRadius * angryRadius + 1e-9) return true;
                        }
                    }
                }
                return false;
            }
        }
        var clutterTargets = new List<ClutterForceTarget>();
        if (!ClutterForceTargets.GatherNear(owner, hit, angryRadius, clutterTargets,
            ForceController.MaximumCaptured, out reason)) return false;
        foreach (var clutter in clutterTargets)
        {
            if (!clutter.IsValid || !clutter.IsLocalTo(owner) || !ReferenceEquals(clutter.Parent, pick.Parent)
                || !ReferenceEquals(clutter.System, pick.System))
            { reason = "A nearby physical ground object changed; nothing was captured."; return false; }
            if (!clutterIds.Add(clutter.StableKey)) continue;
            if (destination.Count >= ForceController.MaximumCaptured)
            { reason = $"More than 32 objects are within {angryRadius:0.#} m; nothing was captured."; return false; }
            destination.Add(new(clutter));
        }
        return true;
    }

    private static void Collect(Part part, List<MeshReference> meshes)
    {
        meshes.Clear();
        foreach (var model in part.Modules.Get<PartModelModule>()) Add(model.PartModel.Template.Mesh?.Get());
        foreach (var model in part.Modules.Get<PartModelDynamicModule>()) Add(model.PartModelDynamic.Template.Mesh?.Get());
        foreach (var model in part.Modules.Get<PartModelGlassModule>()) Add(model.PartModelGlass.Template.Mesh?.Get());
        if (meshes.Count == 0) foreach (var model in part.Modules.Get<MeshViewModule>()) Add(model.MeshView);
        void Add(MeshReference? mesh) { if (mesh != null && !meshes.Contains(mesh)) meshes.Add(mesh); }
    }
}
