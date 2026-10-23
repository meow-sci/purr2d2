using Brutal.Numerics;
using Purr2D2.Core;

internal static class OverlayDragChecks
{
    public static void Run()
    {
        Check(SaberOverlayDrag.ContainsCircle(new(10, 0), float2.Zero, 10)
            && !SaberOverlayDrag.ContainsCircle(new(9, 9), float2.Zero, 10)
            && !SaberOverlayDrag.ContainsCircle(new(float.NaN, 0), float2.Zero, 10),
            "round overlay includes its boundary and leaves square corners click-through");
        Near(SaberOverlayDrag.ClampDisk(new(30, 40)), new float2(.6f, .8f),
            "diagonal drags clamp radially without changing direction");

        var drag = new SaberOverlayDrag();
        var grip = new float3(-13, -8, 18);
        var aim = new float3(.05f, .8f, .6f); // Deliberately not normalized: restoration must be exact.
        Check(!drag.Begin(float2.Zero, 0, grip, aim)
            && !drag.Begin(float2.Zero, 10, grip, float3.Zero), "invalid drag baselines are rejected");
        Check(drag.Begin(new(100, 100), 80, grip, aim), "valid drag snapshots its baseline");
        Check(drag.Advance(.1, out var resultGrip, out var resultAim) && resultGrip == grip && resultAim == aim,
            "pressing without moving preserves the exact configured pose");
        drag.Move(new(10_000, 10_000));
        for (int i = 0; i < 8; i++) drag.Advance(.1, out resultGrip, out resultAim);
        Check(drag.Offset.Length() <= 1.000001f && MathF.Abs(resultGrip.X - grip.X) <= 9
            && MathF.Abs(resultGrip.Y - grip.Y) <= 10 && resultGrip.Z == grip.Z,
            "arbitrarily long drags stay within a small arm reach range");
        Check(MathF.Abs(resultAim.Length() - aim.Length()) < 2e-5f
            && float3.Dot(resultAim, aim) / aim.LengthSquared() > .55f,
            "drag aim preserves magnitude and stays within a plausible forward arc");
        var moved = drag.Offset;
        drag.Release();
        Check(!drag.Held && drag.Active && drag.Offset == moved, "release starts from the displayed pose without snapping");
        drag.Advance(.08, out _, out _);
        Check(drag.Offset.Length() < moved.Length() && drag.Offset.Length() > 0,
            "released pose eases toward the baseline");
        for (int i = 0; i < 3; i++) drag.Advance(.08, out resultGrip, out resultAim);
        Check(!drag.Active && resultGrip == grip && resultAim == aim,
            "return finishes at the exact pre-drag grip and unnormalized aim");
        Check(!drag.Advance(.1, out _, out _), "completed drag relinquishes pose ownership");

        drag.Begin(float2.Zero, 80, grip, aim);
        drag.Move(new(70, -40));
        drag.Advance(.1, out _, out _);
        drag.Release();
        drag.Advance(.08, out resultGrip, out resultAim);
        moved = drag.Offset;
        Check(drag.Begin(new(230, 450), 80, resultGrip, resultAim), "a returning gesture can be re-grabbed");
        drag.Move(new(230, 450));
        drag.Advance(.1, out var regrabGrip, out var regrabAim);
        Check((drag.Offset - moved).Length() < 1e-6f && (regrabGrip - resultGrip).Length() < 1e-5f
            && (regrabAim - resultAim).Length() < 1e-5f && drag.OriginalGrip == grip && drag.OriginalAim == aim,
            "re-grabbing preserves the current pose and the original baseline without drift");
        drag.Release();
        drag.Advance(0, out var pausedGrip, out var pausedAim);
        Check(pausedGrip == regrabGrip && pausedAim == regrabAim, "pause freezes a held or returning offset exactly");
        for (int i = 0; i < 3; i++) drag.Advance(.1, out resultGrip, out resultAim);
        Check(!drag.Active && resultGrip == grip && resultAim == aim,
            "re-grabbed gesture returns to the original configured pose exactly");

        drag.Begin(float2.Zero, 80, new(39, -39, 18), aim);
        drag.Move(new(-80, 80));
        for (int i = 0; i < 4; i++) drag.Advance(.1, out resultGrip, out _);
        Check(resultGrip.X <= 40 && resultGrip.Y >= -40, "configured boundary poses cannot exceed preference limits");
        moved = drag.Offset;
        drag.Move(new(float.NaN, 0));
        drag.Advance(double.NaN, out _, out _);
        Check(drag.Offset == moved, "invalid pointer and elapsed time cannot inject nonfinite pose values");
        drag.Reset();
        Check(!drag.Active && !drag.Held && drag.Offset == float2.Zero,
            "world or ownership reset clears the temporary gesture");
        PointerOwnership();
        PairedConsumerOwnership();
        LayoutBounds();
        SomersaultPressOwnership();
    }

    private static void PairedConsumerOwnership()
    {
        var capture = new SaberOverlayPointerCapture();
        Check(!capture.Consume(0, true, false, false, 1) && !capture.SuppressImGuiButton(0, true, 1)
            && !capture.Consume(0, false, true, false, 2) && !capture.SuppressImGuiButton(0, false, 2),
            "a native ImGui press and its release pass both consumers even after entering the overlay");
        Check(capture.Consume(0, true, true, false, 3) && capture.SuppressImGuiButton(0, true, 3)
            && capture.IsHeld(0), "an overlay press is hidden from covered ImGui widgets and remains held in raw state");
        Check(capture.Consume(0, false, false, false, 4) && !capture.IsHeld(0)
            && capture.SuppressImGuiButton(0, false, 4),
            "game release preserves the backend suppression decision until that same release is delivered");
        Check(!capture.Consume(0, true, false, false, 5) && !capture.SuppressImGuiButton(0, true, 5)
            && !capture.SuppressImGuiButton(0, false, 6), "a completed overlay gesture cannot swallow a fresh native click");

        capture.Consume(0, true, true, false, 7);
        capture.SuppressImGuiButton(0, true, 7);
        Check(!capture.SuppressImGuiButton(1, false, 7) && capture.IsHeld(0),
            "one consumer's unrelated release cannot clear another button's ownership");
        Check(capture.SuppressImGuiButton(0, false, 8) && !capture.IsHeld(0)
            && !capture.Consume(0, false, false, false, 8),
            "a backend-first platform release completes only its captured gesture");

        Check(capture.OwnsScroll(true) && !capture.OwnsScroll(false),
            "scroll is blocked from covered widgets only inside actual overlay hit geometry");
        capture.Consume(0, true, true, false, 9);
        Check(capture.OwnsScroll(false), "held overlay gestures keep outside scroll away from native consumers");
        capture.Reset();
        Check(!capture.SuppressImGuiButton(0, true, 10) && !capture.SuppressImGuiButton(0, false, 10)
            && !capture.OwnsScroll(false), "focus loss clears game and backend ownership together");
    }

    private static void LayoutBounds()
    {
        foreach (var (size, font) in new[] { (new float2(1920, 1080), 16f), (new float2(3840, 2160), 32f),
            (new float2(800, 600), 16f), (new float2(240, 220), 16f), (new float2(1920, 220), 32f),
            (new float2(240, 1080), 32f), (new float2(360, 300), 12f) })
        {
            var origin = new float2(120, 80); // A viewport need not start at desktop (0,0).
            var layout = SaberOverlayLayout.Create(origin, size, font, 100);
            Check(layout.Valid && layout.ListMin.X >= origin.X && layout.ListMin.Y >= origin.Y
                && layout.ListMax.X <= origin.X + size.X && layout.ListMax.Y < layout.SwingCenter.Y - layout.SwingRadius
                && layout.SwingCenter.X - layout.SwingRadius >= origin.X
                && layout.SwingCenter.Y - layout.SwingRadius >= origin.Y
                && layout.SwingCenter.Y + layout.SwingRadius <= origin.Y + size.Y
                && layout.SwingCenter.X + layout.SwingRadius < layout.SliceCenter.X - layout.SliceRadius
                && layout.SliceCenter.X + layout.SliceRadius <= origin.X + size.X,
                $"overlay anchors and nonoverlapping controls fit {size.X} x {size.Y}");
            Check(MathF.Abs(layout.SwingRadius - 132 * layout.Scale) < 1e-4f
                && MathF.Abs(layout.SliceRadius - 27 * layout.Scale) < 1e-4f
                && MathF.Abs(origin.X + size.X - layout.SliceCenter.X - layout.SliceRadius - 80 * layout.Scale) < 1e-3f,
                "larger swing and smaller slice circles retain the requested left shift and gap");
            Check(layout.Hit(layout.SwingCenter) == SaberOverlayHit.Swing
                && layout.Hit(layout.SwingCenter + new float2(layout.SwingRadius * .9f)) == SaberOverlayHit.None
                && layout.Hit(layout.SliceCenter) == SaberOverlayHit.Slice,
                "drawing geometry and pointer geometry agree without square corner capture");
            var circles = new[] {
                (layout.SwingCenter, layout.SwingRadius, SaberOverlayHit.Swing),
                (layout.SliceCenter, layout.SliceRadius, SaberOverlayHit.Slice),
                (layout.IgniteCenter, layout.IgniteRadius, SaberOverlayHit.Ignite),
                (layout.ForwardCenter, layout.SomersaultRadius, SaberOverlayHit.SomersaultForward),
                (layout.BackwardCenter, layout.SomersaultRadius, SaberOverlayHit.SomersaultBackward),
                (layout.LeftCenter, layout.SomersaultRadius, SaberOverlayHit.SomersaultLeft),
                (layout.RightCenter, layout.SomersaultRadius, SaberOverlayHit.SomersaultRight) };
            bool contained = true, separate = true, exactHits = true;
            for (int i = 0; i < circles.Length; i++)
            {
                var (center, radius, hit) = circles[i];
                contained &= center.X - radius >= origin.X - 1e-4f && center.Y - radius >= origin.Y - 1e-4f
                    && center.X + radius <= origin.X + size.X + 1e-4f
                    && center.Y + radius <= origin.Y + size.Y + 1e-4f;
                exactHits &= layout.Hit(center) == hit
                    && layout.Hit(center + new float2(radius * .9f)) == SaberOverlayHit.None;
                for (int j = i + 1; j < circles.Length; j++)
                    separate &= (center - circles[j].Item1).Length() > radius + circles[j].Item2;
            }
            Check(contained && separate && exactHits && layout.SomersaultRadius == layout.SliceRadius
                && layout.IgniteRadius == layout.SliceRadius && layout.IgniteCenter.X < layout.SwingCenter.X
                && layout.IgniteCenter.Y < layout.SwingCenter.Y && layout.IgniteCenter.X + layout.IgniteRadius < layout.ListMin.X,
                "seven separate circular hit regions fit; ignite sits above-left of swing and matches slice size");
            Check(layout.ForwardCenter.X == layout.BackwardCenter.X
                && layout.LeftCenter.Y == layout.RightCenter.Y
                && layout.ForwardCenter.Y < layout.LeftCenter.Y && layout.LeftCenter.Y < layout.BackwardCenter.Y
                && layout.LeftCenter.X < layout.ForwardCenter.X && layout.RightCenter.X > layout.ForwardCenter.X
                && layout.ForwardCenter.Y - layout.SomersaultRadius > layout.SwingCenter.Y + layout.SwingRadius
                && layout.StatusMin.Y > layout.BackwardCenter.Y + layout.SomersaultRadius
                && layout.StatusMin.X >= origin.X && layout.StatusMax.X <= origin.X + size.X
                && layout.StatusMax.Y <= origin.Y + size.Y && layout.StatusMax.Y - layout.StatusMin.Y >= 48 * layout.Scale - 1e-3f,
                "forward/left/right/back form a diamond below swing/slice with a separate visible status area");
            var hidden = SaberOverlayLayout.Create(origin, size, font, 100, showSomersaultButtons: false);
            Check(hidden.Valid && !hidden.ShowSomersaultButtons && hidden.SwingCenter == layout.SwingCenter
                && hidden.ListMax == layout.ListMax && hidden.StatusMin == layout.StatusMin
                && hidden.Hit(layout.SwingCenter) == SaberOverlayHit.Swing && hidden.Hit(layout.SliceCenter) == SaberOverlayHit.Slice
                && hidden.Hit(layout.IgniteCenter) == SaberOverlayHit.Ignite
                && circles.Skip(3).All(circle => hidden.Hit(circle.Item1) == SaberOverlayHit.None),
                "airborne somersault circles become click-through while the remaining overlay keeps stable anchors");
            var swatch = layout.ColorSwatchMin(0, 0);
            Check(swatch.X > layout.ListMin.X + layout.Padding + layout.ColorSwatchSize
                && swatch.X + layout.ColorSwatchSize < layout.ListMax.X
                && layout.IsColorSwatch(swatch + new float2(layout.ColorSwatchSize * .5f), 0, 0)
                && !layout.IsColorSwatch(new(layout.ListMin.X + layout.Padding, swatch.Y), 0, 0),
                "row swatches have independent square bounds away from the checkbox");
            var scrolled = layout.ColorSwatchMin(4, layout.RowHeight * 4);
            Check((scrolled - swatch).Length() < 1e-4f && layout.IsColorSwatch(scrolled, 4, layout.RowHeight * 4)
                && !layout.IsColorSwatch(layout.ColorSwatchMin(0, layout.RowHeight), 0, layout.RowHeight)
                && !layout.IsColorSwatch(new(float.NaN, 0), 0, 0),
                "swatch hit testing follows scroll and rejects clipped rows or invalid pointers");
        }
        Check(!SaberOverlayLayout.Create(float2.Zero, new(100, 100), 16, 2).Valid
            && SaberOverlayLayout.Create(float2.Zero, new(100, 100), 16, 2).Hit(float2.Zero) == SaberOverlayHit.None,
            "unusable viewport dimensions cannot leave an invisible hit target");
        Check(SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 24, 2, 74).ListMin.Y == 74,
            "the list clears the current native menu bar instead of assuming WorkPos excludes it");
        Check(MathF.Abs(SaberOverlayLayout.Create(new(120, 80), new(1920, 1080), 16, 100).SwingCenter.Y - 620) < 1e-4f,
            "large viewports retain the familiar half-height swing anchor");
        var inset = SaberOverlayLayout.Create(new(-400, -200), new(240, 220), 32, 100, 80);
        Check(inset.Valid && inset.ListMin.Y >= -120 && inset.ListMax.Y < inset.SwingCenter.Y - inset.SwingRadius
            && inset.StatusMax.Y <= 20 && inset.LeftCenter.X - inset.SomersaultRadius >= -400,
            "compact layout also fits a negative-origin viewport with a large native menu inset");
        Check(!SaberOverlayLayout.Create(float2.Zero, new(240, 220), 16, 100, 180).Valid
            && !SaberOverlayLayout.Create(float2.Zero, new(240, 220), float.NaN, 100).Valid
            && !SaberOverlayLayout.Create(float2.Zero, new(240, 220), 16, 100, float.NaN).Valid,
            "insufficient work area or nonfinite UI inputs cannot leave invisible controls");
    }

    private static void SomersaultPressOwnership()
    {
        var layout = SaberOverlayLayout.Create(new(120, 80), new(800, 600), 16, 2);
        var hidden = SaberOverlayLayout.Create(new(120, 80), new(800, 600), 16, 2, showSomersaultButtons: false);
        foreach (var center in new[] { layout.ForwardCenter, layout.BackwardCenter, layout.LeftCenter, layout.RightCenter })
        {
            var capture = new SaberOverlayPointerCapture();
            Check(capture.Consume(0, true, layout.Hit(center) != SaberOverlayHit.None, false, 1, out bool first) && first
                && capture.SuppressImGuiButton(0, true, 1)
                && capture.Consume(0, true, true, false, 2, out bool duplicate) && !duplicate,
                "a somersault click authorizes one action; held/duplicate presses stay consumed without repeating");
            Check(hidden.Hit(center) == SaberOverlayHit.None
                && capture.Consume(0, false, false, false, 3, out bool release) && !release
                && capture.SuppressImGuiButton(0, false, 3) && !capture.AnyHeld,
                "launch may hide its circle before release without leaking the paired release to native UI/camera");
            Check(!capture.Consume(0, true, false, false, 4, out bool outside) && !outside
                && !capture.Consume(0, false, true, false, 5, out bool entered) && !entered,
                "dragging from the world into a somersault circle never creates an action");
            // Availability is deliberately absent from pointer ownership: disabled circles capture too.
            Check(capture.Consume(0, true, layout.Hit(center) != SaberOverlayHit.None, false, 6, out bool disabled) && disabled,
                "a disabled visible somersault owns its click independently of action eligibility");
            capture.Reset();
            Check(!capture.AnyHeld && !capture.SuppressImGuiButton(0, false, 7)
                && !capture.Consume(0, false, true, false, 7, out _),
                "focus loss releases somersault pointer state without retaining an invisible target");
        }
    }

    private static void PointerOwnership()
    {
        var capture = new SaberOverlayPointerCapture();
        Check(!capture.Consume(1, true, false, false, 1)
            && !capture.Consume(1, false, true, false, 2),
            "a native camera drag that enters the circle retains its release");
        Check(!capture.Consume(0, true, true, true, 3),
            "a native held gesture cannot be stolen by an overlay press");
        Check(capture.Consume(0, true, true, false, 4) && capture.AnyHeld,
            "an overlay press takes pointer ownership");
        Check(capture.Consume(1, true, false, false, 4)
            && capture.Consume(0, false, false, false, 5) && capture.AnyHeld,
            "extra buttons during overlay capture are owned independently");
        Check(capture.Consume(1, false, false, false, 6) && !capture.AnyHeld && capture.EventFrame == 6,
            "release outside a hidden or reset overlay is consumed and leaves a final-frame guard");
        Check(!capture.Consume(1, false, true, false, 7), "a captured release cannot consume later native releases");
        capture.Consume(0, true, true, false, 8);
        capture.Reset();
        Check(!capture.AnyHeld && capture.EventFrame == ulong.MaxValue,
            "focus loss clears pointer ownership without a stuck gesture");
    }

    private static void Near(float2 actual, float2 expected, string message) => Check((actual - expected).Length() < 1e-6f, message);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Overlay drag check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
