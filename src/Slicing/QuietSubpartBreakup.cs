using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

/// <summary>Deterministic inert wreckage at authored subpart boundaries. No cloned
/// functional templates, failure events or probabilistic native debris shedding.</summary>
internal static class QuietSubpartBreakup
{
    internal static bool TryBuild(SliceInput input, out CpuSlice? result, out string reason, CancellationToken cancel)
    {
        result = null;
        reason = "No two valid authored subparts are available.";
        var geometry = input.Geometry;
        // Generated solids must never be reinterpreted as backed authored sheets.
        if (input.GeneratedClosedGeometry) { reason = "Generated closed geometry requires a validated mesh cut."; return false; }
        if (input.Groups.Length < 2 || !float.IsFinite(input.MassKg) || input.MassKg <= .001f) return false;
        if (!geometry.AllowsSource(input.Groups.Length, input.Groups.Sum(g => (long)g.Length)))
        { reason = $"Authored fallback exceeds the geometry budget ({geometry.SourceUsage(input.Groups.Length, input.Groups.Sum(g => (long)g.Length))}). {SliceGeometryBudget.SettingsHint}"; return false; }
        try
        {
            var authored = new List<(int Index, SliceFace[][] Solids, double Volume, CutPoint Centroid)>();
            long faces = 0; int solids = 0;
            for (int i = 0; i < input.Groups.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (!SliceShellBuilder.TryPrepare([input.Groups[i]], out var shells, out reason, cancel, geometry: geometry)
                    || !PlaneSlicer.TryAuditCompound(shells, out var volume, out int count, out reason, geometry)) return false;
                faces += shells.Sum(s => (long)s.Length); solids += count;
                if (!geometry.AllowsSource(solids, faces))
                { reason = $"Authored fallback exceeds the geometry budget ({geometry.SourceUsage(solids, faces)}). {SliceGeometryBudget.SettingsHint}"; return false; }
                var props = PlaneSlicer.Properties(shells.SelectMany(s => s).ToArray());
                authored.Add((i, shells, volume, props.Centroid));
            }
            double total = authored.Sum(a => a.Volume);
            // Tiny decorative islands travel with the nearest substantial subpart;
            // no visual faces or source dry mass are discarded to fit the limit.
            var anchors = authored.OrderByDescending(a => a.Volume).ThenBy(a => a.Index)
                .Where(a => input.MassKg * a.Volume / total > .0001).Take(8).ToArray();
            if (anchors.Length < 2) { reason = "Authored subparts fall below the native fragment mass floor."; return false; }
            var groups = anchors.Select(a => new List<SliceFace[]>(a.Solids)).ToArray();
            foreach (var subpart in authored.Where(a => !anchors.Any(anchor => anchor.Index == a.Index)))
            {
                int nearest = Enumerable.Range(0, anchors.Length)
                    .OrderBy(i => (anchors[i].Centroid - subpart.Centroid).LengthSquared).ThenBy(i => anchors[i].Index).First();
                groups[nearest].AddRange(subpart.Solids);
            }
            var centres = groups.Select(g => PlaneSlicer.Properties(g.SelectMany(s => s).ToArray()).Centroid).ToArray();
            var min = centres.Aggregate(CutPoint.Min); var max = centres.Aggregate(CutPoint.Max); var extent = max - min;
            CutPoint normal = extent.X >= extent.Y && extent.X >= extent.Z ? new(1, 0, 0)
                : extent.Y >= extent.Z ? new(0, 1, 0) : new(0, 0, 1);
            var order = Enumerable.Range(0, centres.Length).OrderBy(i => CutPoint.Dot(centres[i], normal)).ThenBy(i => anchors[i].Index).ToArray();
            var positive = order.Skip(order.Length / 2).ToHashSet();
            double offset = (CutPoint.Dot(centres[order[order.Length / 2 - 1]], normal)
                + CutPoint.Dot(centres[order[order.Length / 2]], normal)) / 2;
            var assets = new CpuFragmentAsset[groups.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var originalSolids = groups[i].ToArray();
                var all = originalSolids.SelectMany(s => s).ToArray(); var properties = PlaneSlicer.Properties(all);
                var piece = new SlicePiece(all, properties.Volume, properties.Centroid, properties.Inertia, positive.Contains(i)) { ClosedSolids = originalSolids };
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out reason)
                    || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var prepared, out var dimensions, out reason, cancel, geometry)) return false;
                var points = prepared.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct().Count();
                if (points > SliceResourceLimits.MaximumFragmentVertices
                    || FragmentAssetWriter.CountRenderSurfaces(prepared, input.Materials) > SliceResourceLimits.MaximumRenderSurfaces)
                { reason = $"Authored fallback exceeds the fragment export budget ({points:N0} / {SliceResourceLimits.MaximumFragmentVertices:N0} unique positions; maximum {SliceResourceLimits.MaximumRenderSurfaces:N0} render surfaces)."; return false; }
                float mass = (float)(input.MassKg * piece.Volume / total);
                if (!float.IsFinite(mass) || mass <= .0001f) { reason = "Authored fallback mass is invalid."; return false; }
                assets[i] = FragmentAssetWriter.Build(new(piece, frame, prepared, dimensions, mass), input.Materials, cancel, geometry);
            }
            result = new(new(normal, offset), assets) { AuthoredSubpartFallback = true };
            reason = ""; return true;
        }
        catch (InvalidDataException error) { reason = error.Message; return false; }
    }
}
