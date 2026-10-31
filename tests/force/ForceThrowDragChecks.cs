using Brutal.Numerics;
using Purr2D2.Core;

internal static class ForceThrowDragChecks
{
    public static void Run()
    {
        StartsAtMinimumAndRequiresOneFinish();
        MapsUpwardMovementAndClamps();
        CancelsAndStartsFresh();
        RejectsInvalidInputAndHeldOverwrite();
        MatchesOnlyTheCurrentLayout();
    }

    private static void StartsAtMinimumAndRequiresOneFinish()
    {
        var drag = new ForceThrowDrag();
        var center = new float2(120, 240);
        Check(drag.Begin(new float2(900, -50), center, 12, 100), "any finite press position can start the throw drag");
        Check(drag.Held && drag.Speed == ForceThrowDrag.MinimumSpeed, "a new throw drag begins at minimum speed");
        Check(drag.Center == center && drag.Radius == 12 && drag.Travel == 100, "throw geometry is retained");
        Check(drag.TrackTop == 140 && drag.TrackBottom == 240, "throw track opens upward from the button");
        Check(drag.PillMin == new float2(108, 128) && drag.PillMax == new float2(132, 252),
            "throw pill expands upward and around the button");
        Check(drag.Thumb == center, "minimum thumb stays at the throw button");
        Check(drag.Finish(out var speed) && speed == ForceThrowDrag.MinimumSpeed, "release snapshots the chosen minimum speed");
        Check(!drag.Held && drag.Speed == ForceThrowDrag.MinimumSpeed, "finish resets the transient drag");
        Check(!drag.Finish(out var second) && second == ForceThrowDrag.MinimumSpeed, "a completed drag cannot throw twice");
    }

    private static void MapsUpwardMovementAndClamps()
    {
        var drag = NewDrag();
        Check(drag.Move(new float2(120, 215)) && drag.Speed == 63.25, "one quarter travel selects one quarter speed range");
        Check(drag.Thumb == new float2(120, 215), "thumb follows the selected speed");
        Check(drag.Move(new float2(-500, 140)) && drag.Speed == ForceThrowDrag.MaximumSpeed,
            "full upward travel selects maximum speed and ignores horizontal movement");
        Check(drag.Thumb == new float2(120, 140), "maximum thumb is at the top of the pill track");
        Check(drag.Move(new float2(120, 900)) && drag.Speed == ForceThrowDrag.MinimumSpeed,
            "downward movement clamps to minimum speed");
        Check(drag.Move(new float2(float.MaxValue, -float.MaxValue)) && drag.Speed == ForceThrowDrag.MaximumSpeed,
            "finite extreme pointer values clamp without overflowing");
    }

    private static void CancelsAndStartsFresh()
    {
        var drag = NewDrag();
        drag.Move(new float2(20, 140));
        drag.Cancel();
        Check(!drag.Held && drag.Speed == ForceThrowDrag.MinimumSpeed && drag.Center == float2.Zero,
            "cancel resets held state, speed, and geometry");
        Check(drag.Begin(new float2(40, 60), new float2(20, 60), 8, 40), "a canceled drag can be restarted");
        Check(drag.Speed == ForceThrowDrag.MinimumSpeed, "a new press after cancellation starts at minimum speed");
        Check(drag.Finish(out var speed) && speed == ForceThrowDrag.MinimumSpeed, "a click-release at minimum is valid");
    }

    private static void RejectsInvalidInputAndHeldOverwrite()
    {
        var drag = new ForceThrowDrag();
        Check(!drag.Begin(new float2(float.NaN, 0), new float2(1, 2), 8, 40), "non-finite pointer is rejected");
        Check(!drag.Begin(new float2(0), new float2(float.PositiveInfinity, 2), 8, 40), "non-finite center is rejected");
        Check(!drag.Begin(new float2(0), new float2(1, 2), 0, 40), "zero radius is rejected");
        Check(!drag.Begin(new float2(0), new float2(1, 2), 8, float.NaN), "non-finite travel is rejected");
        Check(!drag.Begin(new float2(float.MaxValue), new float2(float.MaxValue), float.MaxValue, float.MaxValue),
            "geometry whose derived pill overflows is rejected");
        Check(!drag.Begin(new float2(1e30f), new float2(1e30f), 1, 1),
            "geometry too large to represent a positive pill and track is rejected");
        Check(drag.Begin(new float2(0), new float2(1, 2), 8, 40), "valid geometry starts after invalid attempts");
        drag.Move(new float2(1, -40));
        Check(!drag.Begin(new float2(0), new float2(9, 9), 10, 100), "a held drag cannot be overwritten by another press");
        Check(drag.Center == new float2(1, 2) && drag.Speed == ForceThrowDrag.MaximumSpeed,
            "a rejected overlapping press preserves the active drag");
        Check(!drag.Move(new float2(0, float.NaN)) && drag.Speed == ForceThrowDrag.MaximumSpeed,
            "non-finite movement is ignored without corrupting speed");
    }

    private static void MatchesOnlyTheCurrentLayout()
    {
        var layout = Layout();
        var drag = new ForceThrowDrag();
        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel),
            "layout geometry starts a throw drag");
        Check(drag.Matches(layout), "the pressed layout matches its geometry");
        Check(!drag.Matches(layout with { AngryCenter = layout.AngryCenter + new float2(1, 0) }),
            "moving the throw button invalidates the drag");
        Check(!drag.Matches(layout with { ButtonRadius = layout.ButtonRadius + 1 }),
            "changing the button radius invalidates the drag");
        Check(!drag.Matches(layout with { Scale = layout.Scale + .1f }),
            "changing the layout scale invalidates the travel");
        Check(!drag.Matches(layout with { Scale = float.NaN }), "non-finite layout travel cannot match");
        drag.Cancel();
        Check(!drag.Matches(layout), "a canceled drag never matches a layout");
    }

    private static ForceThrowDrag NewDrag()
    {
        var drag = new ForceThrowDrag();
        Check(drag.Begin(new float2(70, 240), new float2(120, 240), 12, 100), "test throw drag begins");
        return drag;
    }

    private static ForceOverlayLayout Layout() => new(
        new float2(10, 10), new float2(20, 20), new float2(120, 240), new float2(40, 40),
        12, new float2(100, 100), 60, new float2(0, 0), new float2(50, 50), 1, 16);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
