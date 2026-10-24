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

        foreach (var size in new float2[] { new(1920, 1080), new(800, 600), new(420, 820), new(240, 220), new(3840, 2160), new(600, 1080) })
        {
            float2 origin = new(-35, 17);
            var saber = SaberOverlayLayout.Create(origin, size, 16, 40, 30);
            var force = ForceOverlayLayout.Create(saber, origin, size);
            Check(force.Valid, "Force controls fit the supported viewport sizes");
            Check(force.MoveCenter.X - force.MoveRadius >= origin.X
                && force.MoveCenter.X + force.MoveRadius < saber.SwingCenter.X - saber.SwingRadius
                && force.MoveCenter.Y - force.MoveRadius >= origin.Y + 30
                && force.MoveCenter.Y + force.MoveRadius <= origin.Y + size.Y,
                "left Force and right saber circles remain separate and within the work area");
            Check(force.Hit(force.ForceCenter) == ForceOverlayHit.Force
                && force.Hit(force.AngryCenter) == ForceOverlayHit.AngryForce
                && force.Hit(force.MoveCenter) == ForceOverlayHit.Move
                && force.Hit(force.MoveCenter - new float2(force.MoveRadius * .9f)) == ForceOverlayHit.None,
                "drawing and Force hit-testing share exact circles with click-through corners");
            Check(force.ForceCenter.X > force.AngryCenter.X && force.ForceCenter.Y < force.AngryCenter.Y
                && force.ForceCenter.Y > force.MoveCenter.Y
                && force.AngryCenter.Y > force.MoveCenter.Y + force.MoveRadius
                && (force.ForceCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && (force.AngryCenter - force.MoveCenter).Length() > force.ButtonRadius + force.MoveRadius
                && (force.AngryCenter - force.ForceCenter).Length() > force.ButtonRadius * 2
                && force.StatusMin.Y > force.AngryCenter.Y + force.ButtonRadius
                && force.StatusMax.Y <= origin.Y + size.Y,
                "Force and Angry Force sit below-right of Move with separate hit regions and status space");
            Check(force.WithControlsOffset(origin, size, float2.Zero, 30) == force,
                "zero Force offsets preserve the existing left-side anchors");
            foreach (var offset in new float2[] { new(-1, -1), new(1, 1), new(-1, 1), new(1, -1), new(100, -100) })
            {
                var moved = force.WithControlsOffset(origin, size, offset, 30);
                var delta = moved.MoveCenter - force.MoveCenter;
                Check(moved.Valid && moved.ControlsMin.X >= origin.X - 1e-3f
                    && moved.ControlsMin.Y >= origin.Y + 30 - 1e-3f
                    && moved.ControlsMax.X <= origin.X + size.X + 1e-3f
                    && moved.ControlsMax.Y <= origin.Y + size.Y + 1e-3f,
                    "custom Force offsets keep the entire group below the menu and inside each supported work area");
                Check((moved.ForceCenter - force.ForceCenter - delta).Length() < 1e-3f
                    && (moved.AngryCenter - force.AngryCenter - delta).Length() < 1e-3f
                    && (moved.StatusMin - force.StatusMin - delta).Length() < 1e-3f
                    && (moved.StatusMax - force.StatusMax - delta).Length() < 1e-3f
                    && moved.MoveRadius == force.MoveRadius && moved.ButtonRadius == force.ButtonRadius
                    && moved.Hit(moved.MoveCenter) == ForceOverlayHit.Move
                    && moved.Hit(moved.ForceCenter) == ForceOverlayHit.Force
                    && moved.Hit(moved.AngryCenter) == ForceOverlayHit.AngryForce,
                    "Force, Angry Force, Move and their status translate rigidly with matching hit targets");
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
        Console.WriteLine("Force overlay checks passed (persistent offsets, camera basis, bounds, compact layout, pointer ownership).");
    }

    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static void Near(double3 value, double3 expected, string message) => Check((value - expected).Length() < 1e-8, message);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
