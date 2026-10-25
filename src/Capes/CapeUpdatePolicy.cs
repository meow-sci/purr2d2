using System;
using Purr2D2.Core;

namespace Purr2D2.Capes;

/// <summary>Small allocation-free budget and frame-clock helpers for cape simulation.</summary>
internal static class CapeUpdatePolicy
{
    // Fixed storage capacity follows the configurable setting's hard maximum.
    internal const int MaximumSimulatedCapes = SaberSettings.MaximumCapeSimulationLimit;
    internal const int MaxStepCallsPerFrame = 2;
    internal const float SimulationInterval = 1f / 30f;
    internal const float SimulationDistance = 25f;
    internal const float RenderDistance = 60f;
    internal const float ReleaseAfterIdle = 1.5f;

    internal readonly record struct Candidate(int Index, double DistanceSquared);

    internal static bool WasVisibleRecently(ulong frame, ulong lastMainDrawFrame) =>
        lastMainDrawFrame != ulong.MaxValue && frame >= lastMainDrawFrame && frame - lastMainDrawFrame <= 1;

    internal static bool CanRenderSquared(double distanceSquared) =>
        double.IsFinite(distanceSquared) && distanceSquared >= 0d
        && distanceSquared <= (double)RenderDistance * RenderDistance;

    internal static bool CanSimulateSquared(bool enabled, bool visibleRecently, double distanceSquared) =>
        enabled && visibleRecently && double.IsFinite(distanceSquared) && distanceSquared >= 0d
        && distanceSquared <= (double)SimulationDistance * SimulationDistance;

    internal static void ClearCandidates(Span<Candidate> candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
            candidates[i] = new(-1, double.PositiveInfinity);
    }

    /// <summary>Keep the nearest candidates in stable squared-distance/index order.</summary>
    internal static void OfferCandidateSquared(Span<Candidate> candidates, int index, double distanceSquared)
    {
        if (index < 0 || !double.IsFinite(distanceSquared) || distanceSquared < 0d) return;

        int insert = candidates.Length;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate current = candidates[i];
            if (current.Index < 0 || distanceSquared < current.DistanceSquared
                || distanceSquared == current.DistanceSquared && index < current.Index)
            {
                insert = i;
                break;
            }
        }
        if (insert >= candidates.Length) return;
        for (int i = candidates.Length - 1; i > insert; i--)
            candidates[i] = candidates[i - 1];
        candidates[insert] = new(index, distanceSquared);
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
    /// Selects due entries in owner-index order. The distance-ranked span may be
    /// reordered every frame, so the cursor tracks stable compact-owner indices,
    /// not candidate slots. At most two distinct controllers are returned.
    /// </summary>
    internal static int SelectDue(ReadOnlySpan<Candidate> candidates, ReadOnlySpan<bool> due,
        Span<int> selected, ref int cursor)
    {
        int count = 0;
        int budget = Math.Min(MaxStepCallsPerFrame, selected.Length);
        int length = Math.Min(candidates.Length, due.Length);
        if (length == 0) { cursor = 0; return 0; }
        if (budget == 0) return 0;

        while (count < budget)
        {
            int chosen = -1;
            int chosenOwner = int.MaxValue;
            for (int i = 0; i < length; i++)
            {
                int owner = candidates[i].Index;
                if (!due[i] || owner < 0 || owner < cursor || IsSelected(i, selected[..count])) continue;
                if (owner < chosenOwner || owner == chosenOwner && i < chosen)
                {
                    chosen = i;
                    chosenOwner = owner;
                }
            }

            if (chosen < 0)
            {
                // Wrap to the lowest due owner when this frame's candidate set
                // contains no due index at or beyond the cursor.
                for (int i = 0; i < length; i++)
                {
                    int owner = candidates[i].Index;
                    if (!due[i] || owner < 0 || IsSelected(i, selected[..count])) continue;
                    if (owner < chosenOwner || owner == chosenOwner && i < chosen)
                    {
                        chosen = i;
                        chosenOwner = owner;
                    }
                }
            }

            if (chosen < 0) break;
            selected[count++] = chosen;
            cursor = chosenOwner + 1;
        }
        return count;

        static bool IsSelected(int candidateIndex, ReadOnlySpan<int> selectedIndices)
        {
            for (int i = 0; i < selectedIndices.Length; i++)
                if (selectedIndices[i] == candidateIndex) return true;
            return false;
        }
    }
}
