using System.Text;
using System.Xml.Linq;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

internal static class QuietBreakupAssetChecks
{
    internal static void Run()
    {
        var (fixture, materials) = FragmentAssetChecks.Fixture();
        var original = fixture.Solids[0];
        var groups = Enumerable.Range(0, 11).Select(i => Move(original, 1, new(i * 3, 0, 0))).ToArray();
        var input = new SliceInput(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 110);
        Check(QuietSubpartBreakup.TryBuild(input, out var result, out var failure, default), "valid authored subpart fallback: " + failure);
        Check(result!.AuthoredSubpartFallback && result.Fragments.Length == 8
            && Math.Abs(result.Fragments.Sum(f => f.MassKg) - 110) < .001,
            "fallback groups trim into at most eight fragments while conserving all source dry mass");
        Check(result.Fragments.Any(f => f.PositiveSide) && result.Fragments.Any(f => !f.PositiveSide),
            "authored fallback has both separation sides even without a geometric cutting plane");
        foreach (var fragment in result.Fragments)
        {
            var xml = XDocument.Parse(Encoding.UTF8.GetString(fragment.Xml));
            var part = xml.Root!.Element("Part")!;
            Check(part.Elements().Select(e => e.Name.LocalName).All(n => n is "SubPart" or "Collider" or "CustomMass"),
                "fallback uses durable inert generated templates without cloning functional modules");
        }
        Check(QuietSubpartBreakup.TryBuild(input, out var repeated, out _, default)
            && repeated!.Fragments.Select(f => f.Id).SequenceEqual(result.Fragments.Select(f => f.Id)),
            "authored grouping, material export and asset IDs are deterministic");
        Check(!QuietSubpartBreakup.TryBuild(input with { GeneratedClosedGeometry = true }, out _, out failure, default)
            && failure.Contains("closed geometry"), "generated debris cannot fall back to authored sheet backing");
        Check(!QuietSubpartBreakup.TryBuild(input with { Groups = [original] }, out _, out _, default),
            "one authored subpart refuses instead of deleting the original without a breakup");
        var broken = original.Take(original.Length - 1).ToArray();
        var invalid = input with { Groups = [broken, [broken[0] with { A = broken[0].A with { Position = new(double.NaN, 0, 0) } }]] };
        Check(!QuietSubpartBreakup.TryBuild(invalid, out _, out _, default), "nonfinite authored fallback refuses without native mutation");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool stopped = false;
        try { QuietSubpartBreakup.TryBuild(input, out _, out _, cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "fallback worker respects cancellation");

        // Every common cutting plane rejects the tiny solids under the large
        // compound tolerance. Independently centred authored assets remain valid.
        // This exercises the production Calculate -> fallback -> export path.
        var separated = input with { Groups = [Move(original, .001, new(-100000, 0, 0)), Move(original, .001, new(100000, 0, 0))], MassKg = 100 };
        SlicePlan? published = null;
        var calculated = SliceGeometryWork.Calculate(separated, default, plan => published = plan);
        Check(calculated.AuthoredSubpartFallback && published != null && calculated.Fragments.Length == 2,
            "production geometry worker tries quiet authored breakup after every mesh cut fails validation");
        Console.WriteLine("PASS: quiet fallback bounded inert asset export, dry mass, determinism, provenance, negative fixtures and production worker integration");
    }
    private static SliceFace[] Move(SliceFace[] faces, double scale, CutPoint offset)
    {
        // Retain every normal/UV/material while moving fixture positions.
        SliceVertex V(SliceVertex vertex) => vertex with { Position = vertex.Position * scale + offset };
        return faces.Select(f => f with { A = V(f.A), B = V(f.B), C = V(f.C) }).ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
