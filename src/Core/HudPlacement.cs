using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>Placement math shared by the window host and game-free regression checks.</summary>
public static class HudPlacement
{
    public static bool Contains(float2 min, float2 max, float2 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y)
        && point.X >= min.X && point.X < max.X && point.Y >= min.Y && point.Y < max.Y;

    public static float2 ClampPosition(float2 position, float2 size, float2 workMin, float2 workSize)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) position = workMin;
        return new(Math.Clamp(position.X, workMin.X, workMin.X + Math.Max(0, workSize.X - size.X)),
            Math.Clamp(position.Y, workMin.Y, workMin.Y + Math.Max(0, workSize.Y - size.Y)));
    }

    // A visible title bar lets the user recover a panel even when its contents are taller than a monitor.
    public static bool TitleVisible(float2 position, float2 size, float2 workMin, float2 workSize, float titleHeight)
        => position.Y >= workMin.Y && position.Y + titleHeight <= workMin.Y + workSize.Y
        && position.X + Math.Min(size.X, 100) > workMin.X
        && position.X < workMin.X + workSize.X - Math.Min(size.X, 100);
}
