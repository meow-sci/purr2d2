using Brutal.Numerics;
using Purr2D2.Core;

internal static class ForceRotationDragChecks
{
    public static void Run()
    {
        foreach (var size in new[] { new float2(640, 480), new float2(1920, 1080), new float2(3840, 2160) })
        {
            var layout = ForceOverlayLayout.Create(SaberOverlayLayout.Create(float2.Zero, size, 16, 2), float2.Zero, size);
            var drag = new ForceRotationDrag();
            var top = layout.RotationHandleCenter(0);
            Check(top.Y + layout.RotationHandleRadius < layout.MoveCenter.Y - layout.MoveRadius,
                "the neutral circle sits completely outside the top of the move circle");
            Check(drag.Begin(top, layout, 0), "rotation can begin on the visible handle");
            foreach (double turn in new[] { .25, .5, 1.0, .5, 0, -.25, -.5, -1, -.5, 0 })
            {
                var pointer = layout.RotationHandleCenter(turn * Math.PI * 2);
                Check(drag.Move(pointer) && Math.Abs(drag.Rotation - turn * Math.PI * 2) < 1e-5,
                    "arc length maps linearly and continuously to signed full turns");
            }
            drag.Move(layout.MoveCenter + new float2(layout.RotationTrackRadius, layout.RotationTrackRadius));
            Check(Math.Abs(drag.Rotation - Math.PI * 2) < 1e-5, "dragging below the right endpoint clamps at 360 degrees");
            drag.Move(layout.MoveCenter + new float2(-1, layout.RotationTrackRadius));
            Check(Math.Abs(drag.Rotation - Math.PI * 2) < 1e-5, "crossing the bottom angle seam cannot jump to the left endpoint");
            drag.Move(top);
            drag.Move(layout.MoveCenter + new float2(-layout.RotationTrackRadius, layout.RotationTrackRadius));
            Check(Math.Abs(drag.Rotation + Math.PI * 2) < 1e-5, "dragging below the left endpoint clamps at minus 360 degrees");
            double retained = drag.Rotation;
            drag.Move(new(float.NaN, 0)); drag.Move(layout.MoveCenter);
            Check(drag.Rotation == retained && !drag.Matches(layout with { MoveCenter = layout.MoveCenter + new float2(1, 0) }),
                "invalid or central pointers cannot corrupt the angle; moved layout invalidates geometry");
            drag.Release();
            Check(!drag.Move(top) && drag.Rotation == retained, "release retains the angle without accepting further movement");
            var thumb = layout.RotationHandleCenter(retained);
            Check(drag.Begin(thumb + new float2(0, layout.RotationHandleRadius * .5f), layout, retained),
                "off-center regrabs can start anywhere inside the tiny handle");
            drag.Move(thumb + new float2(0, layout.RotationHandleRadius * .5f));
            Check(Math.Abs(drag.Rotation - retained) < 1e-9, "an off-center regrab does not jump on the first move");
            drag.Reset();
            Check(!drag.Held && drag.Rotation == 0 && !drag.Begin(top, layout, double.NaN)
                && !drag.Begin(layout.MoveCenter, layout, 0), "reset clears state and invalid presses cannot begin a gesture");
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
