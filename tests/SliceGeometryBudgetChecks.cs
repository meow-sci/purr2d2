using System.Text;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class SliceGeometryBudgetChecks
{
    private static readonly IReadOnlyDictionary<int, SliceMaterialData> Materials =
        new Dictionary<int, SliceMaterialData> { [0] = new("BudgetFixtureMetal", false) };

    internal static void Run()
    {
        var previous = SliceGeometryBudget.Current;
        try
        {
            CustomSolidLimitAndSnapshot();
            TriangleLimitsAndFallback();
            AboveOriginalTriangleLimits();
        }
        finally { SliceGeometryBudget.Configure(previous.Solids, previous.SourceTriangles); }
        Console.WriteLine("PASS: configurable slice geometry limits, per-cut snapshots, >2048 solids, >120k triangles, export and saved topology");
    }

    private static void CustomSolidLimitAndSnapshot()
    {
        var box = Box(1);
        var groups = Enumerable.Range(0, 2078).Select(i => Move(box, new(i * 3, 0, 0))).ToArray();
        Check(!SliceShellBuilder.TryPrepare(groups, out _, out var reason, geometry: SliceGeometryBudget.Default)
            && reason.Contains("2,078 / 2,048"), "default preparation reports the measured and selected solid limits");

        SliceGeometryBudget.Configure(4096, 240_000);
        var input = Input(groups);
        SliceGeometryBudget.Configure(128, 1000); // A later UI edit must not change this cut.
        Check(input.Geometry.Solids == 4096 && input.Geometry.SourceTriangles == 240_000,
            "worker input retains its immutable geometry snapshot after live settings change");
        var cut = SliceGeometryWork.Calculate(input, default, _ => { });
        Check(cut.Fragments.Length == 2 && !cut.AuthoredSubpartFallback
            && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - input.MassKg) < .01,
            "2078 solids pass preparation, clipping, float32 validation and actual fragment export with raised limits");
        var large = cut.Fragments.Select(f => XDocument.Parse(Encoding.UTF8.GetString(f.Xml)))
            .OrderByDescending(xml => (int)xml.Root!.Element("Purr2D2SliceTopology")!.Attribute("Solids")!).First();
        Check((int)large.Root!.Element("Purr2D2SliceTopology")!.Attribute("Solids")! > 2048,
            "the production result really exports a fragment above the former solid ceiling");
        FragmentTopology.Remember(large);
        Check(FragmentTopology.Read(large) != null,
            "saved topology remains loadable after geometry preferences are lowered");
        ExpectRejected(() => FragmentTopology.Read(large, SliceGeometryBudget.Default),
            "explicit default-budget validation refuses the same larger topology");
        ExpectRejected(() => SliceGeometryWork.Calculate(input with { Geometry = SliceGeometryBudget.Current }, default, _ => { }),
            "the next cut uses the newly lowered solid limit");
    }

    private static void TriangleLimitsAndFallback()
    {
        var small = new SliceGeometryBudget(128, 1000);
        var larger = new SliceGeometryBudget(256, 12_000);
        var box = Box(10); // 1200 real, manifold triangles.
        foreach (bool generated in new[] { false, true })
            Check(!SliceShellBuilder.TryPrepare([box], out _, out var reason, generatedClosedGeometry: generated, geometry: small)
                && reason.Contains("1,200 / 1,000"), "closed and generated fast paths cannot bypass source triangle admission");
        var panels = Enumerable.Range(0, 100).SelectMany(i => Move(Box(1).Take(2).ToArray(), new(i * 3, 0, 0))).ToArray();
        Check(panels.Length < small.SourceTriangles
            && !SliceShellBuilder.TryPrepare([panels], out _, out var panelReason, geometry: small)
            && panelReason.Contains("Prepared hull") && panelReason.Contains("1,000 source triangles"),
            "backing authored panels cannot exceed the selected prepared triangle allowance");
        Check(!PlaneSlicer.TrySlice(box, new(new(1, 0, 0), .37), out _, out _, geometry: small),
            "direct planar clipping uses the same source triangle limit");
        Check(!FacetedSliceGeometry.TrySlice([box], new(new(1, 0, 0), .37), 123, out _, out _, geometry: small)
            && FacetedSliceGeometry.TrySlice([box], new(new(1, 0, 0), .37), 123, out _, out _, geometry: larger),
            "faceted subdivision and its internal clipping follow the selected triangle allowance");

        var output = Box(13); // 2028 triangles: just beyond the small combined output allowance.
        Check(!PlaneSlicer.TryAudit(output, out _, small) && PlaneSlicer.TryAudit(output, out _, larger)
            && !ClosedMeshPreparation.TryNormalize(output, out _, out _, geometry: small)
            && ClosedMeshPreparation.TryNormalize(output, out _, out _, geometry: larger),
            "closed-mesh auditing and normalization scale their output ceilings with the selected source budget");
        var fragmentFaces = Box(12); // 1728: below small combined output, above its per-fragment allowance.
        var properties = PlaneSlicer.Properties(fragmentFaces);
        var piece = new SlicePiece(fragmentFaces, properties.Volume, properties.Centroid, properties.Inertia, true)
            { ClosedSolids = [fragmentFaces] };
        Check(SlicePrincipalFrame.TryCreate(piece, out var frame, out _)
            && !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out _, out _, out _, geometry: small)
            && SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out _, out _, out _, geometry: larger),
            "float32 preparation enforces the derived per-fragment allowance");
        var fragment = new CpuFragment(piece, frame, [fragmentFaces], new(1, 1, 1), 10);
        ExpectRejected(() => FragmentAssetWriter.Build(fragment, Materials, default, small),
            "asset export independently checks the same per-fragment allowance");

        var islands = Enumerable.Range(0, 129).Select(i => Move(Box(1), new(i * 3, 0, 0))).ToArray();
        var manySolids = new SliceGeometryBudget(128, 12_000);
        Check(!PlaneSlicer.TryAuditCompound([islands.SelectMany(s => s).ToArray()], out _, out _, manySolids),
            "one authored group cannot disguise too many independent closed solids");
        Check(!PlaneSlicer.TrySlice(islands.SelectMany(s => s).ToArray(), new(new(1, 0, 0), .37), out _, out _, geometry: manySolids),
            "direct planar clipping counts independent closed islands too");
        Check(!SliceShellBuilder.TryPrepare([islands.Take(64).SelectMany(s => s).ToArray(), islands.Skip(64).SelectMany(s => s).ToArray()],
            out _, out _, geometry: manySolids), "preparation counts closed islands across otherwise-valid authored groups");

        var fallback = Input([box, Move(box, new(3, 0, 0))]) with { Contact = null, Geometry = small };
        Check(!QuietSubpartBreakup.TryBuild(fallback, out _, out var reason2, default) && reason2.Contains("2,400 / 1,000"),
            "authored fallback cannot reset the aggregate source budget per group");
        Check(QuietSubpartBreakup.TryBuild(fallback with { Geometry = larger }, out _, out reason2, default),
            "authored fallback honors raised limits through precision and export: " + reason2);
        var input = Input([box]) with { Geometry = larger };
        Check(SliceGeometryWork.Calculate(input, default, _ => { }).Fragments.Length == 2,
            "a custom triangle budget is carried through the production contact worker");
    }

    private static void AboveOriginalTriangleLimits()
    {
        var raised = new SliceGeometryBudget(4096, 240_000);
        var box = Box(101); // 122,412 triangles, below the independent unique-position limit.
        Check(!SliceShellBuilder.TryPrepare([box], out _, out _, geometry: SliceGeometryBudget.Default)
            && SliceShellBuilder.TryPrepare([box], out _, out var reason, geometry: raised),
            "a valid source above 120k triangles uses the configurable preparation ceiling");
        Check(PlaneSlicer.TrySliceCompound([box], new(new(1, 0, 0), .37), out var result, out reason, geometry: raised),
            "the compound and inner planar clippers accept more than the former 120k triangles: " + reason);
        foreach (var piece in result!.Pieces)
        {
            Check(SlicePrincipalFrame.TryCreate(piece, out var frame, out reason)
                && SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out _, out _, out reason, geometry: raised),
                "large sliced geometry passes normalization and float32 audits with the same snapshot: " + reason);
        }
        var faces = Enumerable.Repeat(box[0], 200_001).ToArray();
        FragmentGlbWriter.Group[] groups = [new("LargeBudgetSurface", 0, faces)];
        ExpectRejected(() => FragmentGlbWriter.Write(groups, SliceGeometryBudget.Default),
            "default GLB output retains its original 200k fragment allowance");
        Check(FragmentGlbWriter.Write(groups, raised).Length > 0,
            "GLB serialization scales its triangle ceiling instead of retaining a hidden 200k refusal");
    }

    private static SliceInput Input(SliceFace[][] groups) => new(groups,
        [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], Materials, groups.Length * 10)
        { Contact = new(new(.37, 0, .5), new(1, 0, 0)) };

    private static SliceFace[] Move(SliceFace[] faces, CutPoint offset)
    {
        SliceVertex V(SliceVertex v) => v with { Position = v.Position + offset };
        return faces.Select(f => new SliceFace(V(f.A), V(f.B), V(f.C), f.Material)).ToArray();
    }

    private static SliceFace[] Box(int divisions)
    {
        CutPoint[] points = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];
        int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]];
        var faces = new List<SliceFace>();
        foreach (var q in quads)
        {
            var u = points[q[1]] - points[q[0]]; var v = points[q[3]] - points[q[0]];
            var normal = CutPoint.Cross(u, v).Normalized;
            SliceVertex Vertex(int x, int y) => new(points[q[0]] + u * ((double)x / divisions) + v * ((double)y / divisions),
                normal, (double)x / divisions, (double)y / divisions);
            for (int x = 0; x < divisions; x++) for (int y = 0; y < divisions; y++)
            {
                var a = Vertex(x, y); var b = Vertex(x + 1, y); var c = Vertex(x + 1, y + 1); var d = Vertex(x, y + 1);
                faces.Add(new(a, b, c, 0)); faces.Add(new(a, c, d, 0));
            }
        }
        return faces.ToArray();
    }

    private static void ExpectRejected(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Geometry budgets: " + message);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Geometry budgets: " + message); }
}
