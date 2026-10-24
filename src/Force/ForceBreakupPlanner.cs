using System.Threading;
using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Rendering;

namespace Purr2D2.Force;

/// <summary>One immutable source-part snapshot. Only the top-level part and each visual node's
/// copied authored data cross into the worker; no KSA objects are retained here.</summary>
internal sealed record ForceBreakupSourceNode(
    int FullPartIndex,
    int FirstLayerBranchIndex,
    bool IsFullPart,
    AuthoredDebrisNode? Geometry);

/// <summary>Pure grouping policy for detached source geometry. Full parts seed chunks first;
/// optional refinement peels only direct subpart branches and keeps their descendants together.</summary>
internal static class ForceBreakupPlanner
{
    internal sealed record PlannedPiece(AuthoredDebrisShape Shape, double3 Position, doubleQuat Rotation, double Weight);

    private sealed class FullPart(int index)
    {
        internal readonly int Index = index;
        internal readonly List<AuthoredDebrisNode> RootGeometry = new();
        internal readonly Dictionary<int, Branch> Branches = new();
        internal double Volume;
        internal double3 Center;
        internal bool HasGeometry;

        internal IEnumerable<AuthoredDebrisNode> Geometry()
        {
            foreach (var node in RootGeometry) yield return node;
            foreach (var branch in Branches.Values.OrderBy(branch => branch.Index))
                foreach (var node in branch.Geometry) yield return node;
        }
    }

    private sealed class Branch(int index)
    {
        internal readonly int Index = index;
        internal readonly List<AuthoredDebrisNode> Geometry = new();
        internal double Volume;
        internal bool ContainsInternal;
    }

    private sealed class PartGroup(FullPart part)
    {
        internal readonly FullPart Part = part;
        internal readonly HashSet<int> PeeledBranches = new();

        internal IEnumerable<AuthoredDebrisNode> RemainingGeometry()
        {
            foreach (var node in Part.RootGeometry) yield return node;
            foreach (var branch in Part.Branches.Values.OrderBy(branch => branch.Index))
            {
                if (PeeledBranches.Contains(branch.Index)) continue;
                foreach (var node in branch.Geometry) yield return node;
            }
        }
    }

    private sealed record BranchCluster(FullPart Part, int[] BranchIndexes,
        List<AuthoredDebrisNode> Geometry, double Volume);
    private sealed record GeometryGroup(List<FullPart> Parts, List<AuthoredDebrisNode> Geometry);

    private const double MinimumRefinementShare = .04;
    private const double MinimumPieceMass = .0002;

    internal static PlannedPiece[] Build(
        IReadOnlyList<ForceBreakupSourceNode> source,
        int maximumPieces,
        double dryMass,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        maximumPieces = ForceBreakupLimits.Clamp(maximumPieces);
        if (!double.IsFinite(dryMass) || dryMass <= 0)
            throw new InvalidDataException("Vehicle dry mass is invalid.");

        int topPartCount = 0;
        for (int i = 0; i < source.Count; i++)
        {
            if ((i & 63) == 0) cancel.ThrowIfCancellationRequested();
            if (source[i].IsFullPart) topPartCount = Math.Max(topPartCount, source[i].FullPartIndex + 1);
        }
        if (topPartCount == 0) throw new InvalidDataException("The vehicle has no structural parts to break up.");

        var parts = new FullPart[topPartCount];
        for (int i = 0; i < parts.Length; i++) parts[i] = new(i);
        var sourceKeys = new HashSet<int>();
        for (int i = 0; i < source.Count; i++)
        {
            if ((i & 63) == 0) cancel.ThrowIfCancellationRequested();
            var item = source[i];
            if ((uint)item.FullPartIndex >= (uint)parts.Length)
                throw new InvalidDataException("The captured vehicle hierarchy is inconsistent.");
            var part = parts[item.FullPartIndex];
            if (item.IsFullPart)
            {
                if (item.FirstLayerBranchIndex != -1)
                    throw new InvalidDataException("A structural part has an invalid branch identity.");
                if (item.Geometry is not null) part.RootGeometry.Add(item.Geometry);
                continue;
            }
            if (item.FirstLayerBranchIndex < 0)
                throw new InvalidDataException("An attached subpart has no structural parent branch.");
            if (!part.Branches.TryGetValue(item.FirstLayerBranchIndex, out var branch))
                part.Branches.Add(item.FirstLayerBranchIndex, branch = new(item.FirstLayerBranchIndex));
            if (item.Geometry is not null) branch.Geometry.Add(item.Geometry);
        }

        foreach (var part in parts)
        {
            cancel.ThrowIfCancellationRequested();
            part.HasGeometry = false;
            double totalVolume = 0;
            double3 weightedCenter = double3.Zero;
            foreach (var node in part.Geometry())
            {
                ValidateGeometryNode(node);
                if (!sourceKeys.Add(node.SourceIndex))
                    throw new InvalidDataException("Captured visual geometry appears more than once.");
                totalVolume += node.Volume;
                weightedCenter += node.BoundsCenter * node.Volume;
                part.HasGeometry = true;
            }
            if (part.HasGeometry)
            {
                if (!double.IsFinite(totalVolume) || totalVolume <= 0 || !Finite(weightedCenter))
                    throw new InvalidDataException("Captured visual geometry has invalid bounds.");
                part.Volume = totalVolume;
                part.Center = weightedCenter / totalVolume;
            }
            foreach (var branch in part.Branches.Values)
            {
                foreach (var node in branch.Geometry)
                {
                    branch.Volume += node.Volume;
                    branch.ContainsInternal |= node.AttachedInternal || node.Surfaces.Any(surface => surface.Internal);
                }
            }
        }

        var visibleParts = parts.Where(part => part.HasGeometry).ToArray();
        if (visibleParts.Length == 0)
            throw new InvalidDataException("The vehicle has no visible authored geometry to break up.");

        var geometryGroups = visibleParts.Length > maximumPieces
            ? CoarsenFullParts(visibleParts, maximumPieces, cancel)
            : RefineFirstLayer(visibleParts, maximumPieces, cancel);
        if (geometryGroups.Count is < 2 || geometryGroups.Count > maximumPieces)
            throw new InvalidDataException("The vehicle has fewer than two visible authored pieces.");

        var raw = new (AuthoredDebrisShape Shape, double Weight)[geometryGroups.Count];
        double totalWeight = 0;
        for (int i = 0; i < geometryGroups.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            raw[i] = BuildShape(geometryGroups[i], cancel);
            totalWeight += raw[i].Weight;
        }
        if (!double.IsFinite(totalWeight) || totalWeight <= 0
            || dryMass <= geometryGroups.Count * MinimumPieceMass)
            throw new InvalidDataException("The vehicle has insufficient dry mass for separate debris.");

        var planned = new PlannedPiece[raw.Length];
        double distributable = dryMass - raw.Length * MinimumPieceMass;
        int residualIndex = 0;
        for (int i = 1; i < raw.Length; i++)
            if (raw[i].Weight > raw[residualIndex].Weight) residualIndex = i;
        var masses = new float[raw.Length];
        double assigned = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (i == residualIndex) continue;
            float mass = (float)(MinimumPieceMass + distributable * raw[i].Weight / totalWeight);
            if (!float.IsFinite(mass) || mass <= 0)
                throw new InvalidDataException("The vehicle dry mass cannot be represented by native debris masses.");
            masses[i] = mass;
            assigned += mass;
        }
        masses[residualIndex] = (float)(dryMass - assigned);
        if (!float.IsFinite(masses[residualIndex]) || masses[residualIndex] <= 0)
            throw new InvalidDataException("The vehicle dry mass cannot be represented by native debris masses.");

        for (int i = 0; i < raw.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            float mass = masses[i];
            var shape = raw[i].Shape with { MassKg = mass };
            planned[i] = new(shape, shape.Center, doubleQuat.Identity, raw[i].Weight);
        }
        return planned;
    }

    private static List<GeometryGroup> CoarsenFullParts(FullPart[] parts, int maximumPieces, CancellationToken cancel)
    {
        // Keep the most substantial whole-part roots as the chunk anchors; nearby surplus
        // structural parts and all of their furnishing branches join their closest anchor.
        var anchors = parts.OrderByDescending(part => part.Volume).ThenBy(part => part.Index)
            .Take(maximumPieces).OrderBy(part => part.Index).ToArray();
        var anchorGroups = anchors.ToDictionary(part => part.Index, part => new List<FullPart> { part });
        var anchorCenters = anchors.ToDictionary(part => part.Index, part => part.Center);
        var anchorIndexes = anchors.Select(part => part.Index).ToHashSet();
        foreach (var part in parts.OrderBy(part => part.Index))
        {
            cancel.ThrowIfCancellationRequested();
            if (anchorIndexes.Contains(part.Index)) continue;
            int selected = -1;
            double nearest = double.PositiveInfinity;
            foreach (var anchor in anchors)
            {
                double distance = (part.Center - anchorCenters[anchor.Index]).LengthSquared();
                if (distance < nearest || distance == nearest && anchor.Index < selected)
                {
                    selected = anchor.Index;
                    nearest = distance;
                }
            }
            anchorGroups[selected].Add(part);
        }
        return anchorGroups.OrderBy(pair => pair.Key)
            .Select(pair => new GeometryGroup(pair.Value.OrderBy(part => part.Index).ToList(),
                pair.Value.SelectMany(part => part.Geometry()).OrderBy(node => node.SourceIndex).ToList()))
            .ToList();
    }

    private static List<GeometryGroup> RefineFirstLayer(FullPart[] parts, int maximumPieces, CancellationToken cancel)
    {
        var groups = parts.OrderBy(part => part.Index).Select(part => new PartGroup(part)).ToList();
        var candidates = new List<BranchCluster>();
        foreach (var part in parts)
        {
            cancel.ThrowIfCancellationRequested();
            double threshold = part.Volume * MinimumRefinementShare;
            double unpeeledVolume = part.Volume;
            double clusterVolume = 0;
            var clusterBranches = new List<Branch>();
            foreach (var branch in part.Branches.Values.OrderByDescending(branch => branch.Volume).ThenBy(branch => branch.Index))
            {
                cancel.ThrowIfCancellationRequested();
                if (branch.ContainsInternal || branch.Geometry.Count == 0) continue;
                clusterBranches.Add(branch);
                clusterVolume += branch.Volume;
                if (clusterVolume < threshold || unpeeledVolume - clusterVolume < threshold) continue;
                candidates.Add(new(part, clusterBranches.Select(item => item.Index).ToArray(),
                    clusterBranches.SelectMany(item => item.Geometry).OrderBy(node => node.SourceIndex).ToList(), clusterVolume));
                unpeeledVolume -= clusterVolume;
                clusterBranches.Clear();
                clusterVolume = 0;
            }
        }

        // Each peeled chunk contains one or more complete first-layer branches. Coalescing
        // small siblings keeps hundreds of trim nodes from becoming hundreds of debris
        // objects while still refining substantial portions of the original structure.
        candidates = candidates.OrderByDescending(candidate => candidate.Volume)
            .ThenBy(candidate => candidate.Part.Index).ThenBy(candidate => candidate.BranchIndexes[0]).ToList();
        int splitCapacity = maximumPieces - groups.Count;
        var selected = candidates.Take(Math.Max(0, splitCapacity)).ToArray();
        var peeledByPart = new Dictionary<int, HashSet<int>>();
        foreach (var cluster in selected)
        {
            if (!peeledByPart.TryGetValue(cluster.Part.Index, out var indexes))
                peeledByPart.Add(cluster.Part.Index, indexes = new());
            foreach (int index in cluster.BranchIndexes) indexes.Add(index);
        }
        foreach (var group in groups)
            if (peeledByPart.TryGetValue(group.Part.Index, out var peeled))
                foreach (int index in peeled) group.PeeledBranches.Add(index);

        if (groups.Count + selected.Length == 1 && groups.Count + selected.Length < maximumPieces)
        {
            cancel.ThrowIfCancellationRequested();
            var group = groups[0];
            var fallback = group.Part.Branches.Values
                .Where(branch => !branch.ContainsInternal && !group.PeeledBranches.Contains(branch.Index)
                    && branch.Geometry.Count > 0 && RemainingNodeCount(group) > branch.Geometry.Count)
                .OrderByDescending(branch => branch.Volume).ThenBy(branch => branch.Index).FirstOrDefault();
            if (fallback is not null)
            {
                group.PeeledBranches.Add(fallback.Index);
                selected = [.. selected, new(group.Part, [fallback.Index],
                    fallback.Geometry.OrderBy(node => node.SourceIndex).ToList(), fallback.Volume)];
            }
        }

        var result = new List<GeometryGroup>(groups.Count);
        foreach (var group in groups)
        {
            cancel.ThrowIfCancellationRequested();
            var remaining = group.RemainingGeometry().OrderBy(node => node.SourceIndex).ToList();
            if (remaining.Count > 0) result.Add(new([group.Part], remaining));
        }
        result.AddRange(selected.Select(candidate => new GeometryGroup([candidate.Part], candidate.Geometry)));
        return result;

        static int RemainingNodeCount(PartGroup group)
        {
            int count = group.Part.RootGeometry.Count;
            foreach (var branch in group.Part.Branches.Values)
                if (!group.PeeledBranches.Contains(branch.Index)) count += branch.Geometry.Count;
            return count;
        }
    }

    private static (AuthoredDebrisShape Shape, double Weight) BuildShape(GeometryGroup group, CancellationToken cancel)
    {
        if (group.Geometry.Count == 0) throw new InvalidDataException("A debris group lost all visible geometry.");
        double weight = 0;
        double3 weightedCenter = double3.Zero;
        var minimum = new double3(double.PositiveInfinity);
        var maximum = new double3(double.NegativeInfinity);
        foreach (var node in group.Geometry)
        {
            cancel.ThrowIfCancellationRequested();
            weight += node.Volume;
            weightedCenter += node.BoundsCenter * node.Volume;
            var half = node.BoundsExtents * .5;
            minimum = double3.Min(minimum, node.BoundsCenter - half);
            maximum = double3.Max(maximum, node.BoundsCenter + half);
        }
        if (!double.IsFinite(weight) || weight <= 0 || !Finite(weightedCenter)
            || !Finite(minimum) || !Finite(maximum))
            throw new InvalidDataException("A debris group has invalid geometry bounds.");
        var center = weightedCenter / weight;
        var extents = double3.Max(double3.Max(maximum - center, center - minimum) * 2, new double3(.002));
        var nodes = group.Geometry.OrderBy(node => node.SourceIndex).ToArray();
        var surfaces = nodes.SelectMany(node => node.Surfaces).ToArray();
        if (surfaces.Length == 0) throw new InvalidDataException("A debris group has no authored surfaces.");
        return (new AuthoredDebrisShape(surfaces, center, extents, 1, 0) { Nodes = nodes }, weight);
    }

    private static void ValidateGeometryNode(AuthoredDebrisNode node)
    {
        if (node.Surfaces.Length == 0 || !double.IsFinite(node.Volume) || node.Volume <= 0
            || !Finite(node.Position) || !Finite(node.Rotation) || !Finite(node.Scale)
            || node.Scale.X <= 0 || node.Scale.Y <= 0 || node.Scale.Z <= 0
            || !Finite(node.BoundsCenter) || !Finite(node.BoundsExtents)
            || node.BoundsExtents.X <= 0 || node.BoundsExtents.Y <= 0 || node.BoundsExtents.Z <= 0)
            throw new InvalidDataException("Captured visual geometry has invalid bounds or transforms.");
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static bool Finite(doubleQuat value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z) && double.IsFinite(value.W);
}
