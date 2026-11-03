using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class TreeEndChecks
{
    internal static void Run(string path)
    {
        int sections = 0, recuts = 0;
        foreach (var fixture in JsonSerializer.Deserialize<ClutterStockSliceChecks.Fixture[]>(File.ReadAllText(path))!.Where(f => f.Tree))
        {
            var groups = fixture.Groups.Select(g => g.Select(Face).ToArray()).ToArray();
            var materials = fixture.Materials.ToDictionary(s => s.Index, s => new SliceMaterialData(s.Id, false,
                0x65432100, ClutterSurfaceTint.Luminosity(.21763764f), s.Foliage, s.DoubleSided, s.KeepBackfaceNormals));
            Check(TreeSolidPreparation.TryPrepare(groups, out var prepared, out var reason), fixture.Name + " solid preparation: " + reason);
            Check(prepared.SelectMany(s => s).All(f => f.Material >= 0), "artificial source root/tip closures cannot become fresh hot cuts or sparks");
            Check(PlaneSlicer.TryAuditCompound(prepared, out _, out reason), "every sealed native tree and retained detail passes closed physics audit: " + reason);
            var main = prepared.MaxBy(s => PlaneSlicer.Properties(s).Volume)!;
            var points = main.SelectMany(Points).ToArray();
            var normals = new[] { new CutPoint(0, 1, 0), new CutPoint(0, 1, 0), new CutPoint(.31, 1, -.23).Normalized };
            var fractions = new[] { .12, .5, .35 };
            var acceptedNormals = new List<string>();
            for (int section = 0; section < normals.Length; section++)
            {
                var normal = normals[section];
                double low = points.Min(p => CutPoint.Dot(p, normal)), high = points.Max(p => CutPoint.Dot(p, normal));
                var plane = new SlicePlane(normal, low + (high - low) * fractions[section]);
                // The complete worker still carries every real branch/bark component
                // and foliage; no simplified proxy is credited as a stock tree cut.
                var input = new SliceInput(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 100)
                { Foliage = fixture.Foliage.Select(Face).ToArray(), Contact = new(normal * plane.Offset, normal) };
                var cut = ClutterSliceGeometry.Calculate(input, true, default, _ => { });
                normal = cut.Plane.Normal;
                Check(section != 2 || new[] { normal.X, normal.Y, normal.Z }.Count(n => Math.Abs(n) > .001) >= 2,
                    fixture.Name + " oblique coverage requires a genuinely oblique accepted plane");
                acceptedNormals.Add($"{section}:({normal.X:F6},{normal.Y:F6},{normal.Z:F6})/{cut.Attempts}attempts");
                Check(PlaneSlicer.TrySliceCompound([main], cut.Plane, out var reference, out reason), fixture.Name + " main section: " + reason);
                var referenceCaps = reference!.Pieces.Single(p => p.PositiveSide).Faces.Where(f => f.Material == -1).ToArray();
                var center = LargestSectionCenter(referenceCaps);
                double expectedArea = Area(referenceCaps);
                var rawMapped = reference.Pieces.Select(p => WoodCapGeometry.Map(p, reference.EffectivePlane)).ToArray();
                CheckPairedUvs(rawMapped);
                foreach (var piece in reference.Pieces)
                    Check(Hit(piece.Faces.Where(f => f.Material == -1), center, normal), "solid wood fills the center of both complementary cut ends");
                Check(cut.Fragments.Length == 2 && !cut.Faceted && !cut.AuthoredSubpartFallback
                    && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - 100) < .001, "stock section produces two capped solid-wood fragments with conserved native mass");
                foreach (var asset in cut.Fragments)
                {
                    var xml = XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
                    var topology = FragmentTopology.Read(xml)!;
                    Check(topology.CutKind == SliceCutKind.Wood && (int)xml.Root!.Element("Purr2D2SliceTopology")!.Attribute("Version")! == 3,
                        "wood semantics persist explicitly even on a stump without any foliage");
                    var cap = ReadWoodCaps(asset, xml);
                    var localCenter = Transform(center, asset.Frame.SourceToFragment);
                    var localNormal = Normal(normal, asset.Frame.SourceToFragment);
                    Check(Hit(cap, localCenter, localNormal) && Area(cap) >= expectedArea * .98,
                        $"{fixture.Name} section {section}: exported native cap covers the trunk center and filled section area");
                    Check(cap.SelectMany(Vertices).All(v => v.U >= .02999 && v.U <= .97001 && v.V >= .02999 && v.V <= .97001),
                        "new end-grain UVs stay within their centered per-contour texture field");
                    var restored = ClutterStockSliceChecks.Restore(asset, materials);
                    Check(restored.CutKind == SliceCutKind.Wood && !restored.SourceSolidWood && restored.GeneratedClosedGeometry
                        && PlaneSlicer.TryAuditCompound(restored.Groups, out _, out reason), "saved recapture keeps structural solids and never reruns native tube closure: " + reason);
                    if (section != 0) continue;
                    var next = SliceGeometryWork.Calculate(restored, default, _ => { });
                    Check(next.Fragments.Length == 2 && Math.Abs(next.Fragments.Sum(f => f.MassKg) - asset.MassKg) < .001
                        && next.Fragments.All(f => FragmentTopology.Read(XDocument.Parse(Encoding.UTF8.GetString(f.Xml)))!.CutKind == SliceCutKind.Wood),
                        "both saved stump and crown actually recut as wood with conserved mass");
                    CheckOldCapUvs(restored, next);
                    recuts++;
                }
                sections++;
            }
            Console.WriteLine($"PASS: {fixture.Name}: solid low/mid/oblique cut ends, paired and preserved end-grain UVs, stump/crown wood recuts and closed physics; accepted normals {string.Join("; ", acceptedNormals)}");
        }
        Check(sections == 27 && recuts == 18, "all nine stock variants require three genuine filled sections and both durable fragment recuts");
        HollowMetalAndSheetRemainHollow();
        Console.WriteLine("PASS: 27 actual stock solid-wood sections and 18 saved stump/crown recuts; hollow metal vessels and flat annular sheets remain hollow");
    }

    private static void CheckOldCapUvs(SliceInput original, CpuSlice next)
    {
        var old = original.Groups.SelectMany(g => g).Where(f => original.Materials[f.Material].Id == FragmentAssetWriter.WoodCapMaterial).ToArray();
        Check(old.Length > 0 && old.All(f => f.Material >= 0), "restored old caps have bound positive material indices");
        var normal = old[0].A.Normal.Normalized;
        double offset = CutPoint.Dot(old[0].A.Position, normal), area = 0; int samples = 0;
        foreach (var asset in next.Fragments)
        {
            Check(double4x4.Invert(asset.Frame.SourceToFragment, out var inverse), "recut principal frame can restore parent coordinates");
            var xml = XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
            foreach (var face in ReadWoodCaps(asset, xml))
            {
                var a = Transform(face.A.Position, inverse); var b = Transform(face.B.Position, inverse); var c = Transform(face.C.Position, inverse);
                if (Math.Abs(CutPoint.Dot(Normal(face.A.Normal, inverse), normal)) < .99999
                    || new[] { a, b, c }.Any(p => Math.Abs(CutPoint.Dot(p, normal) - offset) > .0001)) continue;
                area += CutPoint.Cross(b - a, c - a).Length / 2;
                foreach (var vertex in Vertices(face))
                {
                    var point = Transform(vertex.Position, inverse);
                    Check(old.Any(f => Matches(f, point, vertex.U, vertex.V)),
                        "saved old-cap UVs remain the original affine field after clipping, float export and another principal-frame transform");
                    samples++;
                }
            }
        }
        Check(samples > 0 && Math.Abs(area - Area(old)) < Math.Max(1e-7, Area(old) * .0002),
            "recutting preserves the complete old cut surface with its existing end-grain field");
        static bool Matches(SliceFace face, CutPoint point, double u, double v)
        {
            var ab = face.B.Position - face.A.Position; var ac = face.C.Position - face.A.Position;
            var normal = CutPoint.Cross(ab, ac); double denominator = normal.LengthSquared;
            if (denominator < 1e-22) return false;
            var delta = point - face.A.Position;
            double b = CutPoint.Dot(CutPoint.Cross(delta, ac), normal) / denominator;
            double c = CutPoint.Dot(CutPoint.Cross(ab, delta), normal) / denominator;
            double a = 1 - b - c;
            if (Math.Min(a, Math.Min(b, c)) < -.001) return false;
            return Math.Abs(a * face.A.U + b * face.B.U + c * face.C.U - u) < .0002
                && Math.Abs(a * face.A.V + b * face.B.V + c * face.C.V - v) < .0002;
        }
    }

    private static void HollowMetalAndSheetRemainHollow()
    {
        var washer = Washer();
        Check(TreeSolidPreparation.TryPrepare([washer], out var backed, out var reason), "flat annular detail retains its backing: " + reason);
        double low = backed.SelectMany(s => s).SelectMany(Points).Min(p => p.Y);
        double high = backed.SelectMany(s => s).SelectMany(Points).Max(p => p.Y);
        Check(PlaneSlicer.TrySliceCompound(backed, new(new(0, 1, 0), (low + high) / 2), out var sheetCut, out reason)
            && sheetCut!.HoleCount > 0 && !Hit(sheetCut.Pieces[0].Faces.Where(f => f.Material == -1), new(0, (low + high) / 2, 0), new(0, 1, 0)),
            "a planar two-loop annular ribbon cannot be bridged into solid wood: " + reason);
        var pipe = Pipe();
        var materials = new Dictionary<int, SliceMaterialData> { [0] = new("TreeCheckMetal", false) };
        var input = new SliceInput([pipe], [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 10)
        { GeneratedClosedGeometry = true, Contact = new(new(0, 0, 0), new(0, 1, 0)) };
        var cut = SliceGeometryWork.Calculate(input, default, _ => { });
        Check(cut.Fragments.Length == 2 && cut.Fragments.All(a => FragmentTopology.Read(XDocument.Parse(Encoding.UTF8.GetString(a.Xml)))!.CutKind == SliceCutKind.Metal),
            "ordinary hollow metal vessel semantics remain unchanged");
        Check(PlaneSlicer.TrySliceCompound([pipe], new(new(0, 1, 0), 0), out var pipeCut, out reason)
            && pipeCut!.HoleCount == 1 && pipeCut.Pieces.All(p => !Hit(p.Faces.Where(f => f.Material == -1), default, new(0, 1, 0))),
            "metal pipe cuts retain the real central hole: " + reason);
        bool rejected = false;
        try { SliceGeometryWork.Calculate(input with { SourceSolidWood = true, CutKind = SliceCutKind.Wood }, default, _ => { }); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "native solid-wood preparation is forbidden on already generated geometry");
    }

    private static void CheckPairedUvs(SlicePiece[] pieces)
    {
        var positive = pieces.Single(p => p.PositiveSide).Faces.Where(f => f.Material == -1).SelectMany(Vertices)
            .GroupBy(v => v.Position).ToDictionary(g => g.Key, g => g.First());
        var negative = pieces.Single(p => !p.PositiveSide).Faces.Where(f => f.Material == -1).SelectMany(Vertices).ToArray();
        Check(negative.All(v =>
            {
                // Cap fan centers sum reversed corner orders on opposite sides;
                // their double-rounding drift is below the native float precision.
                var other = positive.TryGetValue(v.Position, out var exact) ? exact : positive.Values.MinBy(p => (p.Position - v.Position).LengthSquared);
                return (v.Position - other.Position).Length < 1e-9 && Math.Abs(v.U - other.U) < 1e-9 && Math.Abs(v.V - other.V) < 1e-9;
            }),
            "opposing end-grain caps share the same centered UV coordinates at every common vertex");
    }

    private static CutPoint LargestSectionCenter(SliceFace[] faces)
    {
        var incident = new Dictionary<CutPoint, List<int>>();
        for (int i = 0; i < faces.Length; i++) foreach (var point in Points(faces[i]))
        { if (!incident.TryGetValue(point, out var list)) incident.Add(point, list = new()); list.Add(i); }
        var visited = new HashSet<int>(); var regions = new List<SliceFace[]>();
        for (int seed = 0; seed < faces.Length; seed++)
        {
            if (!visited.Add(seed)) continue;
            var region = new List<SliceFace>(); var pending = new Queue<int>(); pending.Enqueue(seed);
            while (pending.TryDequeue(out int i))
            { region.Add(faces[i]); foreach (var p in Points(faces[i])) foreach (int next in incident[p]) if (visited.Add(next)) pending.Enqueue(next); }
            regions.Add(region.ToArray());
        }
        var largest = regions.MaxBy(Area)!;
        double area = Area(largest);
        return largest.Aggregate(default(CutPoint), (sum, f) => sum + (f.A.Position + f.B.Position + f.C.Position)
            * (CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length / 2 / 3 / area));
    }

    private static SliceFace[] ReadWoodCaps(CpuFragmentAsset asset, XDocument xml)
    {
        var names = xml.Root!.Elements("SubPart").Elements("PartModel")
            .Where(m => (string?)m.Element("Material")?.Attribute("Id") == FragmentAssetWriter.WoodCapMaterial)
            .Select(m => (string)m.Element("Mesh")!.Attribute("Id")!).ToHashSet();
        var result = new List<SliceFace>();
        foreach (var upload in asset.Uploads.Where(u => names.Contains(u.Name)))
        {
            for (int f = 0; f < upload.IndexBytes / 12; f++) result.Add(new(V(f * 3), V(f * 3 + 1), V(f * 3 + 2), -1));
            SliceVertex V(int index)
            {
                int offset = BitConverter.ToInt32(upload.Bytes, upload.VertexBytes + index * 4) * 32;
                float F(int i) => BitConverter.ToSingle(upload.Bytes, offset + i * 4);
                return new(new(F(0), F(1), F(2)), new(F(3), F(4), F(5)), F(6), F(7));
            }
        }
        return result.ToArray();
    }

    private static SliceFace[] Washer()
    {
        var result = new List<SliceFace>();
        for (int i = 0; i < 24; i++)
        {
            double a = i * Math.Tau / 24, b = (i + 1) * Math.Tau / 24;
            SliceVertex V(double angle, double radius) => new(new(Math.Cos(angle) * radius, 0, Math.Sin(angle) * radius), new(0, 1, 0), 0, 0);
            result.Add(new(V(a, 2), V(a, 1), V(b, 1), 0)); result.Add(new(V(a, 2), V(b, 1), V(b, 2), 0));
        }
        return result.ToArray();
    }
    private static SliceFace[] Pipe()
    {
        var result = new List<SliceFace>();
        for (int i = 0; i < 24; i++)
        {
            double a = i * Math.Tau / 24, b = (i + 1) * Math.Tau / 24;
            CutPoint P(double angle, double radius, double y) => new(Math.Cos(angle) * radius, y, Math.Sin(angle) * radius);
            Quad(P(a, 2, -1), P(a, 2, 1), P(b, 2, 1), P(b, 2, -1));
            Quad(P(a, 1, -1), P(b, 1, -1), P(b, 1, 1), P(a, 1, 1));
            Quad(P(a, 2, 1), P(a, 1, 1), P(b, 1, 1), P(b, 2, 1));
            Quad(P(a, 2, -1), P(b, 2, -1), P(b, 1, -1), P(a, 1, -1));
        }
        return result.ToArray();
        void Quad(CutPoint a, CutPoint b, CutPoint c, CutPoint d)
        {
            var n = CutPoint.Cross(b - a, c - a).Normalized;
            SliceVertex V(CutPoint p) => new(p, n, 0, 0);
            result.Add(new(V(a), V(b), V(c), 0)); result.Add(new(V(a), V(c), V(d), 0));
        }
    }
    private static bool Hit(IEnumerable<SliceFace> faces, CutPoint center, CutPoint normal) => faces.Any(f =>
        CutGeometry.TryRayTriangle(center + normal * .05, normal * -1, new(f.A.Position, f.B.Position, f.C.Position), out double distance) && distance < .1);
    private static CutPoint Transform(CutPoint p, double4x4 matrix) { var v = double3.Transform(new(p.X, p.Y, p.Z), matrix); return new(v.X, v.Y, v.Z); }
    private static CutPoint Normal(CutPoint p, double4x4 matrix) { var v = double3.TransformNormal(new(p.X, p.Y, p.Z), matrix); return new CutPoint(v.X, v.Y, v.Z).Normalized; }
    private static SliceFace Face(double[][] f) => new(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), (int)f[0][8]);
    private static SliceVertex Vertex(double[] v) => new(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), v[6], v[7]);
    private static IEnumerable<SliceVertex> Vertices(SliceFace face) => new[] { face.A, face.B, face.C };
    private static IEnumerable<CutPoint> Points(SliceFace face) => new[] { face.A.Position, face.B.Position, face.C.Position };
    private static double Area(IEnumerable<SliceFace> faces) => faces.Sum(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length / 2);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Tree ends: " + message); }
}
