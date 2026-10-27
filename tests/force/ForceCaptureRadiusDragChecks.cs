using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Force;

internal static class ForceCaptureRadiusDragChecks
{
    private static int _checks;

    internal static void Run()
    {
        Check(ForceMath.DefaultCaptureRadius == 2, "a new Angry selection starts with a two metre radius");
        ClickAndThreshold();
        ReversalAndReset();
        InvalidInputAndCancellation();
        LayoutMatches();
        Console.WriteLine($"Force capture radius drag checks passed ({_checks} assertions).");
    }

    private static void ClickAndThreshold()
    {
        var drag = new ForceCaptureRadiusDrag();
        var layout = Layout();
        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel), "a normal Angry press starts a transient radius gesture");
        Check(!drag.Dragged && drag.Radius == 2 && drag.Thumb.Y == drag.TrackBottom,
            "press alone keeps the two metre thumb at the bottom of the upward pill");
        Check(drag.Move(layout.AngryCenter + new float2(3, 0)) && !drag.Dragged,
            "movement below four logical pixels remains a click");
        Check(drag.Finish(out bool dragged, out double radius) && !dragged && radius == 2,
            "click release completes with the minimum radius");
        Check(!drag.Finish(out _, out _), "one pointer release cannot finish the gesture twice");

        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel), "a fresh gesture can begin after a click");
        Check(drag.Move(layout.AngryCenter + new float2(0, -4)) && drag.Dragged,
            "the four pixel threshold starts the drag at its exact boundary");
        Check(drag.Radius > 2, "upward movement past the threshold increases the chosen radius");
    }

    private static void ReversalAndReset()
    {
        var layout = Layout();
        var drag = new ForceCaptureRadiusDrag();
        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel),
            "radius selection begins from two metres");
        drag.Move(layout.AngryCenter - new float2(0, layout.SliderTravel));
        Check(drag.Radius == ForceCaptureRadiusDrag.MaximumRadius,
            "full upward travel reaches the fifty metre cap");
        drag.Move(layout.AngryCenter - new float2(0, layout.SliderTravel * .25f));
        Check(Math.Abs(drag.Radius - 14) < 1e-9,
            "downward motion reverses from fifty metres to fourteen metres");
        Check(drag.Finish(out bool dragged, out double radius) && dragged && radius == 14,
            "release returns the final reversed selection");
        Check(!drag.Finish(out _, out _), "selection completion is idempotent");
        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel)
            && drag.Radius == 2 && drag.Thumb.Y == drag.TrackBottom,
            "a new selection resets to two metres regardless of its previous value");
        drag.Cancel();
    }

    private static void InvalidInputAndCancellation()
    {
        var drag = new ForceCaptureRadiusDrag();
        var layout = Layout();
        Check(!drag.BeginSelection(new(float.NaN, 0), layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel),
            "nonfinite press coordinates are rejected");
        Check(!drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, 0, layout.SliderTravel),
            "selection rejects invalid pill geometry");
        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel),
            "valid geometry starts after rejected inputs");
        Check(!drag.Move(new(0, float.NaN)) && drag.Radius == 2,
            "nonfinite motion is ignored without altering a radius gesture");
        Check(drag.Move(new(float.MaxValue, -float.MaxValue)) && drag.Radius == 50,
            "extreme finite pointer values stay clamped and finite");
        drag.Cancel();
        Check(!drag.Held && !drag.Dragged && drag.Radius == 2,
            "cancel clears the transient selection");
    }

    private static void LayoutMatches()
    {
        var layout = Layout();
        var drag = new ForceCaptureRadiusDrag();
        Check(drag.BeginSelection(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel),
            "layout geometry starts a radius selection");
        Check(drag.Matches(layout), "the pressed radius geometry matches its overlay layout");
        Check(!drag.Matches(layout with { AngryCenter = layout.AngryCenter + new float2(1, 0) }),
            "moving the Angry button invalidates a held gesture");
        Check(!drag.Matches(layout with { ButtonRadius = layout.ButtonRadius + 1 }),
            "changing its radius invalidates the held gesture");
        Check(!drag.Matches(layout with { Scale = layout.Scale + .1f }),
            "a DPI/layout scale change invalidates the old travel geometry");
        drag.Cancel();
        Check(!drag.Matches(layout), "a cancelled radius selection cannot match a later layout");
    }

    private static ForceOverlayLayout Layout() => new(
        new float2(10, 10), new float2(20, 20), new float2(120, 240), new float2(40, 40),
        12, new float2(100, 100), 60, new float2(0, 0), new float2(50, 50), 1, 16);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
