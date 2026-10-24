using System;

namespace Purr2D2.Capes;

/// <summary>Small allocation-free budget and frame-clock helpers for cape simulation.</summary>
internal static class CapeUpdatePolicy
{
    internal const int MaxSimulatedCapes = 4;
    internal const int MaxStepCallsPerFrame = 2;
    internal const float SimulationInterval = 1f / 30f;
    internal const float SimulationDistance = 25f;
    internal const float RenderDistance = 60f;
    internal const float ReleaseAfterIdle = 1.5f;

    internal readonly record struct Candidate(int Index, float Distance);

    internal static bool WasVisibleRecently(ulong frame, ulong lastMainDrawFrame) =>
        lastMainDrawFrame != ulong.MaxValue && frame >= lastMainDrawFrame && frame - lastMainDrawFrame <= 1;

    internal static bool CanRender(float distance) =>
        float.IsFinite(distance) && distance >= 0f && distance <= RenderDistance;

    internal static bool CanSimulate(bool enabled, bool visibleRecently, float distance) =>
        enabled && visibleRecently && float.IsFinite(distance) && distance >= 0f && distance <= SimulationDistance;

    internal static void ClearCandidates(Span<Candidate> candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
            candidates[i] = new(-1, float.PositiveInfinity);
    }

    /// <summary>Keep the nearest candidates in stable distance/index order.</summary>
    internal static void OfferCandidate(Span<Candidate> candidates, int index, float distance)
    {
        if (index < 0 || !float.IsFinite(distance) || distance < 0f) return;

        int insert = candidates.Length;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate current = candidates[i];
            if (current.Index < 0 || distance < current.Distance || distance == current.Distance && index < current.Index)
            {
                insert = i;
                break;
            }
        }
        if (insert >= candidates.Length) return;
        for (int i = candidates.Length - 1; i > insert; i--)
            candidates[i] = candidates[i - 1];
        candidates[insert] = new(index, distance);
    }

    /// <summary>
    /// Adds player time up to one 30 Hz quantum. Paused or invalid time leaves the
    /// partial quantum frozen; a consumed quantum drops all accumulated backlog.
    /// </summary>
    internal static void Accumulate(ref float elapsed, double playerDt, bool advancing)
    {
        if (!advancing || !double.IsFinite(playerDt) || playerDt <= 0d) return;
        float dt = (float)Math.Min(playerDt, .1d);
        if (float.IsFinite(dt) && dt > 0f)
            elapsed = MathF.Min(SimulationInterval, MathF.Max(0f, elapsed) + dt);
    }

    internal static bool IsDue(float elapsed) => elapsed >= SimulationInterval;

    internal static float Consume(ref float elapsed)
    {
        if (!IsDue(elapsed)) return 0f;
        elapsed = 0f;
        return SimulationInterval;
    }

    /// <summary>
    /// Selects due entries in a rotating order. At most two distinct controllers
    /// are returned, and a stable cursor prevents later entries from starving.
    /// </summary>
    internal static int SelectDue(ReadOnlySpan<bool> due, Span<int> selected, ref int cursor)
    {
        int count = 0;
        int budget = Math.Min(MaxStepCallsPerFrame, selected.Length);
        if (due.Length == 0 || budget == 0) { cursor = 0; return 0; }
        cursor = ((cursor % due.Length) + due.Length) % due.Length;

        for (int inspected = 0; inspected < due.Length && count < budget; inspected++)
        {
            int index = cursor;
            cursor = (cursor + 1) % due.Length;
            if (due[index]) selected[count++] = index;
        }
        return count;
    }
}
