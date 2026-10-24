using System;
using Brutal.Numerics;

namespace Purr2D2.Capes;

/// <summary>Rest shape for a small cloth cape attached across the kitten's shoulders.</summary>
public static class CapeGeometry
{
    public const int Columns = 9;
    public const int Rows = 13;
    public const int NodeCount = Columns * Rows;

    /// <summary>Returns a rest-space node. Rows run from the pinned shoulders to the hem.</summary>
    public static float3 RestPosition(int index)
    {
        if ((uint)index >= NodeCount) throw new ArgumentOutOfRangeException(nameof(index));

        int row = index / Columns;
        int column = index % Columns;
        float u = column / (float)(Columns - 1);
        float t = row / (float)(Rows - 1);
        return new float3(
            (u - .5f) * (.64f + .56f * t),
            .10f - .88f * t,
            -.30f - .06f * t + .025f * MathF.Sin(u * 4f * MathF.PI) * MathF.Sin(t * MathF.PI / 2f));
    }
}
