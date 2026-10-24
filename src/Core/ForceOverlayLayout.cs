using Brutal.Numerics;

namespace Purr2D2.Core;

public enum ForceOverlayHit { None, CaptureLimit, AngryRadius, AngryForce, Force, Move }

/// <summary>Left-side Force controls, using the same scale as the right-side saber controls.</summary>
public readonly record struct ForceOverlayLayout(float2 CaptureLimitCenter, float2 AngryRadiusCenter,
    float2 AngryCenter, float2 ForceCenter, float ButtonRadius, float2 MoveCenter, float MoveRadius,
    float2 StatusMin, float2 StatusMax, float Scale, float FontSize = 16, float ThrowLabelWidth = 64)
{
    public bool Valid => ButtonRadius > 0 && MoveRadius > 0;
    public float SliderTravel => 96 * Scale;
    public float SliderValueLabelGap => Math.Max(4, 6 * Scale);
    // Values are regular ImGui text, unlike the scaled circles. ThrowLabelWidth
    // comes from the actual no-wrap font measurement when the runtime creates layout.
    public float SliderValueLabelWidth => Math.Max(64 * Scale, ThrowLabelWidth);
    public float SliderValueLabelHalfHeight => Math.Max(FontSize * .5f, 16 * Scale);
    public float SliderValueLabelMaxX => Math.Max(Math.Max(CaptureLimitCenter.X, AngryRadiusCenter.X), AngryCenter.X)
        + ButtonRadius + SliderValueLabelGap + SliderValueLabelWidth;
    public float2 ControlsMin
    {
        get
        {
            float minimumSettingX = Math.Min(CaptureLimitCenter.X, AngryRadiusCenter.X);
            float minimumSettingY = Math.Min(CaptureLimitCenter.Y, AngryRadiusCenter.Y);
            float minimumButtonX = Math.Min(minimumSettingX, Math.Min(AngryCenter.X, ForceCenter.X)) - ButtonRadius;
            float minimumButtonY = Math.Min(minimumSettingY, Math.Min(AngryCenter.Y, ForceCenter.Y)) - ButtonRadius;
            return new(Math.Min(StatusMin.X, Math.Min(MoveCenter.X - MoveRadius, minimumButtonX)),
                Math.Min(StatusMin.Y, Math.Min(MoveCenter.Y - MoveRadius,
                    Math.Min(Math.Min(minimumSettingY - Math.Max(ButtonRadius, SliderValueLabelHalfHeight),
                        AngryCenter.Y - SliderTravel - Math.Max(ButtonRadius, SliderValueLabelHalfHeight)), minimumButtonY))));
        }
    }
    public float2 ControlsMax
    {
        get
        {
            float maximumSettingX = Math.Max(CaptureLimitCenter.X, AngryRadiusCenter.X);
            float maximumSettingY = Math.Max(CaptureLimitCenter.Y, AngryRadiusCenter.Y);
            float maximumButtonX = Math.Max(Math.Max(ForceCenter.X, AngryCenter.X), maximumSettingX) + ButtonRadius;
            float maximumButtonY = Math.Max(Math.Max(ForceCenter.Y, AngryCenter.Y), maximumSettingY) + ButtonRadius;
            float maximumSliderY = maximumSettingY + SliderTravel + Math.Max(ButtonRadius, SliderValueLabelHalfHeight);
            float maximumThrowLabelY = AngryCenter.Y + Math.Max(ButtonRadius, SliderValueLabelHalfHeight);
            return new(Math.Max(StatusMax.X, Math.Max(MoveCenter.X + MoveRadius,
                    Math.Max(maximumButtonX, SliderValueLabelMaxX))),
                Math.Max(StatusMax.Y, Math.Max(MoveCenter.Y + MoveRadius,
                    Math.Max(Math.Max(maximumButtonY, maximumSliderY), maximumThrowLabelY))));
        }
    }

    /// <summary>Bounds of a settings or throw circle expanded vertically into its slider pill.</summary>
    public (float2 Min, float2 Max) SliderEnvelope(ForceOverlayHit hit)
    {
        var center = hit switch
        {
            ForceOverlayHit.CaptureLimit => CaptureLimitCenter,
            ForceOverlayHit.AngryRadius => AngryRadiusCenter,
            ForceOverlayHit.AngryForce => AngryCenter,
            _ => new float2(float.NaN)
        };
        if (!Valid || !float.IsFinite(center.X)) return default;
        if (hit == ForceOverlayHit.AngryForce)
            return (center - new float2(ButtonRadius, SliderTravel + ButtonRadius), center + new float2(ButtonRadius));
        return (center - new float2(ButtonRadius),
            center + new float2(ButtonRadius, SliderTravel + ButtonRadius));
    }

    /// <summary>Moves all Force controls and their status as one bounded group.</summary>
    public ForceOverlayLayout WithControlsOffset(float2 origin, float2 size, float2 normalizedOffset, float topInset = 0)
    {
        if (!Valid || !SaberOverlayLayout.TryControlsTranslation(ControlsMin, ControlsMax, origin, size,
            normalizedOffset, topInset, out var delta)) return default;
        return this with { CaptureLimitCenter = CaptureLimitCenter + delta,
            AngryRadiusCenter = AngryRadiusCenter + delta, ForceCenter = ForceCenter + delta, AngryCenter = AngryCenter + delta,
            MoveCenter = MoveCenter + delta, StatusMin = StatusMin + delta, StatusMax = StatusMax + delta };
    }

    public ForceOverlayHit Hit(float2 pointer)
    {
        if (!Valid) return ForceOverlayHit.None;
        if (SaberOverlayDrag.ContainsCircle(pointer, ForceCenter, ButtonRadius)) return ForceOverlayHit.Force;
        if (SaberOverlayDrag.ContainsCircle(pointer, AngryCenter, ButtonRadius)) return ForceOverlayHit.AngryForce;
        if (SaberOverlayDrag.ContainsCircle(pointer, AngryRadiusCenter, ButtonRadius)) return ForceOverlayHit.AngryRadius;
        if (SaberOverlayDrag.ContainsCircle(pointer, CaptureLimitCenter, ButtonRadius)) return ForceOverlayHit.CaptureLimit;
        return SaberOverlayDrag.ContainsCircle(pointer, MoveCenter, MoveRadius) ? ForceOverlayHit.Move : ForceOverlayHit.None;
    }

    public static ForceOverlayLayout Create(SaberOverlayLayout saber, float2 origin, float2 size,
        float fontSize = 16, float throwLabelWidth = -1)
    {
        if (!saber.Valid || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0
            || !float.IsFinite(fontSize) || fontSize <= 0) return default;
        if (!float.IsFinite(throwLabelWidth) || throwLabelWidth < 0) throwLabelWidth = fontSize * 3.5f;
        float scale = saber.Scale;
        float edgeGap = saber.SwingSliceEdgeGap;
        float availableRight = Math.Min(saber.SwingCenter.X - saber.SwingRadius,
            saber.SliceCenter.X - saber.SliceRadius) - origin.X;
        float interGroupGap = Math.Max(1, saber.Scale);
        for (int i = 0; i < 64 && EstimateRightEdge(scale) + interGroupGap > availableRight; i++)
            scale *= .98f;
        if (EstimateRightEdge(scale) + interGroupGap > availableRight) return default;
        float radius = 132 * scale, button = 27 * scale;
        float centerDistance = radius + button + edgeGap;
        float adjacentCenterDistance = button * 2 + edgeGap;
        float angleStep = 2 * MathF.Asin(Math.Clamp(adjacentCenterDistance / (2 * centerDistance), -1, 1));
        // The wider centered info box needs 198 scaled pixels on each side of Move.
        var move = new float2(origin.X + 198 * scale, saber.SwingCenter.Y);
        const float angryAngleDegrees = 5;
        float angleStepDegrees = angleStep * 180 / MathF.PI;
        var angry = AtAngle(angryAngleDegrees);
        var force = AtAngle(angryAngleDegrees + angleStepDegrees);
        var angryRadius = AtAngle(angryAngleDegrees + 2 * angleStepDegrees);
        var captureLimit = AtAngle(angryAngleDegrees + 3 * angleStepDegrees);
        const float statusWidth = 396;
        const float statusHeight = 96;
        const float statusGap = 25;
        float boxWidth = statusWidth * scale, boxHeight = statusHeight * scale;
        var statusMax = new float2(move.X + boxWidth * .5f, move.Y - radius - statusGap * scale);
        var statusMin = new float2(move.X - boxWidth * .5f, statusMax.Y - boxHeight);
        return new(captureLimit, angryRadius, angry, force, button, move, radius,
            statusMin, statusMax, scale, fontSize, throwLabelWidth);

        float2 AtAngle(float degrees)
        {
            float radians = degrees * MathF.PI / 180;
            return move + new float2(MathF.Cos(radians), MathF.Sin(radians)) * centerDistance;
        }

        float EstimateRightEdge(float candidateScale)
        {
            float candidateRadius = 132 * candidateScale, candidateButton = 27 * candidateScale;
            float distance = candidateRadius + candidateButton + edgeGap;
            float step = 2 * MathF.Asin(Math.Clamp((candidateButton * 2 + edgeGap) / (2 * distance), -1, 1));
            float stepDegrees = step * 180 / MathF.PI;
            float moveX = 198 * candidateScale;
            float angryX = moveX + distance * MathF.Cos(5 * MathF.PI / 180);
            float forceX = moveX + distance * MathF.Cos((5 + stepDegrees) * MathF.PI / 180);
            float distanceX = moveX + distance * MathF.Cos((5 + 2 * stepDegrees) * MathF.PI / 180);
            float countX = moveX + distance * MathF.Cos((5 + 3 * stepDegrees) * MathF.PI / 180);
            float valueLabelWidth = Math.Max(64 * candidateScale, throwLabelWidth);
            float valueLabelMaxX = Math.Max(Math.Max(countX, distanceX), angryX) + candidateButton
                + Math.Max(4, 6 * candidateScale) + valueLabelWidth;
            return Math.Max(396 * candidateScale,
                Math.Max(moveX + candidateRadius, Math.Max(angryX + candidateButton,
                    Math.Max(forceX + candidateButton, valueLabelMaxX))));
        }
    }
}
