using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal static partial class ForceTargeting
{
    private const int MaximumProximityTriangles = 250_000;
    private readonly record struct ProximityCandidate(Vehicle? Vehicle, ClutterForceTarget? Clutter,
        double DistanceSquared, string StableOrder);
    private static readonly List<MeshReference> ProximityMeshes = new(64);
    private static readonly List<ProximityCandidate> ProximityCandidates = new(ForceTargeting.MaximumVehicles);
    private static readonly List<ClutterForceCandidate> ProximityClutterCandidates = new(ForceCaptureLimits.Maximum);
    private static readonly List<ForceTarget> InitialAdditionalTargets = new(ForceCaptureLimits.Maximum);

    /// <summary>
    /// Settled-thread query around the primary hit: vessel render triangles and native physical clutter colliders.
    /// Eligibility uses exact collider/triangle surface distance; overflow ranking compares candidate origins.
    /// Nothing mutates until all queries pass.
    /// </summary>
    internal static bool Gather(KittenEva owner, ForcePick pick, bool angry, List<ForceTarget> destination, out string reason,
        double angryRadius = ForceMath.DefaultAngryRadius, int captureLimit = ForceCaptureLimits.Default)
    {
        destination.Clear();
        reason = "The selected object changed; pick again.";
        if (!Valid(owner, pick)) return false;
        if (!ForceCaptureLimits.Valid(captureLimit))
        { reason = "The Furce capture limit must be between 1 and 512 objects; nothing was captured."; return false; }
        if (angry && !ForceMath.ValidAngryRadius(angryRadius))
        { reason = "Angry Furce radius must be between 1 and 50 m; nothing was captured."; return false; }
        var vesselIds = new HashSet<string>(StringComparer.Ordinal);
        var clutterIds = new HashSet<ClutterForceIdentity>();
        ForceTarget primary;
        if (pick.Vehicle is { } primaryVehicle) { primary = new(primaryVehicle); vesselIds.Add(primaryVehicle.Id); }
        else if (pick.Clutter is { } primaryClutter) { primary = new(primaryClutter); clutterIds.Add(primaryClutter.StableKey); }
        else return false;
        if (!angry || captureLimit == 1) { destination.Add(primary); return true; }
        InitialAdditionalTargets.Clear();
        try
        {
            if (!GatherNearby(owner, pick.HitCci, angryRadius, vesselIds, clutterIds,
                InitialAdditionalTargets, captureLimit - 1, out reason))
            { destination.Clear(); return false; }
            destination.Add(primary);
            destination.AddRange(InitialAdditionalTargets);
            return true;
        }
        finally { InitialAdditionalTargets.Clear(); }
    }

    /// <summary>
    /// Finds nearest eligible objects around the initial selected-point center. Already-selected identities are excluded
    /// before vessel surface traversal and before clutter's nearest-count result is capped.
    /// This synchronous method is called only in the settled BeforePhysics handoff.
    /// </summary>
    private static bool GatherNearby(KittenEva owner, double3 centerCci, double radius,
        HashSet<string> excludedVehicleIds, HashSet<ClutterForceIdentity> excludedClutterIds,
        List<ForceTarget> destination, int maximum, out string reason)
    {
        destination.Clear();
        reason = "The controlled kitten changed.";
        if (!IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag
            || !ForceMath.Finite(centerCci) || !ForceMath.ValidAngryRadius(radius)
            || maximum < 0 || maximum > ForceCaptureLimits.Maximum)
        { reason = "The Angry Furce query is invalid; nothing was captured."; return false; }
        if (maximum == 0) { reason = ""; return true; }

        var nearby = ProximityCandidates;
        nearby.Clear();
        var meshes = ProximityMeshes;
        meshes.Clear();
        var clutterCandidates = ProximityClutterCandidates;
        clutterCandidates.Clear();
        int triangles = 0, parts = 0, vehicles = 0;
        double radiusSquared = radius * radius;
        try
        {
            foreach (var item in Universe.CurrentSystem!.All.AsSpan())
            {
                if (item is not Vehicle vehicle || vehicle is KittenEva || excludedVehicleIds.Contains(vehicle.Id)
                    || !IsLive(vehicle) || !ReferenceEquals(vehicle.Parent, owner.Parent)) continue;
                if (++vehicles > MaximumVehicles)
                { reason = "Too many objects for a safe Angry Furce query; nothing was captured."; return false; }
                var origin = vehicle.GetPositionCci();
                double originDistanceSquared = (origin - centerCci).LengthSquared();
                double bound = vehicle.BoundingSphereRadiusBody + radius;
                if (!ForceMath.Finite(origin) || !double.IsFinite(originDistanceSquared) || !double.IsFinite(bound)
                    || bound <= 0 || originDistanceSquared > bound * bound) continue;
                var query = (centerCci - origin).Transform(vehicle.GetBody2Cci().Inverse()) + vehicle.CenterOfMassAsmb;
                bool found = false;
                foreach (var full in vehicle.Parts.Parts)
                {
                    if (full.IsAttachedInternal) continue;
                    if (SurfaceNear(vehicle, full, query, radiusSquared, meshes, ref triangles, ref parts, out bool budgetExceeded))
                    { found = true; break; }
                    if (budgetExceeded)
                    { reason = "Nearby geometry exceeds the Angry Furce surface budget; nothing was captured."; return false; }
                    foreach (var sub in full.SubParts)
                    {
                        if (SurfaceNear(vehicle, sub, query, radiusSquared, meshes, ref triangles, ref parts, out budgetExceeded))
                        { found = true; break; }
                        if (budgetExceeded)
                        { reason = "Nearby geometry exceeds the Angry Furce surface budget; nothing was captured."; return false; }
                    }
                    if (found) break;
                }
                if (found)
                {
                    excludedVehicleIds.Add(vehicle.Id);
                    nearby.Add(new(vehicle, null, originDistanceSquared, vehicle.Id));
                }
            }

            if (!ClutterForceTargets.GatherNear(owner, centerCci, radius, clutterCandidates,
                maximum, out reason, excludedClutterIds)) return false;
            foreach (var candidate in clutterCandidates)
            {
                var clutter = candidate.Target;
                if (!clutter.IsValid || !clutter.IsLocalTo(owner) || !ReferenceEquals(clutter.Parent, owner.Parent)
                    || !ReferenceEquals(clutter.System, owner.System))
                { reason = "A nearby physical ground object changed; nothing was added."; return false; }
                if (excludedClutterIds.Add(clutter.StableKey))
                    nearby.Add(new(null, clutter, candidate.DistanceSquared, clutter.StableOrder));
            }

            nearby.Sort(ProximityCandidateComparer.Instance);
            for (int i = 0, count = Math.Min(maximum, nearby.Count); i < count; i++)
            {
                var candidate = nearby[i];
                destination.Add(candidate.Vehicle is { } vehicle ? new ForceTarget(vehicle) : new ForceTarget(candidate.Clutter!));
            }
            reason = "";
            return true;
        }
        finally
        {
            nearby.Clear();
            meshes.Clear();
            clutterCandidates.Clear();
        }
    }

    private static bool SurfaceNear(Vehicle vehicle, Part part, double3 query, double radiusSquared,
        List<MeshReference> meshes, ref int triangles, ref int parts, out bool budgetExceeded)
    {
        budgetExceeded = false;
        if (++parts > MaximumParts || !ReferenceEquals(part.Tree?.OwningVehicle, vehicle))
        { budgetExceeded = parts > MaximumParts; return false; }
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
                    if (++triangles > MaximumProximityTriangles)
                    { budgetExceeded = true; return false; }
                    double distance = ForceMath.SurfaceDistanceSquared(query, vertices[i].Transform(matrix),
                        vertices[i + 1].Transform(matrix), vertices[i + 2].Transform(matrix));
                    if (distance <= radiusSquared + 1e-9) return true;
                }
            }
        }
        return false;
    }

    private static void Collect(Part part, List<MeshReference> meshes)
    {
        meshes.Clear();
        foreach (var model in part.Modules.Get<PartModelModule>()) AddMesh(meshes, model.PartModel.Template.Mesh?.Value);
        foreach (var model in part.Modules.Get<PartModelDynamicModule>()) AddMesh(meshes, model.PartModelDynamic.Template.Mesh?.Value);
        foreach (var model in part.Modules.Get<PartModelGlassModule>()) AddMesh(meshes, model.PartModelGlass.Template.Mesh?.Value);
        if (meshes.Count == 0) foreach (var model in part.Modules.Get<MeshViewModule>()) AddMesh(meshes, model.MeshView);
    }

    private static void AddMesh(List<MeshReference> meshes, MeshReference? mesh)
    { if (mesh != null && !meshes.Contains(mesh)) meshes.Add(mesh); }

    private sealed class ProximityCandidateComparer : IComparer<ProximityCandidate>
    {
        internal static readonly ProximityCandidateComparer Instance = new();
        public int Compare(ProximityCandidate left, ProximityCandidate right)
        {
            int distance = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (distance != 0) return distance;
            // Keep the existing stable policy: clutter sorts before vessels when distances tie.
            int type = (left.Clutter == null ? 1 : 0).CompareTo(right.Clutter == null ? 1 : 0);
            return type != 0 ? type : StringComparer.Ordinal.Compare(left.StableOrder, right.StableOrder);
        }
    }
}
