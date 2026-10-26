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
        DeploymentDrag();
        AutoSwingStateAndDrag();
        PointerOwnership();
        PairedConsumerOwnership();
        LayoutBounds();
        SomersaultPressOwnership();
        ManualSwooshGate();
    }

    private static void DeploymentDrag()
    {
        var drag = new SaberDeploymentDrag();
        Check(!drag.Begin(float.NaN, 10, 110) && !drag.Begin(50, 110, 10)
            && !drag.Begin(9, 10, 110) && !drag.Begin(50, float.NaN, 110)
            && !drag.Begin(50, 10, float.PositiveInfinity),
            "deployment drag rejects nonfinite, reversed and outside-track starts");
        Check(drag.Begin(35, 10, 110) && drag.Value == .25f
            && drag.Release(35) == .25f
            && drag.Begin(60, 10, 110) && drag.Value == .5f
            && drag.Release(60) == .5f
            && drag.Begin(85, 10, 110) && drag.Value == .75f,
            "absolute clicks select 25, 50 and 75 percent independently of the prior target");
        Check(drag.Move(85) == .75f && drag.Release(85) == .75f,
            "re-grabbing and releasing at the current fraction does not jump");
        Check(drag.Begin(50, 10, 110) && drag.Move(float.NaN) == .4f,
            "nonfinite pointer motion keeps the last finite deployment");
        Check(MathF.Abs(drag.Move(70) - .6f) < 1e-6f && drag.Move(200) == 1,
            "absolute horizontal motion maps directly to the blade fraction and clamps at the upper endpoint");
        Check(drag.MatchesTrack(10, 110) && !drag.MatchesTrack(10, 120),
            "a resize invalidates the captured deployment track");
        Check(drag.Release(10) == 0 && !drag.Held,
            "release applies its final pointer coordinate and clamps at the lower endpoint");
        Check(drag.Begin(70, 10, 110) && drag.Value == .6f,
            "a later press selects the absolute track position instead of an on/off baseline");
        Check(drag.Release(float.NaN) == .6f, "a nonfinite release preserves the last valid fraction");
        Check(drag.Begin(0, -float.MaxValue, float.MaxValue) && drag.Value == .5f
            && drag.Move(float.MaxValue) == 1,
            "finite extreme track coordinates use double precision and cannot overflow the deployment fraction");
    }

    private static void AutoSwingStateAndDrag()
    {
        var swing = new SaberAutoSwing();
        Check(!swing.Enabled && swing.DurationSeconds == .5f && swing.Phase == 0,
            "automatic swing starts disabled at the half-second default");
        Check(!swing.SetDuration(float.NaN) && swing.SetDuration(0) && swing.DurationSeconds == .5f
            && swing.SetDuration(4) && swing.DurationSeconds == 3,
            "duration rejects nonfinite input and clamps to half through three seconds");
        bool symmetricStroke = true;
        foreach (double progress in new[] { 0d, .01, .1, .2, .35, .5, .65, .8, .9, .99, 1 })
            symmetricStroke &= Math.Abs(SaberAutoSwing.StrokePosition(1 - progress)
                - (1 - SaberAutoSwing.StrokePosition(progress))) < 1e-12;
        double middleStep = SaberAutoSwing.StrokePosition(.4) - SaberAutoSwing.StrokePosition(.3);
        bool steadyMiddle = Math.Abs(middleStep - (SaberAutoSwing.StrokePosition(.5) - SaberAutoSwing.StrokePosition(.4))) < 1e-12
            && Math.Abs(middleStep - (SaberAutoSwing.StrokePosition(.7) - SaberAutoSwing.StrokePosition(.6))) < 1e-12;
        double nearLeftVelocity = SaberAutoSwing.StrokePosition(.001) / .001;
        double nearRightVelocity = (1 - SaberAutoSwing.StrokePosition(.999)) / .001;
        Check(symmetricStroke && steadyMiddle && nearLeftVelocity < 1e-3 && nearRightVelocity < 1e-3
            && middleStep > nearLeftVelocity * .1,
            "horizontal stroke eases symmetrically to zero endpoint speed and moves steadily through its faster middle");
        swing.SetDuration(1.25f);
        swing.SetEnabled(true);
        swing.GetOffset(out float x, out float y);
        Check(swing.EntryBlend == 0 && x == 0 && y == 0,
            "automatic swing enters from the neutral calibrated pose");
        swing.Advance(.1); swing.Advance(.05);
        Check(MathF.Abs(swing.EntryBlend - 1) < 1e-6f,
            "the automatic pose eases fully in over roughly 0.15 player seconds");
        swing.SetEnabled(false);
        Check(swing.DurationSeconds == 1.25f && !swing.Enabled,
            "disabling clears the transient phase while retaining the selected stroke duration");

        swing.SetDuration(.5f);
        swing.SetEnabled(true);
        for (int i = 0; i < 5; i++) swing.Advance(.1);
        swing.GetOffset(out x, out y);
        Check(Math.Abs(swing.Phase - 1) < 1e-9 && MathF.Abs(x - .8f) < 1e-5f && MathF.Abs(y) < 1e-5f,
            "a half-second one-way stroke reaches the opposite endpoint with a stationary turnaround");
        swing.Advance(.1); swing.Advance(.1); swing.Advance(.05);
        swing.GetOffset(out x, out y);
        Check(Math.Abs(swing.Phase - 1.5) < 1e-9 && MathF.Abs(x) < 1e-5f && MathF.Abs(y + .18f) < 1e-5f,
            "the middle of the stroke follows a shallow upward squared-sine arch");
        swing.Advance(.1); swing.Advance(.1); swing.Advance(.05);
        swing.GetOffset(out x, out y);
        Check(Math.Abs(swing.Phase) < 1e-9 && MathF.Abs(x + .8f) < 1e-5f && MathF.Abs(y) < 1e-5f,
            "the return stroke takes the same duration and wraps the bounded phase");
        double heldPhase = swing.Phase;
        for (int i = 0; i < 4; i++) { swing.Advance(0); swing.Advance(double.NaN); }
        Check(swing.Phase == heldPhase, "pause, nonfinite time and a clash hold supplied as zero freeze phase and entry");
        swing.Advance(.1);
        Check(Math.Abs(swing.Phase - .2) < 1e-9,
            "the first supported positive time resumes smoothly without catch-up");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) { swing.Advance(.01); swing.GetOffset(out _, out _); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "warmed automatic swing phase and pose evaluation allocate no managed memory");
        swing.Reset();
        Check(!swing.Enabled && swing.DurationSeconds == .5f && swing.Phase == 0,
            "full owner reset restores the transient per-kitten duration default");

        var drag = new SaberAutoSwingDrag();
        var center = new float2(200, 300);
        float radius = 27;
        var pillMin = center - new float2(radius);
        var pillMax = center + new float2(radius, radius + 120);
        Check(!drag.Begin(new(float.NaN, 0), .5f, center, radius, pillMin, pillMax, 4)
            && !drag.Begin(center + new float2(radius + 1, 0), .5f, center, radius, pillMin, pillMax, 4)
            && drag.Begin(center + new float2(1, 0), 1, center, radius, pillMin, pillMax, 4),
            "duration gesture rejects invalid/outside presses and captures the current duration inside the circle");
        Check(MathF.Abs(drag.Move(center + new float2(1, 3)) - 1) < 1e-6f && !drag.Dragged,
            "motion below the scaled four-pixel threshold remains a click");
        Check(drag.Release(center + new float2(1, 3)) && !drag.Dragged && !drag.Held
            && drag.Value == 1 && !drag.Release(center),
            "a click release is single-shot and keeps the original duration for the caller to toggle");

        Check(drag.Begin(center, 1, center, radius, pillMin, pillMax, 4)
            && MathF.Abs(drag.Move(center + new float2(0, 60)) - 2.25f) < 1e-6f
            && drag.Dragged && drag.MatchesLayout(center, radius, pillMin, pillMax)
            && !drag.MatchesLayout(center, radius, pillMin, pillMax + new float2(0, 1)),
            "drag duration is press-relative across the 120-pixel capsule and its layout is captured");
        Check(MathF.Abs(drag.Move(center + new float2(0, -120)) - .5f) < 1e-6f
            && drag.Move(center + new float2(0, 120)) == 3,
            "vertical movement reaches both duration limits and can reverse direction");
        float last = drag.Value;
        Check(drag.Move(new(float.NaN, 0)) == last && drag.Release(new(float.NaN, 0)) && !drag.Held,
            "nonfinite motion or release preserves the last accepted duration and ends once");

        Check(drag.Begin(center, .5f, center, radius, pillMin, pillMax, 4)
            && drag.Release(center + new float2(0, 8)) && drag.Dragged
            && MathF.Abs(drag.Value - (float)(.5 + 8d / 120 * 2.5)) < 1e-5f,
            "the final release coordinate can cross the drag threshold and supplies the committed endpoint");
        float selected = drag.Value;
        drag.Reset();
        Check(drag.Begin(center, selected, center, radius, pillMin, pillMax, 4)
            && drag.Value == selected && MathF.Abs(drag.Move(center + new float2(0, 12)) - (selected + .25f)) < 1e-5f,
            "a later gesture re-grabs the current per-kitten duration as its own baseline");
        drag.Cancel();
        Check(!drag.Held && drag.Value > selected,
            "cancel stops capture without converting the duration gesture into an enable action");
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
                && layout.SliceCenter.X + layout.SliceRadius <= origin.X + size.X,
                $"overlay anchors and nonoverlapping controls fit {size.X} x {size.Y}");
            Check(MathF.Abs(layout.SwingRadius - 132 * layout.Scale) < 1e-4f
                && MathF.Abs(layout.SliceRadius - 27 * layout.Scale) < 1e-4f
                && layout.SliceCenter.X < layout.SwingCenter.X
                && layout.SliceCenter.Y > layout.SwingCenter.Y
                && (layout.SliceCenter - layout.SwingCenter).Length() > layout.SwingRadius + layout.SliceRadius,
                "slice takes the upper action position below-left without touching swing");
            Check(layout.Hit(layout.SwingCenter) == SaberOverlayHit.Swing
                && layout.Hit(layout.SwingCenter + new float2(layout.SwingRadius * .9f)) == SaberOverlayHit.None
                && layout.Hit(layout.SliceCenter) == SaberOverlayHit.Slice
                && layout.Hit(layout.AutoSwingCenter) == SaberOverlayHit.AutoSwing,
                "drawing geometry and pointer geometry agree without square corner capture");
            var circles = new[] {
                (layout.SwingCenter, layout.SwingRadius, SaberOverlayHit.Swing),
                (layout.SliceCenter, layout.SliceRadius, SaberOverlayHit.Slice),
                (layout.IgniteCenter, layout.IgniteRadius, SaberOverlayHit.Ignite),
                (layout.AutoSwingCenter, layout.IgniteRadius, SaberOverlayHit.AutoSwing),
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
                var outsideCorner = center + new float2(radius * .9f);
                var cornerTargets = circles.Where(circle => SaberOverlayDrag.ContainsCircle(
                    outsideCorner, circle.Item1, circle.Item2)).ToArray();
                exactHits &= layout.Hit(center) == hit
                    && !SaberOverlayDrag.ContainsCircle(outsideCorner, center, radius)
                    && (cornerTargets.Length == 0
                        ? layout.Hit(outsideCorner) == SaberOverlayHit.None
                        : cornerTargets.Length == 1 && layout.Hit(outsideCorner) == cornerTargets[0].Item3);
                for (int j = i + 1; j < circles.Length; j++)
                    separate &= (center - circles[j].Item1).Length() > radius + circles[j].Item2;
            }
            Check(contained && separate && exactHits && layout.SomersaultRadius == layout.SliceRadius
                && layout.IgniteRadius == layout.SliceRadius && layout.IgniteCenter.X < layout.SwingCenter.X
                && layout.IgniteCenter.Y > layout.SwingCenter.Y
                && layout.IgniteCenter.X > layout.SliceCenter.X && layout.IgniteCenter.Y > layout.SliceCenter.Y
                && MathF.Abs((layout.IgniteCenter.X - layout.SwingCenter.X) / layout.Scale + 94) < 1e-3f
                && MathF.Abs((layout.IgniteCenter.Y - layout.SwingCenter.Y) / layout.Scale
                    - (132 + 27 + 12)) < 1e-3f
                && MathF.Abs((layout.AutoSwingCenter.X - layout.IgniteCenter.X) / layout.Scale - (54 + 12)) < 1e-3f
                && (layout.IgniteCenter - layout.SliceCenter).Length() > layout.IgniteRadius * 2
                && layout.AutoSwingPillMin.X >= origin.X - 1e-3f
                && layout.AutoSwingPillMax.X <= origin.X + size.X + 1e-3f
                && layout.AutoSwingPillMax.Y <= origin.Y + size.Y + 1e-3f
                && layout.AutoSwingPillMax.Y > layout.AutoSwingPillMin.Y + 2 * layout.IgniteRadius,
                "Ignite and Auto Swing sit in a separate row, and the duration capsule fits the viewport");
            Check(layout.ForwardCenter.Y - layout.SomersaultRadius
                    >= layout.AutoSwingCenter.Y + layout.IgniteRadius + 12 * layout.Scale - 1e-3f,
                "optional somersault buttons move below the Auto Swing row without overlapping idle hit targets");
            foreach (bool listVisible in new[] { false, true })
            foreach (bool controlsVisible in new[] { false, true })
            {
                var visible = layout with { ShowKittenList = listVisible, ShowLightsaberControls = controlsVisible };
                Check(visible.Hit((layout.ListMin + layout.ListMax) * .5f)
                        == (listVisible ? SaberOverlayHit.Kittens : SaberOverlayHit.None)
                    && visible.Hit(layout.SwingCenter) == (controlsVisible ? SaberOverlayHit.Swing : SaberOverlayHit.None)
                    && visible.Hit(layout.SliceCenter) == (controlsVisible ? SaberOverlayHit.Slice : SaberOverlayHit.None)
                    && visible.Hit(layout.IgniteCenter) == (controlsVisible ? SaberOverlayHit.Ignite : SaberOverlayHit.None)
                    && visible.Hit(layout.AutoSwingCenter) == (controlsVisible ? SaberOverlayHit.AutoSwing : SaberOverlayHit.None),
                    "list and lightsaber visibility independently remove hidden controls from pointer hit testing");
            }
            foreach (var direction in new[] { new float2(1, 0), new float2(0, 1), new float2(-1, 0), new float2(0, -1),
                SaberOverlayDrag.ClampDisk(new(1, 1)) })
            {
                var indicator = layout.SwingIndicator(direction);
                var edgeDrag = new SaberOverlayDrag();
                edgeDrag.Begin(layout.SwingCenter, layout.SwingTravelRadius, float3.Zero, float3.UnitZ);
                edgeDrag.Move(indicator);
                for (int i = 0; i < 8; i++) edgeDrag.Advance(.1, out _, out _);
                Check(MathF.Abs((indicator - layout.SwingCenter).Length() + layout.SwingMarkerRadius - layout.SwingRadius) < 1e-3f
                    && (layout.SwingIndicator(edgeDrag.Offset) - indicator).Length() < 1e-3f
                    && layout.Hit(indicator) == SaberOverlayHit.Swing,
                    "swing input and indicator share the full travel radius up to the visible edge");
            }
            Check(layout.ForwardCenter.X == layout.BackwardCenter.X
                && layout.LeftCenter.Y == layout.RightCenter.Y
                && layout.ForwardCenter.Y < layout.LeftCenter.Y && layout.LeftCenter.Y < layout.BackwardCenter.Y
                && layout.LeftCenter.X < layout.ForwardCenter.X && layout.RightCenter.X > layout.ForwardCenter.X
                && layout.ForwardCenter.Y - layout.SomersaultRadius > layout.SwingCenter.Y + layout.SwingRadius
                && MathF.Abs(layout.StatusMin.Y - layout.BackwardCenter.Y - layout.SomersaultRadius - 25 * layout.Scale) < 1e-3f
                && layout.StatusMin.X >= origin.X && layout.StatusMax.X <= origin.X + size.X
                && layout.StatusMax.Y <= origin.Y + size.Y
                && MathF.Abs(layout.StatusMax.Y - layout.StatusMin.Y - 96 * layout.Scale) < 1e-3f
                && MathF.Abs((layout.StatusMin.X + layout.StatusMax.X) * .5f - layout.SwingCenter.X) < 1e-3f
                && layout.ControlsMin.X >= origin.X - 1e-3f && layout.ControlsMin.Y >= origin.Y - 1e-3f
                && layout.ControlsMax.X <= origin.X + size.X + 1e-3f && layout.ControlsMax.Y <= origin.Y + size.Y + 1e-3f,
                "forward/left/right/back form a diamond below swing/slice with a centered, bounded status area");
            float expectedFirstSomersaultOffset = 132 + 2 * 27 + 12 + 12 + 27 + (66 - 27 / MathF.Sqrt(2));
            Check(MathF.Abs((layout.LeftCenter.Y - layout.SwingCenter.Y) / layout.Scale - expectedFirstSomersaultOffset) < 1e-3f
                && MathF.Abs((layout.LeftCenter - layout.ForwardCenter).Length() / layout.Scale
                    - (66 * MathF.Sqrt(2) - 27)) < 1e-3f,
                "the optional somersault diamond clears the Auto Swing row while preserving button spacing");
            var hidden = SaberOverlayLayout.Create(origin, size, font, 100, showSomersaultButtons: false);
            Check(hidden.Valid && !hidden.ShowSomersaultButtons
                && hidden.Hit(hidden.SwingCenter) == SaberOverlayHit.Swing && hidden.Hit(hidden.SliceCenter) == SaberOverlayHit.Slice
                && hidden.Hit(hidden.IgniteCenter) == SaberOverlayHit.Ignite
                && hidden.Hit(hidden.AutoSwingCenter) == SaberOverlayHit.AutoSwing
                && circles.Skip(4).All(circle => hidden.Hit(circle.Item1) == SaberOverlayHit.None),
                "airborne somersault circles become click-through while Auto Swing remains available");
            var swatch = layout.ColorSwatchMin(0, 0);
            Check(swatch.X > layout.ListMin.X + layout.Padding + layout.ColorSwatchSize
                && swatch.X + layout.ColorSwatchSize < layout.ListMax.X
                && layout.IsColorSwatch(swatch + new float2(layout.ColorSwatchSize * .5f), 0, 0)
                && !layout.IsColorSwatch(new(layout.ListMin.X + layout.Padding, swatch.Y), 0, 0),
                "row swatches have independent square bounds away from the checkbox");
            Check(MathF.Abs(layout.ColorSwatchSize - 28 * layout.Scale * .62f) < 1e-4f
                && layout.RowHeight > layout.TopRowHeight,
                "deployment bars add row height without changing checkbox or swatch dimensions");
            var bar = layout.DeploymentBarMin(0, 0);
            var barMax = layout.DeploymentBarMax(0, 0);
            var barCenter = new float2((bar.X + barMax.X) * .5f, (bar.Y + barMax.Y) * .5f);
            float leftInset = layout.ListMin.X + layout.Padding + 1;
            float rowTop = layout.ListBodyTop;
            Check(layout.IsDeploymentBar(barCenter, 0, 0)
                && layout.IsDeploymentBar(new(leftInset, rowTop + layout.TopRowHeight + 1), 0, 0)
                && !layout.IsDeploymentBar(new(leftInset, rowTop + layout.TopRowHeight - 1), 0, 0)
                && !layout.IsDeploymentBar(new(layout.ListMin.X + 1, barCenter.Y), 0, 0),
                "the full lower row strip hits the inset deployment control while the name row remains a toggle");
            var scrolled = layout.ColorSwatchMin(4, layout.RowHeight * 4);
            Check((scrolled - swatch).Length() < 1e-4f && layout.IsColorSwatch(scrolled, 4, layout.RowHeight * 4)
                && !layout.IsColorSwatch(layout.ColorSwatchMin(0, layout.RowHeight), 0, layout.RowHeight)
                && !layout.IsColorSwatch(new(float.NaN, 0), 0, 0),
                "swatch hit testing follows scroll and rejects clipped rows or invalid pointers");
            var scrolledBar = layout.DeploymentBarMin(4, layout.RowHeight * 4);
            Check((scrolledBar - bar).Length() < 1e-4f
                && layout.IsDeploymentBar(scrolledBar + new float2(1, layout.DeploymentBarHeight * .5f), 4, layout.RowHeight * 4)
                && layout.IsDeploymentBar(new(leftInset, rowTop + layout.TopRowHeight + 1), 4, layout.RowHeight * 4)
                && !layout.IsDeploymentBar(layout.DeploymentBarMin(0, layout.RowHeight) + new float2(1, 1), 0, layout.RowHeight),
                "the lower deployment hit strip scrolls with rows and rejects rows clipped above the list viewport");
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
        LayoutOffsets();
    }

    private static void LayoutOffsets()
    {
        foreach (var size in new float2[] { new(1920, 1080), new(240, 220), new(420, 820), new(1920, 220) })
        {
            var origin = new float2(-400, -200);
            const float inset = 30;
            var layout = SaberOverlayLayout.Create(origin, size, 16, 40, inset);
            Check(layout.WithControlsOffset(origin, size, float2.Zero, inset) == layout,
                "zero saved offsets preserve the default action layout");
            foreach (var offset in new float2[] { new(-1, -1), new(1, 1), new(-1, 1), new(1, -1), new(-100, 100) })
            {
                var moved = layout.WithControlsOffset(origin, size, offset, inset);
                var delta = moved.SwingCenter - layout.SwingCenter;
                Check(moved.Valid && moved.ControlsMin.X >= origin.X - 1e-3f
                    && moved.ControlsMin.Y >= origin.Y + inset - 1e-3f
                    && moved.ControlsMax.X <= origin.X + size.X + 1e-3f
                    && moved.ControlsMax.Y <= origin.Y + size.Y + 1e-3f,
                    "extreme action offsets clamp the whole group below the menu and inside compact viewports");
                Check((moved.SliceCenter - layout.SliceCenter - delta).Length() < 1e-3f
                    && (moved.IgniteCenter - layout.IgniteCenter - delta).Length() < 1e-3f
                    && (moved.ForwardCenter - layout.ForwardCenter - delta).Length() < 1e-3f
                    && (moved.BackwardCenter - layout.BackwardCenter - delta).Length() < 1e-3f
                    && (moved.LeftCenter - layout.LeftCenter - delta).Length() < 1e-3f
                    && (moved.RightCenter - layout.RightCenter - delta).Length() < 1e-3f
                    && (moved.StatusMin - layout.StatusMin - delta).Length() < 1e-3f
                    && (moved.StatusMax - layout.StatusMax - delta).Length() < 1e-3f
                    && moved.SwingRadius == layout.SwingRadius && moved.Scale == layout.Scale,
                    "saber, movement buttons and status translate rigidly without changing scale or spacing");
                Check(moved.ListMin == layout.ListMin && moved.ListMax == layout.ListMax
                    && moved.ListBodyTop == layout.ListBodyTop
                    && moved.ColorSwatchMin(0, 0) == layout.ColorSwatchMin(0, 0),
                    "moving action controls leaves the kitten list and swatches anchored");
                Check(moved.Hit(moved.SwingCenter) == SaberOverlayHit.Swing
                    && moved.Hit(moved.SliceCenter) == SaberOverlayHit.Slice
                    && moved.Hit(moved.IgniteCenter) == SaberOverlayHit.Ignite
                    && moved.Hit(moved.ForwardCenter) == SaberOverlayHit.SomersaultForward
                    && moved.Hit(moved.BackwardCenter) == SaberOverlayHit.SomersaultBackward
                    && moved.Hit(moved.LeftCenter) == SaberOverlayHit.SomersaultLeft
                    && moved.Hit(moved.RightCenter) == SaberOverlayHit.SomersaultRight,
                    "translated actions retain their drawn hit targets, including over the fixed kitten list");
            }
        }
        var normal = SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2);
        var custom = normal.WithControlsOffset(float2.Zero, new(1920, 1080), new(-.05f, .04f));
        Check((custom.SwingCenter - normal.SwingCenter - new float2(-96, 43.2f)).Length() < 1e-3f,
            "unclamped custom X and Y positions use viewport fractions");
        Check(normal.WithControlsOffset(float2.Zero, new(1920, 1080), new(float.NaN, float.PositiveInfinity)) == normal
            && !normal.WithControlsOffset(float2.Zero, new(100, 100), float2.Zero).Valid
            && !normal.WithControlsOffset(float2.Zero, new(1920, 1080), float2.Zero, float.NaN).Valid,
            "nonfinite offsets reset per axis and unusable work areas leave no invisible action targets");
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
            Check(capture.Consume(0, true, true, false, 4, out bool second) && second
                && capture.Consume(0, false, true, false, 4, out _)
                && capture.Consume(0, true, true, false, 4, out bool third) && third,
                "release then press authorizes each deliberate combo jump even within one frame");
            capture.Consume(0, false, false, false, 4, out _);
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

    private static void ManualSwooshGate()
    {
        var gate = new ManualSwingSwooshGate();
        gate.Begin(float2.Zero, 4);
        Check(!gate.TryConsume(0, true), "a manual SWING press without pointer movement produces no swoosh");
        gate.Move(new(2, 0), 1, true);
        Check(!gate.TryConsume(1, true), "sub-threshold pointer movement remains silent");
        gate.Move(new(4, 0), 2, true);
        Check(gate.TryConsume(2, true), "the first four logical pixels of held mouse movement produce one swoosh");
        Check(!gate.TryConsume(3, true), "a stationary held pointer does not repeat the swoosh");

        gate.Reset(); gate.Begin(float2.Zero, 4);
        gate.Move(new(4, 0), 100, true);
        gate.Move(new(4, 0), 125, true); // UI and native callbacks can report the same sample.
        Check(!gate.TryConsume(251, true), "duplicate native/UI coordinates do not refresh a stale pending sample");
        gate.Move(new(8, 0), 252, true);
        Check(gate.TryConsume(252, true), "fresh motion after a stale sample can still produce a swoosh");

        gate.Reset(); gate.Begin(float2.Zero, 4);
        gate.Move(new(4, 0), 0, true);
        Check(gate.TryConsume(0, true), "first cue establishes a monotonic movement cadence");
        gate.Move(new(8, 0), 1249, true);
        Check(!gate.TryConsume(1249, true), "repeat cue is blocked through 1249 milliseconds");
        Check(!gate.TryConsume(1250, true), "a discarded cooldown sample cannot replay at the deadline without new motion");
        gate.Move(new(9, 0), 1250, true);
        Check(gate.TryConsume(1250, true), "fresh held movement can repeat exactly at 1250 milliseconds");

        gate.Release(); gate.Begin(new(20, 0), 4);
        gate.Move(new(24, 0), 1500, true);
        Check(!gate.TryConsume(1500, true), "release and re-press preserve the global swoosh cooldown");
        gate.Move(new(25, 0), 2499, true);
        Check(!gate.TryConsume(2499, true), "re-grab remains silent one millisecond before the retained deadline");
        gate.Move(new(26, 0), 2500, true);
        Check(gate.TryConsume(2500, true), "a re-grab accepts fresh movement at the retained deadline");

        gate.Reset(); gate.Begin(float2.Zero, 4);
        gate.Move(new(4, 0), 10, true);
        Check(!gate.TryConsume(11, false) && !gate.TryConsume(12, true),
            "a blocked final-frame sample is discarded instead of replaying after the clash or pause ends");
        gate.Move(new(6, 0), 13, true);
        Check(!gate.TryConsume(13, true), "resuming after blocked input requires a fresh four-pixel movement");
        gate.Move(new(8, 0), 14, true);
        Check(gate.TryConsume(14, true), "fresh movement after a clash or pause can trigger normally");

        gate.Reset(); gate.Begin(float2.Zero, 4);
        gate.Move(new(4, 0), 100, true);
        Check(!gate.TryConsume(251, true) && !gate.TryConsume(99, true),
            "pending motion older than 150 milliseconds and backwards time are rejected");
        gate.Move(new(8, 0), 252, true);
        Check(gate.TryConsume(252, true), "a stale-sample rebase admits later distinct motion");
        gate.Release(); gate.Reset();
        Check(!gate.Held && !gate.Pending, "full gate reset clears capture, pending input, and cadence state");
        gate.Begin(float2.Zero, 4); gate.Move(new(4, 0), 1, true);
        Check(gate.TryConsume(1, true), "full reset also clears the retained cooldown deadline");
        gate.Reset();

        gate.Begin(new(float.NaN, 0), 4);
        Check(!gate.Held, "a nonfinite press pointer cannot begin a swoosh gesture");
        gate.Begin(float2.Zero, float.NaN);
        Check(!gate.Held, "a nonfinite movement threshold cannot begin a swoosh gesture");
        gate.Begin(float2.Zero, 4); gate.Move(new(float.NaN, 0), 1, true);
        Check(!gate.Pending && !gate.TryConsume(1, true), "nonfinite cursor samples are ignored safely");

        gate.Begin(float2.Zero, 4); gate.Move(new(4, 0), 1, true); gate.TryConsume(1, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        {
            gate.Move(new(4 + (i & 1), 0), 2 + i, true);
            gate.TryConsume(2 + i, true);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "warmed manual swoosh movement and cadence gating allocate no managed memory");
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
