using Brutal.Numerics;
using KSA;

namespace Purr2D2.Cutting;

/// <summary>Button-triggered geometry selection. No per-frame contact or damage update exists.</summary>
internal sealed partial class PartSliceTargeting
{
    private const int MaxCandidateSubparts = 32_768;
    private const int MaxCachedTriangles = 4_000_000;
    private KittenEva? _wielder;
    internal bool BudgetLimited { get; private set; }
    internal int SlicedParts { get; private set; }
    internal Func<Part, SlicePlane, SliceAttempt>? SliceHandler { get; set; }
    internal string Status { get; private set; } = "Ready";
    internal void Reset()
    {
        SlicedParts = 0; _wielder = null; BudgetLimited = false; Status = "Ready";
    }

    private bool CanCut(Part full, out string reason)
    {
        var owner = full.Tree?.OwningVehicle;
        reason = "The source part is unavailable.";
        if (owner == null || owner.IsDisposed || owner is KittenEva) return false;
        if (!owner.HasPhysicsBubble) reason = "Slicing requires a physics bubble";
        else if (!ReferenceEquals(owner.Parent, _wielder?.Parent)) reason = "Slicing requires the same parent body";
        else if (HasAssignedCrew(owner)) reason = "Crew aboard: cutting refused";
        else if (owner.BubbleVehicleCount >= PartFailure.MaximumVehiclesPerBubble) reason = "Debris budget reached";
        else if (full.IsAttachedInternal) reason = "Internal parts cannot be selected independently";
        else { reason = ""; return true; }
        return false;
    }

    private static bool HasAssignedCrew(Vehicle vehicle)
    { foreach (var seat in vehicle.Crew) if (seat.AssignedKittenHash != KeyHash.Zero) return true; return false; }
    private static CutPoint ToPoint(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 ToGame(CutPoint value) => new(value.X, value.Y, value.Z);
    private static CutPoint Transform(CutPoint value, double4x4 matrix) => ToPoint(ToGame(value).Transform(matrix));
}
