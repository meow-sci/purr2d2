using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal sealed partial class ForceController
{
    private bool _breakupRefused;
    /// <summary>Only an explicit expand-wheel request calls this, at the joined handoff.</summary>
    internal void BreakApart(KittenEva owner)
    {
        _breakupRefused = false;
        // Update performs the same owner/world/topology validation as ordinary motion.
        Update(owner);
        if (!HasCapture || !ReferenceEquals(Owner, owner)) return;
        var plans = new List<(Held Held, ForceVehicleBreakup Plan)>();
        try
        {
            JoinOrbitReaders();
            int count = CapturedCount;
            foreach (var held in _held)
                if (held.Source is { } source && ForceVehicleBreakup.NeedsBreakup(source))
                {
                    var plan = new ForceVehicleBreakup(source);
                    plans.Add((held, plan));
                    count += plan.Count - 1;
                    if (count > ForceCaptureLimits.Maximum)
                        throw new InvalidOperationException($"Breakup would exceed {ForceCaptureLimits.Maximum} held objects.");
                }
            if (plans.Count == 0) return;
            // Prepare every native root before draining or deleting any captured vehicle.
            foreach (var (_, plan) in plans) plan.Prepare();
            var inverseOwner = owner.GetBody2Cci().NormalizedOrZero().Inverse();
            var ownerPosition = owner.GetPositionCci();
            TryGetPoseTargetBody(owner, out var previousAim);
            foreach (var (held, plan) in plans)
            {
                var pieces = plan.Commit();
                int index = _held.IndexOf(held);
                var replacements = pieces.Select(piece =>
                {
                    var state = new ForceRigidState(piece.GetPositionCci(), piece.GetVelocityCci(), piece.GetBody2Cci(), piece.BodyRates);
                    // Invert the currently displayed spread/translation. The new pieces
                    // start exactly on the old surfaces, including after earlier compression.
                    var centered = ((state.Position - ownerPosition).Transform(inverseOwner)
                        - _groupCenterBody - GroupOffsetBody) / GroupSpread;
                    return new Held(new ForceTarget(piece), piece.Parent, piece.Parts, piece.Parts.Parts.ToArray(),
                        centered, doubleQuat.Concatenate(state.Body2Cci, inverseOwner), state);
                }).ToArray();
                _held.RemoveAt(index);
                _held.InsertRange(index, replacements);
                _primaryCenteredOffsetBody = _held[0].PositionBody;
                _anchorBody = previousAim - _primaryCenteredOffsetBody * (GroupSpread - 1) - GroupOffsetBody;
                // A later native failure must never strand already committed peers.
                ForcePhysicsPatches.Publish(_held.Where(item => item.Source != null).Select(item => item.Source!).ToArray());
            }
            _hasFrame = false;
            Follow(Universe.GetLastSimStep().NextTime);
            SetStatus($"Holding {CapturedCount} objects.", ForceStatusSeverity.None);
        }
        catch (Exception error)
        {
            _hasFrame = false;
            _breakupRefused = true;
            SetStatus("Force breakup stopped: " + error.Message, ForceStatusSeverity.Warning);
            Console.WriteLine($"[purr2d2] {Status}");
        }
        finally
        {
            foreach (var (_, plan) in plans)
                try { plan.Dispose(); }
                catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force breakup preparation cleanup failed: {cleanup}"); }
        }
    }
}
