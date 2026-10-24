using Brutal.Numerics;
using Purr2D2.Core;

internal static class ForceOverlayChecks
{
    public static void Run()
    {
        var drag = new ForceOverlayDrag();
        var original = new double3(2, -1, 3);
        Check(drag.Begin(new(100, 100), 80, original, double3.UnitX, double3.UnitY, -double3.UnitZ)
            && drag.Offset == original, "a Force press preserves the existing group position exactly");
        drag.Move(new(180, 100));
        Near(drag.Offset, original + new double3(8, 0, 0), "screen-right movement follows the snapshotted camera basis");
        drag.Scroll(2);
        Near(drag.Offset, original + new double3(8, 0, 1), "wheel up moves the group towards the camera");
        var placed = drag.Offset;
        drag.Release();
        drag.Move(new(-100, -100)); drag.Scroll(50);
        Near(drag.Offset, placed, "release retains the final group offset without a spring return");
        Check(drag.Begin(new(350, 270), 80, placed, double3.UnitY, -double3.UnitX, -double3.UnitZ),
            "a later drag can snapshot a different ground/orbital camera basis");
        Near(drag.Offset, placed, "re-grabbing rebases at the current position without a jump");
        drag.Move(new(350, 190));
        Near(drag.Offset, placed - new double3(8, 0, 0), "screen up maps through the current body-relative camera up axis");
        foreach (var pointer in new[] { new float2(100000, -100000), new float2(float.MaxValue, float.MaxValue) })
        {
            drag.Move(pointer); drag.Scroll(double.MaxValue);
            Check(Finite(drag.Offset) && drag.Offset.Length() <= 25.00000001, "extreme gestures remain finite and within the group movement budget");
        }
        var before = drag.Offset;
        drag.Move(new(float.NaN, 0)); drag.Scroll(double.PositiveInfinity);
        Check(drag.Offset == before && !drag.Begin(float2.Zero, 80, before, double3.UnitX, double3.UnitX, double3.UnitZ),
            "nonfinite input and a degenerate camera basis cannot corrupt Force offsets");
        drag.Reset(); Check(!drag.Held && drag.Offset == double3.Zero, "world reset clears gesture state");
        Check(drag.Scroll(2, original, -double3.UnitZ) && !drag.Held,
            "hovering wheel depth does not start a drag");
        Near(drag.Offset, original + double3.UnitZ, "idle wheel uses the supplied current offset and camera forward");
        Check(drag.Scroll(-2, drag.Offset, double3.UnitX), "idle wheel can use a newly rotated camera");
        Near(drag.Offset, original + double3.UnitZ + double3.UnitX, "idle depth follows the current camera instead of a stale drag basis");
        before = drag.Offset;
        Check(!drag.Scroll(double.NaN, before, double3.UnitZ) && !drag.Scroll(1, before, double3.Zero)
            && drag.Offset == before, "invalid idle depth input cannot corrupt a retained offset");
        Check(drag.Scroll(double.MaxValue, before, double3.UnitZ) && Finite(drag.Offset)
            && drag.Offset.Length() <= ForceOverlayDrag.MaximumOffsetMeters + 1e-8,
            "extreme idle wheel input remains finite and within the group movement budget");

        var sixtyHz = new ForceOffsetSmoother();
        var oneFortyFourHz = new ForceOffsetSmoother();
        sixtyHz.SetTarget(new(8, 0, 0)); oneFortyFourHz.SetTarget(new(8, 0, 0));
        for (int i = 0; i < 15; i++) sixtyHz.Advance(1.0 / 60);
        for (int i = 0; i < 36; i++) oneFortyFourHz.Advance(1.0 / 144);
        Check(sixtyHz.Offset.X > 0 && sixtyHz.Offset.X < 8
            && (sixtyHz.Offset - oneFortyFourHz.Offset).Length() < 1e-8,
            "the critically damped target follows smoothly with equivalent motion at 60 and 144 Hz");
        var releasedTarget = sixtyHz.Target;
        double previousDistance = (releasedTarget - sixtyHz.Offset).Length();
        double previousSpeed = sixtyHz.Velocity.Length();
        for (int i = 0; i < 120; i++)
        {
            sixtyHz.Advance(1.0 / 60);
            double distance = (releasedTarget - sixtyHz.Offset).Length();
            Check(distance <= previousDistance + 1e-10 && distance >= 0
                && sixtyHz.Offset.Length() <= ForceOverlayDrag.MaximumOffsetMeters + 1e-8,
                "a released Force target eases toward its endpoint without overshoot or leaving the movement limit");
            previousDistance = distance;
        }
        Check((sixtyHz.Offset - releasedTarget).Length() < 1e-5 && sixtyHz.Velocity.Length() < previousSpeed,
            "the released drag settles with its residual velocity damped away");
        var jitter = new ForceOffsetSmoother();
        for (int i = 0; i < 120; i++)
        {
            jitter.SetTarget(new(i % 2 == 0 ? 1 : -1, 0, 0));
            jitter.Advance(1.0 / 60);
            Check(Math.Abs(jitter.Offset.X) < 1,
                "small alternating pointer jitter is filtered instead of reproduced as raw movement");
        }
        jitter.Reset(new(2, 3, 4));
        Check(jitter.Offset == new double3(2, 3, 4) && jitter.Target == jitter.Offset
            && jitter.Velocity == double3.Zero,
            "reset clears spring velocity and prevents motion from leaking into a later capture");
        jitter.SetTarget(new(double.NaN, 0, 0));
        Check(jitter.Target == new double3(2, 3, 4), "invalid spring targets cannot corrupt the retained position");

        var slider = new ForceOverlaySliderDrag();
        Check(slider.Begin(ForceOverlayHit.AngryRadius, new(80, 100), new(80, 100), 27, 96,
            10, 1, 50, false), "distance slider starts from a finite shared value");
        Check(slider.PillMin == new float2(53, 73) && slider.PillMax == new float2(107, 223)
            && slider.TrackTop == 100 && slider.TrackBottom == 196,
            "the same-width rounded pill opens vertically downward from the collapsed circle");
        Check(slider.Thumb(1).Y == slider.TrackTop && slider.Thumb(50).Y == slider.TrackBottom,
            "minimum and maximum values draw at the top and bottom of the track");
        Check(slider.Move(new(80, 100), out double value) && Math.Abs(value - 10) < 1e-9,
            "pressing without movement preserves the current value");
        Check(slider.Move(new(5000, -5000), out value) && value == 1
            && slider.Move(new(-5000, 5000), out value) && value == 50,
            "vertical motion clamps at both range ends and ignores horizontal distance");
        slider.Release();
        Check(!slider.Held && !slider.Move(float2.Zero, out _), "release collapses the settings pill");
        Check(slider.Begin(ForceOverlayHit.CaptureLimit, new(20, 30), new(20, 30), 10, 100,
            1, 1, 5, true) && slider.Move(new(20, 92.5f), out value) && value == 4,
            "capture counts round midpoint values to an integer");
        Check(slider.Move(new(20, float.PositiveInfinity), out _) == false,
            "nonfinite slider motion cannot change a setting");
        slider.Reset();

        foreach (var size in new float2[] { new(1920, 1080), new(800, 600), new(420, 820), new(240, 220), new(3840, 2160), new(600, 1080) })
        {
            float2 origin = new(-35, 17);
            var saber = SaberOverlayLayout.Create(origin, size, 16, 40, 30);
            var force = ForceOverlayLayout.Create(saber, origin, size);
            Check(force.Valid, "Force controls fit the supported viewport sizes");
            CheckSliderLayouts(force, "base supported viewport");
            Check(force.MoveCenter.X - force.MoveRadius >= origin.X
                && force.ControlsMax.X < saber.SwingCenter.X - saber.SwingRadius
                && force.MoveCenter.Y - force.MoveRadius >= origin.Y + 30
                && force.ControlsMax.Y <= origin.Y + size.Y,
                "the full left Force group and right saber controls remain separate and within the work area");
            Check(force.Hit(force.ForceCenter) == ForceOverlayHit.Force
                && force.Hit(force.AngryCenter) == ForceOverlayHit.AngryForce
                && force.Hit(force.AngryRadiusCenter) == ForceOverlayHit.AngryRadius
                && force.Hit(force.CaptureLimitCenter) == ForceOverlayHit.CaptureLimit
                && force.Hit(force.MoveCenter) == ForceOverlayHit.Move
                && force.Hit(force.MoveCenter - new float2(force.MoveRadius * .9f)) == ForceOverlayHit.None,
                "drawing and all five Force hit targets share exact circles with click-through corners");
            Check(force.CaptureLimitCenter.X < force.AngryRadiusCenter.X
                && force.AngryRadiusCenter.X < force.AngryCenter.X && force.AngryCenter.X < force.ForceCenter.X
                && force.CaptureLimitCenter.Y == force.ForceCenter.Y
                && force.AngryRadiusCenter.Y == force.AngryCenter.Y
                && force.AngryCenter.Y > force.MoveCenter.Y + force.MoveRadius
                && (force.CaptureLimitCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && (force.AngryRadiusCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && (force.ForceCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && (force.AngryCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && force.SliderEnvelope(ForceOverlayHit.CaptureLimit).Max.Y < force.StatusMin.Y
                && force.SliderEnvelope(ForceOverlayHit.AngryRadius).Max.Y < force.StatusMin.Y
                && force.StatusMax.Y <= origin.Y + size.Y,
                "count, distance, Angry Force and Force form a separated left-to-right arc with pill and status space");
            float expectedGap = saber.SwingSliceEdgeGap;
            float captureGap = (force.CaptureLimitCenter - force.MoveCenter).Length()
                - force.ButtonRadius - force.MoveRadius;
            float angryRadiusGap = (force.AngryRadiusCenter - force.MoveCenter).Length()
                - force.ButtonRadius - force.MoveRadius;
            float angryGap = (force.AngryCenter - force.MoveCenter).Length()
                - force.ButtonRadius - force.MoveRadius;
            float forceGap = (force.ForceCenter - force.MoveCenter).Length()
                - force.ButtonRadius - force.MoveRadius;
            float leftSpacing = (force.AngryRadiusCenter - force.CaptureLimitCenter).Length()
                - force.ButtonRadius * 2;
            float middleSpacing = (force.AngryCenter - force.AngryRadiusCenter).Length()
                - force.ButtonRadius * 2;
            float rightSpacing = (force.ForceCenter - force.AngryCenter).Length()
                - force.ButtonRadius * 2;
            Check(MathF.Abs(captureGap - expectedGap) < 1e-3f
                && MathF.Abs(angryRadiusGap - expectedGap) < 1e-3f
                && MathF.Abs(angryGap - expectedGap) < 1e-3f
                && MathF.Abs(forceGap - expectedGap) < 1e-3f
                && MathF.Abs(leftSpacing - expectedGap) < 1e-3f
                && MathF.Abs(middleSpacing - expectedGap) < 1e-3f
                && MathF.Abs(rightSpacing - expectedGap) < 1e-3f
                && force.ControlsMax.X >= force.SliderValueLabelMaxX,
                "the four Force controls use the Swing/Slice edge gap around Move and between neighbors, with room reserved for slider values");
            Check(force.WithControlsOffset(origin, size, float2.Zero, 30) == force,
                "zero Force offsets preserve the existing left-side anchors");
            foreach (var offset in new float2[] { new(-1, -1), new(1, 1), new(-1, 1), new(1, -1), new(100, -100) })
            {
                var moved = force.WithControlsOffset(origin, size, offset, 30);
                var delta = moved.MoveCenter - force.MoveCenter;
                CheckSliderLayouts(moved, "offset supported viewport");
                Check(moved.Valid && moved.ControlsMin.X >= origin.X - 1e-3f
                    && moved.ControlsMin.Y >= origin.Y + 30 - 1e-3f
                    && moved.ControlsMax.X <= origin.X + size.X + 1e-3f
                    && moved.ControlsMax.Y <= origin.Y + size.Y + 1e-3f,
                    "custom Force offsets keep the entire group below the menu and inside each supported work area");
                Check((moved.ForceCenter - force.ForceCenter - delta).Length() < 1e-3f
                    && (moved.AngryCenter - force.AngryCenter - delta).Length() < 1e-3f
                    && (moved.AngryRadiusCenter - force.AngryRadiusCenter - delta).Length() < 1e-3f
                    && (moved.CaptureLimitCenter - force.CaptureLimitCenter - delta).Length() < 1e-3f
                    && (moved.StatusMin - force.StatusMin - delta).Length() < 1e-3f
                    && (moved.StatusMax - force.StatusMax - delta).Length() < 1e-3f
                    && moved.MoveRadius == force.MoveRadius && moved.ButtonRadius == force.ButtonRadius
                    && moved.Hit(moved.MoveCenter) == ForceOverlayHit.Move
                    && moved.Hit(moved.ForceCenter) == ForceOverlayHit.Force
                    && moved.Hit(moved.AngryCenter) == ForceOverlayHit.AngryForce
                    && moved.Hit(moved.AngryRadiusCenter) == ForceOverlayHit.AngryRadius
                    && moved.Hit(moved.CaptureLimitCenter) == ForceOverlayHit.CaptureLimit,
                    "all four arc controls, Move and their status translate rigidly with matching hit targets");
            }
        }
        var baseSaber = SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 2);
        var baseForce = ForceOverlayLayout.Create(baseSaber, float2.Zero, new(1920, 1080));
        var customForce = baseForce.WithControlsOffset(float2.Zero, new(1920, 1080), new(.04f, -.04f));
        Check((customForce.MoveCenter - baseForce.MoveCenter - new float2(76.8f, -43.2f)).Length() < 1e-3f
            && baseForce.WithControlsOffset(float2.Zero, new(1920, 1080), new(float.NegativeInfinity, float.NaN)) == baseForce,
            "Force custom positions use independent viewport fractions and ignore nonfinite offset values");
        Check(!baseForce.WithControlsOffset(float2.Zero, new(100, 100), float2.Zero).Valid,
            "an undersized work area cannot leave partly invisible Force targets");
        Check(!ForceOverlayLayout.Create(default, float2.Zero, new(100, 100)).Valid,
            "invalid overlay geometry cannot retain invisible Force controls");
        var capture = new SaberOverlayPointerCapture();
        Check(capture.Consume(0, true, true, false, 1, out bool first) && first
            && capture.SuppressImGuiButton(0, true, 1), "a Force press belongs to one raw/backend gesture");
        Check(capture.Consume(0, true, true, false, 1, out first) && !first,
            "duplicate presses cannot trigger a second Force action");
        Check(capture.SuppressImGuiButton(0, false, 2) && !capture.IsHeld(0)
            && !capture.Consume(0, false, false, false, 2), "a backend-first release completes its captured gesture once");
        Check(capture.Consume(0, true, true, false, 3) && capture.Consume(0, false, false, false, 3)
            && capture.SuppressImGuiButton(0, true, 3) && capture.SuppressImGuiButton(0, false, 3),
            "quick Force press/release pairs preserve suppression in the other callback order");
        Check(slider.Begin(ForceOverlayHit.AngryRadius, baseForce.AngryRadiusCenter,
                baseForce.AngryRadiusCenter, baseForce.ButtonRadius, baseForce.SliderTravel, 10, 1, 50, false)
            && capture.Consume(0, true, true, false, 4)
            && capture.AnyHeld && capture.OwnsScroll(false)
            && slider.Move(new(-100000, 100000), out value) && value == 50,
            "a held settings control retains pointer and scroll ownership while dragging outside every overlay bound");
        Check(capture.Consume(0, false, false, false, 5) && !capture.AnyHeld,
            "the captured release completes an outside settings drag");
        slider.Release();
        Console.WriteLine("Force overlay checks passed (persistent offsets, slider ranges, arc bounds, held capture, pointer ownership).");
    }

    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static void CheckSliderLayouts(ForceOverlayLayout layout, string context)
    {
        foreach (var (control, center, current, maximum, integer) in new[]
        {
            (ForceOverlayHit.CaptureLimit, layout.CaptureLimitCenter, 128d, 512d, true),
            (ForceOverlayHit.AngryRadius, layout.AngryRadiusCenter, 10d, 50d, false)
        })
        {
            var slider = new ForceOverlaySliderDrag();
            Check(slider.Begin(control, center, center, layout.ButtonRadius, layout.SliderTravel,
                    current, 1, maximum, integer) && slider.Matches(layout),
                $"{control} press geometry exactly matches its rebuilt {context} layout");
        }
    }
    private static void Near(double3 value, double3 expected, string message) => Check((value - expected).Length() < 1e-8, message);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
