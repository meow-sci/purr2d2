namespace Purr2D2.Cutting;

/// <summary>Prefer broad, centered sections along the part's own axes, not a noisy contact plane.</summary>
internal static class AssistedSliceGeometry
{
    internal static IEnumerable<SlicePlane> Candidates(SliceFace[][] source, IReadOnlyList<CutPoint> axes)
    {
        var vertices = source.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
        if (vertices.Length == 0) yield break;
        var sections = axes.Select(a => a.Normalized).Where(a => a.Length > .9).Select(axis =>
        {
            double low = vertices.Min(v => CutPoint.Dot(v, axis));
            double high = vertices.Max(v => CutPoint.Dot(v, axis));
            return (axis, low, span: high - low);
        }).Where(s => double.IsFinite(s.span) && s.span > .01).OrderByDescending(s => s.span).ToArray();
        // Off-center fallbacks avoid coincident seams/crossing contours in authored compound art.
        foreach (var section in sections)
            foreach (double fraction in new[] { .5, .43, .57 })
                yield return new(section.axis, section.low + section.span * fraction);
    }
}
