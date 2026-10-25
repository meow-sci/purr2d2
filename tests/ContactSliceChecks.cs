using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class ContactSliceChecks
{
    private static readonly IReadOnlyDictionary<int, SliceMaterialData> Materials =
        new Dictionary<int, SliceMaterialData> { [0] = new("ContactFixtureMetal", false) };

    internal static void Run()
    {
        DirectionAndCandidates();
        ContactAnchorsWorkerPlane();
        GrazingFallbackIsBoundedAndRepeatable();
        CrossingContourUsesWiderAnchoredFallback();
        CrossingContourUsesPartAxisFallback();
        ImpossibleContactRefusesAuthoredOrCenterFallback();
        AssistedInputKeepsItsExistingPath();
    }

    private static void DirectionAndCandidates()
    {
        var point = new CutPoint(.2, .3, .4);
        Check(ContactSliceGeometry.TryCreate(point, new(0, 1, .2), new(.2, .1, .1), out var contact, out _),
            "finite blade motion produces a manual contact plane");
        Check(ContactSliceGeometry.TryCandidates(contact, out var candidates, out _)
            && candidates.Length == ContactSliceGeometry.CandidateCount
            && candidates.All(plane => Math.Abs(CutPoint.Dot(plane.Normal.Normalized, point) - plane.Offset) < 1e-12
                && CutPoint.Dot(plane.Normal, contact.Normal) >= Math.Cos(ContactSliceGeometry.MaximumTilt) - 1e-12),
            "bounded contact fallback planes all pass through the confirmed hit within the maximum tilt");
        var axes = ContactSliceGeometry.AxisCandidates(contact, [new(-2, 0, 0), new(0, 3, 0), new(0, 0, 4)]);
        Check(axes.Length == 3 && axes.All(p => Math.Abs(p.Normal.Length - 1) < 1e-12
                && Math.Abs(CutPoint.Dot(p.Normal, point) - p.Offset) < 1e-12)
            && axes.Select(p => CutPoint.Dot(p.Normal, contact.Normal)).SequenceEqual(
                axes.Select(p => CutPoint.Dot(p.Normal, contact.Normal)).OrderDescending()),
            "last-resort part-axis sections stay anchored and try the closest direction first");
        Check(ContactSliceGeometry.AxisCandidates(contact, [default, new(double.NaN, 0, 0), new(double.MaxValue, 0, 0)]).Length == 0,
            "invalid part axes cannot invent a fallback cut");
        Check(ContactSliceGeometry.TryCreate(point, new(0, 1, 0), new(0, 4, 0), out var thrust, out _)
            && ContactSliceGeometry.TryCreate(point, new(0, 1, 0), new(0, 4, 0), out var repeated, out _)
            && thrust.Normal == repeated.Normal && thrust.Normal.Finite && Math.Abs(thrust.Normal.Length - 1) < 1e-12,
            "near-collinear thrust uses a deterministic finite perpendicular plane normal");
        Check(!ContactSliceGeometry.TryCreate(point, default, new(1, 0, 0), out _, out _)
            && !ContactSliceGeometry.TryCreate(new(double.NaN, 0, 0), new(0, 1, 0), new(1, 0, 0), out _, out _),
            "zero or nonfinite manual contact vectors refuse instead of inventing a cut direction");
    }

    private static void ContactAnchorsWorkerPlane()
    {
        var contact = new ContactSlice(new(0, .31, .62), new(0, 0, 1));
        var input = Input([Box()], contact);
        Check(input.Contact == contact, "contact payload survives construction of the worker input");
        var cut = Calculate(input);
        Check(!cut.Faceted && !cut.AuthoredSubpartFallback && !cut.ContactDirectionFallback && cut.Attempts == 1 && cut.Fragments.Length == 2,
            "manual contact uses the preferred planar cut without faceted or authored breakup fallback");
        Check(Math.Abs(CutPoint.Dot(cut.Plane.Normal.Normalized, contact.Point) - cut.Plane.Offset) < 1e-5,
            "worker cap plane stays on the off-center surface hit");
        Check(cut.Fragments.All(f => f.MassKg is > .3f and < .7f && f.Glb.Length > 0
                && f.Uploads.Length > 0 && f.Extents.X > 0 && f.Extents.Y > 0 && f.Extents.Z > 0)
            && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - input.MassKg) < 1e-5,
            "contact cut exports two finite, usable fragments with conserved mass");
    }

    private static void GrazingFallbackIsBoundedAndRepeatable()
    {
        // The preferred plane lies on the box boundary and cannot split it. A small
        // deterministic tilt can still cut through this same surface point.
        var input = Input([Box()], new ContactSlice(new(0, .5, .5), new(1, 0, 0)));
        var first = Calculate(input);
        var second = Calculate(input);
        Check(first.Attempts is > 1 and <= 9 && first.Fragments.Length == 2 && !first.Faceted,
            "grazing contact advances only through the bounded planar fallback set");
        Check(Math.Abs(CutPoint.Dot(first.Plane.Normal.Normalized, input.Contact!.Value.Point) - first.Plane.Offset) < 1e-5
            && first.Plane == second.Plane && first.Attempts == second.Attempts
            && first.Fragments.Select(f => f.Id).SequenceEqual(second.Fragments.Select(f => f.Id)),
            "grazing fallback remains anchored and returns the same fragments for identical contact input");
    }

    private static void ImpossibleContactRefusesAuthoredOrCenterFallback()
    {
        var groups = new[] { Box(), Move(Box(), new(2, 0, 0)) };
        var contactInput = Input(groups, new ContactSlice(new(100, 100, 100), new(0, 0, 1)));
        var authoredInput = contactInput with { Contact = null };
        Check(QuietSubpartBreakup.TryBuild(authoredInput, out var authored, out _, CancellationToken.None)
            && authored!.AuthoredSubpartFallback,
            "two closed authored subparts make a real breakup fallback available to this fixture");
        bool refused = false;
        try { Calculate(contactInput); }
        catch (InvalidDataException error) { refused = error.Message.Contains("Cannot slice at contact", StringComparison.Ordinal); }
        Check(refused,
            "impossible manual contact refuses without moving to a center plane or authored-subpart breakup");
    }

    private static void CrossingContourUsesWiderAnchoredFallback()
    {
        // A folded authored corner has crossing sections around the preferred
        // direction, but a wider tilt through the same surface hit can divide it.
        var folded = FoldedBox();
        var contact = new ContactSlice(new(.5, 0, .5), new(0, 0, 1));
        Check(PlaneSlicer.TryAudit(folded, out _) && ContactSliceGeometry.TryCandidates(contact, out _, out _),
            "folded fixture has closed source topology and a confirmed surface contact");
        ContactSliceGeometry.TryCandidates(contact, out var candidates, out _);
        bool crossing = false;
        foreach (var plane in candidates.Take(9))
        {
            Check(!PlaneSlicer.TrySliceCompound([folded], plane, out var rejected, out var reason) && rejected == null,
                "nearby folded section refuses without returning partial geometry");
            crossing |= reason.Contains("Projected cut contours", StringComparison.Ordinal);
        }
        Check(crossing, "folded fixture reproduces the reported crossing-contour error");
        var input = Input([folded], contact);
        var cut = Calculate(input); var repeat = Calculate(input);
        Check(cut.Attempts is > 9 and <= ContactSliceGeometry.CandidateCount
            && !cut.Faceted && !cut.AuthoredSubpartFallback && !cut.ContactDirectionFallback && cut.Fragments.Length == 2
            && Math.Abs(CutPoint.Dot(cut.Plane.Normal, contact.Point) - cut.Plane.Offset) < 1e-5
            && CutPoint.Dot(cut.Plane.Normal, contact.Normal) >= Math.Cos(ContactSliceGeometry.MaximumTilt) - 1e-12
            && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - input.MassKg) < 1e-5
            && cut.Fragments.All(f => f.Glb.Length > 0 && f.Uploads.Length > 0),
            "wider fallback exports two mass-conserving fragments through the same contact within the tilt bound");
        Check(cut.Plane == repeat.Plane && cut.Attempts == repeat.Attempts
            && cut.Fragments.Select(f => f.Id).SequenceEqual(repeat.Fragments.Select(f => f.Id)),
            "wider contact recovery is deterministic");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool stopped = false, published = false;
        try { SliceGeometryWork.Calculate(input, cancellation.Token, _ => published = true); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && !published, "cancelled contact recovery never publishes a slice plan");
    }

    private static void CrossingContourUsesPartAxisFallback()
    {
        var folded = FoldedBox();
        var contact = new ContactSlice(new(0, .31, .62), new(0, 0, 1));
        ContactSliceGeometry.TryCandidates(contact, out var candidates, out _);
        Check(candidates.All(p => !PlaneSlicer.TrySliceCompound([folded], p, out _, out _)),
            "all 25 sweep-angle sections refuse the difficult folded contact");
        var input = Input([folded], contact);
        var cut = Calculate(input); var repeat = Calculate(input);
        Check(cut.ContactDirectionFallback && cut.Attempts is > ContactSliceGeometry.CandidateCount and <= ContactSliceGeometry.MaximumCandidateCount
            && cut.Fragments.Length == 2 && !cut.AuthoredSubpartFallback && !cut.Faceted
            && Math.Abs(CutPoint.Dot(cut.Plane.Normal, contact.Point) - cut.Plane.Offset) < 1e-5
            && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - input.MassKg) < 1e-5
            && ContactSliceGeometry.AxisCandidates(contact, input.Axes).Any(p => (p.Normal - cut.Plane.Normal).Length < 1e-12),
            "last-resort axis fallback exports an actual cut through the hit and reports its direction adjustment");
        Check(cut.Plane == repeat.Plane && cut.Fragments.Select(f => f.Id).SequenceEqual(repeat.Fragments.Select(f => f.Id)),
            "part-axis recovery returns deterministic geometry");
    }

    private static SliceFace[] FoldedBox()
    {
        SliceVertex Fold(SliceVertex vertex) => vertex.Position == new CutPoint(1, 1, 1)
            ? vertex with { Position = new(-1, 1, 1.5) } : vertex;
        return Box().Select(f => new SliceFace(Fold(f.A), Fold(f.B), Fold(f.C), f.Material)).ToArray();
    }

    private static void AssistedInputKeepsItsExistingPath()
    {
        var input = Input([Box()]);
        Check(input.Contact is null, "assisted worker input defaults to no contact payload");
        var assisted = Calculate(input);
        Check(assisted.Faceted && !assisted.AuthoredSubpartFallback && !assisted.ContactDirectionFallback && assisted.Fragments.Length == 2,
            "assisted worker retains its faceted section path when no manual contact is supplied");
    }

    private static SliceInput Input(SliceFace[][] groups, ContactSlice? contact = null) =>
        new(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], Materials, 1, 17) { Contact = contact };

    private static CpuSlice Calculate(SliceInput input) =>
        SliceGeometryWork.Calculate(input, CancellationToken.None, _ => { });

    private static SliceFace[] Move(SliceFace[] source, CutPoint offset) => source.Select(face => new SliceFace(
        face.A with { Position = face.A.Position + offset },
        face.B with { Position = face.B.Position + offset },
        face.C with { Position = face.C.Position + offset }, face.Material)).ToArray();

    private static SliceFace[] Box()
    {
        var faces = new List<SliceFace>();
        Quad(new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1));
        Quad(new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0));
        Quad(new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0));
        Quad(new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1));
        Quad(new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1));
        Quad(new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0));
        return faces.ToArray();

        void Quad(CutPoint a, CutPoint b, CutPoint c, CutPoint d)
        {
            var normal = CutPoint.Cross(b - a, c - a).Normalized;
            SliceVertex Vertex(CutPoint point) => new(point, normal, point.X, point.Y);
            faces.Add(new(Vertex(a), Vertex(b), Vertex(c), 0));
            faces.Add(new(Vertex(a), Vertex(c), Vertex(d), 0));
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Contact slice regression: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
