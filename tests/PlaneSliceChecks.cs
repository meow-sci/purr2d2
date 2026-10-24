using Purr2D2.Cutting;

internal static class PlaneSliceChecks
{
    public static void Run()
    {
        var box = Voxels(new[] { (0, 0, 0) });
        var boxProperties = PlaneSlicer.Properties(box);
        Near(boxProperties.Volume, 1, "closed box volume");
        Near(boxProperties.Centroid.X, .5, "box centroid");
        Near(boxProperties.Inertia.XX, 1.0 / 6, "box exact inertia");
        var cut = Slice(box, new(new(0, 0, 1), .5), "box divided at arbitrary plane");
        Check(cut.Pieces.Length == 2 && cut.ContourCount == 1 && cut.HoleCount == 0, "box produces two watertight solids");
        Check(box.Where(f => new[] { f.A.Position.Z, f.B.Position.Z, f.C.Position.Z }.All(z => z < .5)
                || new[] { f.A.Position.Z, f.B.Position.Z, f.C.Position.Z }.All(z => z > .5))
            .All(f => cut.Pieces.Any(p => p.Faces.Contains(f))), "uncut triangles retain their original tessellation across repeated slicing");
        foreach (var piece in cut.Pieces)
        {
            Near(piece.Volume, .5, "half box volume");
            Near(piece.Centroid.Z, piece.PositiveSide ? .75 : .25, "half box exact centroid");
            Near(piece.Inertia.XX, .5 * (1 + .25) / 12, "half box exact inertia");
            Near(CapArea(piece), 1, "half box exposed cap area");
            Check(piece.Faces.Any(f => f.Material == 10) && piece.Faces.Any(f => f.Material == 11), "original material partitions retained");
            foreach (var face in piece.Faces)
                foreach (var vertex in new[] { face.A, face.B, face.C })
                {
                    if (face.Material != 99) { Near(vertex.U, vertex.Position.X, "surface U interpolated"); Near(vertex.V, vertex.Position.Y, "surface V interpolated"); }
                    else Near(CutPoint.Dot(vertex.Normal, cut.EffectivePlane.Normal), piece.PositiveSide ? -1 : 1, "cap normals face out of each half");
                }
        }
        Slice(box, new(new(1, 2, 3), 2.5), "oblique plane with interpolated corner attributes");
        var grazing = Slice(box, new(new(1, 1, 0), 1), "plane through existing vertices is deterministically nudged");
        Check(Math.Abs(grazing.EffectivePlane.Offset - 1 / Math.Sqrt(2)) < 1e-5, "grazing-plane nudge remains microscopic");
        var lShape = Voxels(new[] { (0, 0, 0), (1, 0, 0), (0, 1, 0) });
        var lCut = Slice(lShape, new(new(0, 0, 1), .43), "nonconvex L profile");
        Check(lCut.Pieces.Length == 2 && lCut.HoleCount == 0, "nonconvex profile remains two closed components");
        foreach (var piece in lCut.Pieces) Near(CapArea(piece), 3, "nonconvex cap area");
        var ring = Voxels(Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Where(y => x != 1 || y != 1).Select(y => (x, y, 0))));
        var ringCut = Slice(ring, new(new(0, 0, 1), .37), "hollow square hull with cap hole");
        Check(ringCut.Pieces.Length == 2 && ringCut.ContourCount == 2 && ringCut.HoleCount == 1, "hollow hull preserves one cap hole");
        foreach (var piece in ringCut.Pieces)
        {
            Near(CapArea(piece), 8, "hollow cap excludes the cavity area");
            foreach (var face in piece.Faces.Where(x => x.Material == 99))
            {
                var p = (face.A.Position + face.B.Position + face.C.Position) / 3;
                Check(!(p.X > 1 + 1e-7 && p.X < 2 - 1e-7 && p.Y > 1 + 1e-7 && p.Y < 2 - 1e-7), "cap never fills hollow centre");
            }
        }
        Slice(ring, new(new(.2, .1, 1), .8), "oblique plane through hollow hull");
        var ringAcross = Slice(ring, new(new(1, 0, 0), 1.5), "slice crosses an open cavity from the side");
        Check(ringAcross.Pieces.Length == 2 && ringAcross.HoleCount == 0 && ringAcross.ContourCount == 2,
            "separate cap regions do not become phantom holes");
        foreach (var piece in ringAcross.Pieces) Near(CapArea(piece), 2, "side cut exposes only actual wall material");
        var twoHoles = Voxels(Enumerable.Range(0, 5).SelectMany(x => Enumerable.Range(0, 3).Where(y => y != 1 || x != 1 && x != 3).Select(y => (x, y, 0))));
        var twoHoleCut = Slice(twoHoles, new(new(0, 0, 1), .63), "multiple independent holes");
        Check(twoHoleCut.HoleCount == 2 && twoHoleCut.Pieces.Length == 2, "both holes reconstructed");
        foreach (var piece in twoHoleCut.Pieces) Near(CapArea(piece), 13, "multiple-hole cap area");
        var enclosedCavity = Voxels(Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3)
            .SelectMany(y => Enumerable.Range(0, 3).Where(z => x != 1 || y != 1 || z != 1).Select(z => (x, y, z)))));
        var cavityCut = Slice(enclosedCavity, new(new(0, 0, 1), .2), "plane misses a sealed internal cavity");
        Check(cavityCut.Pieces.Length == 2, "inward cavity boundary joins surrounding material component");
        Near(cavityCut.Pieces.Sum(p => p.Volume), 26, "sealed cavity volume excluded from solid mass");
        var uShape = Voxels(new[] { (0, 0, 0), (1, 0, 0), (2, 0, 0), (0, 1, 0), (2, 1, 0), (0, 2, 0), (2, 2, 0) });
        var uCut = Slice(uShape, new(new(0, 1, 0), 1.3), "one nonconvex shape breaks into three solids");
        Check(uCut.Pieces.Length == 3, "connected component extraction handles asymmetric fragment counts");
        Slice(cut.Pieces[0].Faces, new(new(1, 0, 0), .25), "generated fragment can be sliced again");
        var tube = Tube(32, 1, .8);
        FacetedChecks(box, tube);
        var tubeCut = Slice(tube, new(new(0, 0, 1), .37), "curved hollow tube with smooth normals");
        Check(tubeCut.HoleCount == 1 && tubeCut.Pieces.Length == 2, "curved cap retains the tube bore");
        foreach (var piece in tubeCut.Pieces) Near(CapArea(piece), 16 * Math.Sin(Math.PI / 16) * .36, "curved tube cap polygon area");
        Slice(tube, new(new(.3, .1, 1), .2), "oblique curved tube cross-section");
        var random = new Random(2701);
        for (int i = 0; i < 20; i++)
        {
            var normal = new CutPoint(random.NextDouble() - .5, random.NextDouble() - .5, random.NextDouble() - .5).Normalized;
            Slice(box, new(normal, CutPoint.Dot(normal, new(.5, .5, .5))), "deterministic arbitrary box orientation");
            Slice(tube, new(normal, 0), "deterministic arbitrary hollow tube orientation");
        }
        var separate = Voxels(new[] { (0, 0, 0), (3, 0, 0) });
        var separateCut = Slice(separate, new(new(0, 0, 1), .4), "multiple disconnected solids");
        Check(separateCut.Pieces.Length == 4, "disconnected solids become four independent pieces");
        var shifted = box.Select(f => new SliceFace(Shift(f.A), Shift(f.B), Shift(f.C), f.Material)).ToArray();
        SliceVertex Shift(SliceVertex v) => v with { Position = v.Position + new CutPoint(.4, .4, 0) };
        Check(!PlaneSlicer.TrySlice(box.Concat(shifted).ToArray(), new(new(0, 0, 1), .5), out _, out _, 99),
            "overlapping authored solids are not silently Boolean-unioned");
        Check(PlaneSlicer.TrySliceCompound(new[] { box, shifted }, new(new(0, 0, 1), .5), out var compound, out var compoundFailure, 99),
            "closed authored compound sliced independently: " + compoundFailure);
        Check(compound!.Pieces.Length == 2, "compound keeps each side as one rigid half");
        Near(compound.SourceVolume, 2, "compound source volume is additive, not Boolean-union volume");
        foreach (var piece in compound.Pieces)
        {
            Near(piece.Volume, 1, "compound half preserves additive mass ratio");
            Check(piece.ClosedSolids?.Length == 2 && PlaneSlicer.TryAuditCompound(piece.ClosedSolids, out var auditedVolume, out _)
                && Math.Abs(auditedVolume - piece.Volume) < 1e-6, "compound carries independent closure provenance for float export");
        }
        Check(PlaneSlicer.TrySliceCompound(new[] { box.Concat(shifted).ToArray() }, new(new(0, 0, 1), .5), out var oneGroup, out _, 99)
            && oneGroup!.Pieces.Length == 2, "closed solids within one authored model are independently clipped");
        var distant = box.Select(f => new SliceFace(Far(f.A), Far(f.B), Far(f.C), f.Material)).ToArray();
        SliceVertex Far(SliceVertex v) => v with { Position = v.Position + new CutPoint(3, 0, 0) };
        Check(PlaneSlicer.TrySliceCompound(new[] { box, distant }, new(new(1, 0, 0), .5), out var carry, out _, 99), "compound carries uncut closed solids");
        Near(carry!.Pieces.Single(p => p.PositiveSide).Volume, 1.5, "uncut source component retains its mass on the correct side");
        MultiscaleChecks(box);
        Check(!PlaneSlicer.TrySliceCompound(new[] { box.Skip(1).ToArray(), shifted }, new(new(0, 0, 1), .5), out _, out _, 99),
            "compound mode never repairs or accepts an open authored group");
        var nearLevels = box.Select(f => new SliceFace(Perturb(f.A), Perturb(f.B), Perturb(f.C), f.Material)).ToArray();
        SliceVertex Perturb(SliceVertex v) => v with { Position = v.Position + new CutPoint(v.Position.Y * v.Position.Z * 1e-8, 0, 0) };
        Slice(nearLevels, new(new(0, 0, 1), .5), "near-equal contour levels remain shared and watertight");
        Check(!PlaneSlicer.TrySlice(box.Skip(1).ToArray(), new(new(0, 0, 1), .5), out _, out var openFailure, 99)
            && openFailure.Contains("manifold"), "open mesh rejected without output");
        Check(!PlaneSlicer.TrySlice(box.Concat(new[] { box[0] }).ToArray(), new(new(0, 0, 1), .5), out _, out _, 99), "nonmanifold duplicate face rejected");
        var reversed = box.ToArray(); reversed[0] = reversed[0] with { B = reversed[0].C, C = reversed[0].B };
        Check(!PlaneSlicer.TrySlice(reversed, new(new(0, 0, 1), .5), out _, out _, 99), "inconsistent surface winding rejected");
        Check(!PlaneSlicer.TrySlice(box, new(new(0, 0, 1), 2), out _, out _, 99), "nonintersecting plane produces no replacement");
        Console.WriteLine("PASS: arbitrary plane slicing, holes, components, winding, attributes, volume and inertia");
    }

    private static void MultiscaleChecks(SliceFace[] box)
    {
        // Five-centimetre trim with a 50 nm edge remains valid at its own
        // topology tolerance; a 100 m companion must not weld that edge away.
        var tiny = box.Select(f => Map(f, p => new(p.X * 5e-8 + .2, p.Y * .05 + .2, p.Z * .05 + .475))).ToArray();
        var large = box.Select(f => Map(f, p => p * 100)).ToArray();
        Check(PlaneSlicer.TryAudit(tiny, out _) && PlaneSlicer.TryAudit(large, out _), "multiscale members each pass their own input topology audit");
        foreach (double scale in new[] { 1.0, 3.0 })
        {
            double angle = .43; var offset = new CutPoint(17, -23, 41);
            CutPoint Rotate(CutPoint p) => new(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), p.X * Math.Sin(angle) + p.Y * Math.Cos(angle), p.Z);
            CutPoint Point(CutPoint p) => Rotate(p * scale) + offset;
            var members = new[] { tiny.Select(f => Map(f, Point)).ToArray(), large.Select(f => Map(f, Point)).ToArray() };
            var plane = new SlicePlane(new(0, 0, 1), offset.Z + .5 * scale);
            Check(PlaneSlicer.TrySliceCompound(members, plane, out var cut, out var failure), "rotated/scaled multiscale compound slices without collapsing trim: " + failure);
            Check(cut!.Pieces.All(p => p.ClosedSolids!.Length == 2 && PlaneSlicer.TryAuditCompound(p.ClosedSolids, out _, out _)),
                "small trim and large hull keep independent closed-solid provenance on both sides");
            Check(cut.EffectivePlane == plane, "every multiscale member uses the exact common plane");
            var reordered = members.Reverse().Select(m => m.Reverse().ToArray()).ToArray();
            Check(PlaneSlicer.TrySliceCompound(reordered, plane, out var repeat, out failure)
                && repeat!.EffectivePlane == cut.EffectivePlane
                && Math.Abs(repeat.SourceVolume - cut.SourceVolume) <= cut.SourceVolume * 1e-12,
                "material/group and triangle ordering preserve multiscale acceptance and volume: " + failure);
        }
        var seam = box.ToArray();
        seam[0] = seam[0] with { A = seam[0].A with { Position = seam[0].A.Position + new CutPoint(1e-10, 0, 0) } };
        var canonical = PlaneSlicer.CanonicalPositions(seam);
        var reverseCanonical = PlaneSlicer.CanonicalPositions(seam.Reverse().ToArray()).Reverse().ToArray();
        Check(canonical.SequenceEqual(reverseCanonical), "seam weld representatives are independent of triangle and material batching order");

        static SliceFace Map(SliceFace f, Func<CutPoint, CutPoint> point) => new(
            f.A with { Position = point(f.A.Position) }, f.B with { Position = point(f.B.Position) }, f.C with { Position = point(f.C.Position) }, f.Material);
    }

    private static void FacetedChecks(SliceFace[] box, SliceFace[] tube)
    {
        var varied = new HashSet<SlicePlane>();
        for (uint seed = 0; seed < 12; seed++)
        {
            var plane = AssistedSliceGeometry.Candidates([box], [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], seed).First();
            varied.Add(plane);
            Check(FacetedSliceGeometry.TrySlice([box], plane, seed, out var cut, out string reason), "coarse faceted box cut succeeds: " + reason);
            Near(cut!.Pieces.Sum(p => p.Volume), 1, "faceted complementary halves conserve original volume");
            double[] areas = new double[2];
            for (int side = 0; side < cut.Pieces.Length; side++)
            {
                var piece = cut.Pieces[side];
                Check(PlaneSlicer.TryAuditCompound(piece.ClosedSolids!, out _, out _), "faceted halves are closed oriented solids");
                var cap = piece.Faces.Where(f => f.Material == -1).ToArray();
                var normals = cap.Select(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Normalized).ToArray();
                Check(normals.Any(a => normals.Any(b => CutPoint.Dot(a, b) < .98)), "section contains several real noncoplanar facets");
                Check(cap.All(f => CutPoint.Dot(CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Normalized, f.A.Normal) > .99999), "facet cap normals match their actual outward geometry");
                areas[side] = cap.Sum(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length * .5);
                foreach (var face in piece.Faces.Where(f => f.Material != -1))
                    foreach (var vertex in new[] { face.A, face.B, face.C })
                    {
                        Near(vertex.U, vertex.Position.X, "faceted cut preserves source U interpolation");
                        Near(vertex.V, vertex.Position.Y, "faceted cut preserves source V interpolation");
                    }
            }
            Near(areas[0], areas[1], "complementary faceted seams have equal area");
            Check(FacetedSliceGeometry.TrySlice([box], plane, seed, out var repeat, out _)
                && repeat!.Pieces[0].Faces.SequenceEqual(cut.Pieces[0].Faces), "same cut seed exactly reproduces its facets");
        }
        Check(varied.Count == 12, "different cut seeds produce distinct oblique off-center planes");
        Check(FacetedSliceGeometry.TrySlice([tube], new(new(0, 0, 1), .37), 17, out var hollow, out var failure)
            && hollow!.HoleCount == 1 && hollow.Pieces.All(p => PlaneSlicer.TryAudit(p.Faces, out _)), "coarse facets preserve a hollow curved tank bore: " + failure);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { FacetedSliceGeometry.TrySlice([box], new(new(1, 0, 0), .5), 0, out _, out _, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "coarse facet work honors cancellation before subdivision");
    }

    internal static SliceFace[] Voxels(IEnumerable<(int X, int Y, int Z)> cells)
    {
        var occupied = cells.ToHashSet(); var output = new List<SliceFace>();
        var directions = new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        foreach (var cell in occupied)
            for (int side = 0; side < directions.Length; side++)
            {
                var direction = directions[side];
                if (occupied.Contains((cell.X + direction.Item1, cell.Y + direction.Item2, cell.Z + direction.Item3))) continue;
                var normal = new CutPoint(direction.Item1, direction.Item2, direction.Item3);
                var tangent = Math.Abs(normal.X) < .8 ? new CutPoint(1, 0, 0) : new CutPoint(0, 1, 0);
                var u = CutPoint.Cross(tangent, normal).Normalized; var v = CutPoint.Cross(normal, u);
                var center = new CutPoint(cell.X + .5, cell.Y + .5, cell.Z + .5) + normal * .5;
                var points = new[] { center - u * .5 - v * .5, center + u * .5 - v * .5, center + u * .5 + v * .5, center - u * .5 + v * .5 };
                SliceVertex Vertex(CutPoint p) => new(p, normal, p.X, p.Y);
                output.Add(new(Vertex(points[0]), Vertex(points[1]), Vertex(points[2]), 10 + side % 2));
                output.Add(new(Vertex(points[0]), Vertex(points[2]), Vertex(points[3]), 10 + side % 2));
            }
        return output.ToArray();
    }
    private static SliceFace[] Tube(int segments, double outer, double inner)
    {
        var faces = new List<SliceFace>();
        for (int i = 0; i < segments; i++)
        {
            double a = i * Math.PI * 2 / segments, b = (i + 1) * Math.PI * 2 / segments;
            var oa = new CutPoint(Math.Cos(a) * outer, Math.Sin(a) * outer, -1);
            var ob = new CutPoint(Math.Cos(b) * outer, Math.Sin(b) * outer, -1);
            var ia = new CutPoint(Math.Cos(a) * inner, Math.Sin(a) * inner, -1);
            var ib = new CutPoint(Math.Cos(b) * inner, Math.Sin(b) * inner, -1);
            var up = new CutPoint(0, 0, 2);
            Quad(oa, ob, ob + up, oa + up, new CutPoint(Math.Cos((a + b) / 2), Math.Sin((a + b) / 2), 0), true);
            Quad(ia, ib, ib + up, ia + up, new CutPoint(-Math.Cos((a + b) / 2), -Math.Sin((a + b) / 2), 0), true);
            Quad(oa, ob, ib, ia, new(0, 0, -1), false);
            Quad(oa + up, ob + up, ib + up, ia + up, new(0, 0, 1), false);
        }
        return faces.ToArray();
        void Quad(CutPoint a, CutPoint b, CutPoint c, CutPoint d, CutPoint normal, bool smooth)
        { Add(a, b, c); Add(a, c, d);
            void Add(CutPoint p, CutPoint q, CutPoint r)
            {
                if (CutPoint.Dot(CutPoint.Cross(q - p, r - p), normal) < 0) (q, r) = (r, q);
                SliceVertex V(CutPoint point) => new(point, smooth
                    ? new CutPoint(point.X, point.Y, 0).Normalized * (CutPoint.Dot(new(point.X, point.Y, 0), normal) < 0 ? -1 : 1)
                    : normal, point.X, point.Y);
                faces.Add(new(V(p), V(q), V(r), 10));
            }
        }
    }
    private static PlaneSliceResult Slice(SliceFace[] faces, SlicePlane plane, string label)
    {
        if (!PlaneSlicer.TrySlice(faces, plane, out var result, out var failure, 99))
            throw new InvalidOperationException(label + ": " + failure);
        Check(result != null && Math.Abs(result.Pieces.Sum(p => p.Volume) - result.SourceVolume) < 1e-5, label);
        return result!;
    }
    private static double CapArea(SlicePiece piece) => piece.Faces.Where(x => x.Material == 99)
        .Sum(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length * .5);
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 2e-6, message);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Plane slicing check failed: " + message); }
}
