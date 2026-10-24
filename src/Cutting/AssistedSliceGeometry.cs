namespace Purr2D2.Cutting;

/// <summary>Bounded, repeatable variation followed by broad sections along the part's own axes.</summary>
internal static class AssistedSliceGeometry
{
    internal static IEnumerable<SlicePlane> Candidates(SliceFace[][] source, IReadOnlyList<CutPoint> axes, uint seed = 0)
    {
        var vertices = source.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
        if (vertices.Length == 0) yield break;
        var sections = axes.Select(a => a.Normalized).Where(a => a.Length > .9).Select(axis =>
        {
            double low = vertices.Min(v => CutPoint.Dot(v, axis));
            double high = vertices.Max(v => CutPoint.Dot(v, axis));
            return (axis, low, span: high - low);
        }).Where(s => double.IsFinite(s.span) && s.span > .01).OrderByDescending(s => s.span).ToArray();
        var random = new SliceRandom(seed);
        // Start near the broadest section, with enough tilt and offset to make successive
        // cuts visibly distinct without deliberately creating tiny fragments.
        foreach (var section in sections)
        {
            Basis(section.axis, out var u, out var v);
            for (int i = 0; i < 2; i++)
            {
                var normal = (section.axis + u * random.Signed(.12, .32) + v * random.Signed(.08, .26)).Normalized;
                double low = vertices.Min(p => CutPoint.Dot(p, normal)), high = vertices.Max(p => CutPoint.Dot(p, normal));
                yield return new(normal, low + (high - low) * (.39 + random.Next() * .22));
            }
        }
        // Authored compound trim can defeat particular oblique contours; keep a bounded
        // axis-aligned fallback set as well, especially for already cut thin fragments.
        foreach (var section in sections)
            foreach (double fraction in new[] { .43, .57, .5 })
                yield return new(section.axis, section.low + section.span * fraction);
    }

    internal static void Basis(CutPoint normal, out CutPoint u, out CutPoint v)
    {
        var helper = Math.Abs(normal.X) < .75 ? new CutPoint(1, 0, 0) : new CutPoint(0, 1, 0);
        u = CutPoint.Cross(normal, helper).Normalized; v = CutPoint.Cross(normal, u).Normalized;
    }

    internal struct SliceRandom(uint seed)
    {
        private uint _state = seed ^ 0xa511e9b3u;
        internal double Next()
        {
            _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
            return _state / 4294967296.0;
        }
        internal double Signed(double low, double high) => (Next() < .5 ? -1 : 1) * (low + (high - low) * Next());
    }
}

/// <summary>
/// A coarse zig-zag section, represented by an invertible piecewise-linear shear.
/// Split at the bends before either mapping so every surface and both complementary
/// caps share the same actual facets. The exterior is restored by the inverse map;
/// its interpolated texture coordinates remain attached to the original triangles.
/// </summary>
internal static class FacetedSliceGeometry
{
    private const int MaximumFaces = 120_000;
    private sealed class Profile(CutPoint normal, CutPoint across, double[] knots, double[] heights)
    {
        internal readonly CutPoint Normal = normal, Across = across;
        internal readonly double[] Knots = knots;
        internal (double Height, double Slope) At(double x)
        {
            int segment = 0;
            while (segment < Knots.Length - 2 && x > Knots[segment + 1]) segment++;
            double slope = (heights[segment + 1] - heights[segment]) / (Knots[segment + 1] - Knots[segment]);
            return (heights[segment] + (x - Knots[segment]) * slope, slope);
        }
    }

    internal static bool TrySlice(SliceFace[][] source, SlicePlane plane, uint seed,
        out PlaneSliceResult? result, out string reason, CancellationToken cancel = default)
    {
        result = null;
        try
        {
            var points = source.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
            if (points.Length == 0) { reason = "No geometry for a faceted section."; return false; }
            var normal = plane.Normal.Normalized;
            AssistedSliceGeometry.Basis(normal, out var u, out var v);
            var random = new AssistedSliceGeometry.SliceRandom(seed ^ 0x738a472du);
            double rotation = random.Next() * Math.PI;
            var across = u * Math.Cos(rotation) + v * Math.Sin(rotation);
            double low = points.Min(p => CutPoint.Dot(p, across)), high = points.Max(p => CutPoint.Dot(p, across));
            double depth = points.Max(p => CutPoint.Dot(p, normal)) - points.Min(p => CutPoint.Dot(p, normal));
            double span = high - low;
            if (span < .01 || depth < .01) { reason = "Section is too small for coarse bends."; return false; }
            // Four broad facets, with three irregularly spaced bends. Amplitude scales
            // down with the remaining fragment so third-generation cuts stay practical.
            double amplitude = Math.Min(span * .065, depth * .095) * (.8 + random.Next() * .4);
            double[] knots = [low, low + span * (.19 + random.Next() * .08), low + span * (.45 + random.Next() * .1), low + span * (.72 + random.Next() * .08), high];
            double sign = random.Next() < .5 ? -1 : 1;
            double[] heights = [0, amplitude * sign, -amplitude * sign * (.65 + random.Next() * .35), amplitude * sign * (.65 + random.Next() * .35), 0];
            var profile = new Profile(normal, across, knots, heights);
            var warped = new List<SliceFace[]>();
            foreach (var solid in source)
            {
                cancel.ThrowIfCancellationRequested();
                var split = Split(solid, profile, cancel);
                var mapped = Map(split, profile, inverse: false);
                if (!PlaneSlicer.TryAudit(mapped, out reason)) { reason = "Faceted forward map: " + reason; return false; }
                warped.Add(mapped);
                if (warped.Sum(s => s.Length) > MaximumFaces) throw new InvalidDataException("Faceted source exceeds the subdivision budget.");
            }
            if (!PlaneSlicer.TrySliceCompound(warped, plane, out var clipped, out reason, cancellation: cancel)) { reason = "Faceted clipping: " + reason; return false; }
            var pieces = new List<SlicePiece>();
            foreach (var piece in clipped!.Pieces)
            {
                var restored = new List<SliceFace[]>();
                foreach (var solid in piece.ClosedSolids ?? [piece.Faces])
                {
                    cancel.ThrowIfCancellationRequested();
                    var inverseSplit = Split(solid, profile, cancel);
                    if (!PlaneSlicer.TryAudit(inverseSplit, out reason)) {
                        reason = "Faceted inverse subdivision: " + reason; return false; }
                    var faces = Map(inverseSplit, profile, inverse: true);
                    if (!PlaneSlicer.TryAudit(faces, out reason)) { reason = "Faceted inverse map: " + reason; return false; }
                    restored.Add(faces);
                }
                var all = restored.SelectMany(s => s).ToArray();
                if (all.Length > 200_000) throw new InvalidDataException("Faceted output exceeds the subdivision budget.");
                var properties = PlaneSlicer.Properties(all);
                pieces.Add(new(all, properties.Volume, properties.Centroid, properties.Inertia, piece.PositiveSide) { ClosedSolids = restored.ToArray() });
            }
            double sourceVolume = source.Sum(s => PlaneSlicer.Properties(s).Volume);
            if (Math.Abs(pieces.Sum(p => p.Volume) - sourceVolume) > Math.Max(1e-10, sourceVolume * 2e-6))
            { reason = "Faceted section failed volume conservation."; return false; }
            result = new(pieces.ToArray(), clipped.EffectivePlane, sourceVolume, clipped.ContourCount, clipped.HoleCount);
            reason = ""; return true;
        }
        catch (InvalidDataException error) { reason = error.Message; return false; }
    }

    private static SliceFace[] Map(SliceFace[] source, Profile profile, bool inverse)
    {
        var result = new SliceFace[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var f = source[i];
            double slope = profile.At(CutPoint.Dot((f.A.Position + f.B.Position + f.C.Position) / 3, profile.Across)).Slope;
            SliceVertex Convert(SliceVertex vertex)
            {
                double height = profile.At(CutPoint.Dot(vertex.Position, profile.Across)).Height;
                double direction = inverse ? 1 : -1;
                var p = vertex.Position + profile.Normal * (height * direction);
                var n = (vertex.Normal - profile.Across * (direction * slope * CutPoint.Dot(vertex.Normal, profile.Normal))).Normalized;
                return vertex with { Position = p, Normal = n };
            }
            result[i] = new(Convert(f.A), Convert(f.B), Convert(f.C), f.Material);
        }
        return result;
    }

    private static SliceFace[] Split(SliceFace[] source, Profile profile, CancellationToken cancel)
    {
        var current = PlaneSlicer.CanonicalPositions(source);
        double epsilon = PlaneSlicer.TopologyTolerance(current);
        // One shared intersection position per geometric edge, including UV seams.
        foreach (double knot in profile.Knots.Skip(1).SkipLast(1))
        {
            var output = new List<SliceFace>(current.Length + 256);
            var intersections = new Dictionary<(CutPoint, CutPoint), CutPoint>();
            var vertices = new SliceVertex[3]; var distance = new double[3];
            var polygon = new List<SliceVertex>(4);
            SliceVertex Snap(SliceVertex vertex)
            {
                double d = CutPoint.Dot(vertex.Position, profile.Across) - knot;
                return Math.Abs(d) <= epsilon * 2 ? vertex with { Position = vertex.Position - profile.Across * d } : vertex;
            }
            for (int i = 0; i < current.Length; i++)
            {
                if ((i & 255) == 0) cancel.ThrowIfCancellationRequested();
                var f = current[i];
                vertices[0] = Snap(f.A); vertices[1] = Snap(f.B); vertices[2] = Snap(f.C);
                f = new(vertices[0], vertices[1], vertices[2], f.Material);
                double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
                for (int j = 0; j < 3; j++)
                {
                    double d = CutPoint.Dot(vertices[j].Position, profile.Across) - knot;
                    distance[j] = Math.Abs(d) < epsilon ? 0 : d;
                    minimum = Math.Min(minimum, distance[j]); maximum = Math.Max(maximum, distance[j]);
                }
                if (minimum >= 0 || maximum <= 0) { output.Add(f); continue; }
                Emit(true); Emit(false);
                void Emit(bool positive)
                {
                    polygon.Clear();
                    for (int edge = 0; edge < 3; edge++)
                    {
                        int next = (edge + 1) % 3;
                        var a = vertices[edge]; var b = vertices[next];
                        double da = distance[edge], db = distance[next];
                        bool inside = positive ? da >= 0 : da <= 0;
                        bool otherInside = positive ? db >= 0 : db <= 0;
                        if (inside) polygon.Add(a);
                        if (inside == otherInside) continue;
                        double t = da / (da - db);
                        if (da == 0 || db == 0) continue;
                        var key = Before(a.Position, b.Position) ? (a.Position, b.Position) : (b.Position, a.Position);
                        if (!intersections.TryGetValue(key, out var position))
                        {
                            var delta = key.Item2 - key.Item1;
                            position = key.Item1 + delta * ((knot - CutPoint.Dot(key.Item1, profile.Across)) / CutPoint.Dot(delta, profile.Across));
                            intersections[key] = position;
                        }
                        polygon.Add(SliceVertex.Lerp(a, b, t) with { Position = position });
                    }
                    if (polygon.Count < 3) return;
                    // A triangle/quad created by clipping has no extra collinear boundary
                    // vertices, so a simple fan adds at most two triangles per side.
                    for (int j = 1; j < polygon.Count - 1; j++)
                        if (CutPoint.Cross(polygon[j].Position - polygon[0].Position, polygon[j + 1].Position - polygon[0].Position).LengthSquared > 1e-24)
                            output.Add(new(polygon[0], polygon[j], polygon[j + 1], f.Material));
                }
                if (output.Count > MaximumFaces * 2) throw new InvalidDataException("Faceted subdivision exceeds the triangle budget.");
            }
            current = output.ToArray();
        }
        return current;
        static bool Before(CutPoint a, CutPoint b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
    }
}
