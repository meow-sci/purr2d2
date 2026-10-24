using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal sealed partial class ForceController
{
    private readonly List<BreakupEntry> _breakupPlans = [];
    private bool _breakupEligible, _breakupActive, _breakupPrepared, _breakupCountPreflighted;
    private bool _breakupRefused;
    private int _breakupPrepareIndex, _breakupCommitIndex;

    private sealed record BreakupEntry(Held Held, ForceVehicleBreakup Plan);

    internal bool CanBreakup => HasCapture && _breakupEligible;
    internal bool BreakupActive => _breakupActive;
    internal bool BreakupPrepared => _breakupPrepared;

    /// <summary>Only intact held vehicles enter the staged, incrementally prepared path.</summary>
    internal bool BeginBreakup(KittenEva owner)
    {
        if (_breakupActive || !HasCapture || !ReferenceEquals(Owner, owner) || !CanBreakup) return false;
        _breakupRefused = false;
        SetStatus($"Holding {CapturedCount} object{(CapturedCount == 1 ? "" : "s")}.", ForceStatusSeverity.None);
        var plans = new List<BreakupEntry>();
        try
        {
            JoinOrbitReaders();
            foreach (var held in _held)
                if (held.Source is { } source && ForceVehicleBreakup.NeedsBreakup(source))
                    plans.Add(new(held, new ForceVehicleBreakup(source)));
            if (plans.Count == 0)
            {
                RefreshBreakupEligibility();
                return false;
            }
            _breakupPlans.AddRange(plans);
            plans.Clear();
            _breakupActive = true;
            return true;
        }
        catch (Exception error)
        {
            DisposePlans(plans);
            _breakupRefused = true;
            SetStatus("Force breakup could not start: " + error.Message, ForceStatusSeverity.Warning);
            Console.WriteLine($"[purr2d2] {Status}");
            return false;
        }
    }

    /// <summary>
    /// Advances plans fairly under one shared bounded handoff budget. The complete
    /// replacement count is checked before the first source is drained, and each
    /// visible commit requires both user tension and the independent visual-delay gate.
    /// </summary>
    internal ForceBreakupStepResult AdvanceBreakup(KittenEva owner, bool splitRequested, bool delayElapsed)
    {
        if (!_breakupActive) return default;
        if (!HasCapture || !ReferenceEquals(Owner, owner))
        {
            CancelBreakup();
            return default;
        }

        try
        {
            if (!_breakupPrepared)
            {
                long started = Stopwatch.GetTimestamp();
                for (int advances = 0; advances < 8 && !_breakupPrepared; advances++)
                {
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    bool budgetAvailable = elapsed < 1.5;
                    int nextPlan = FindNextPlanToAdvance(budgetAvailable);
                    if (nextPlan < 0) break;
                    var entry = _breakupPlans[nextPlan];
                    if (!entry.Plan.CaptureComplete && !entry.Plan.IsCurrent())
                        throw new InvalidOperationException("A source changed before breakup preparation finished.");
                    double remainingBudget = Math.Max(0, 1.5 - elapsed);
                    entry.Plan.AdvancePreparation(remainingBudget);
                    if (entry.Plan.Failed) throw new InvalidOperationException(entry.Plan.Error ?? "Breakup preparation failed.");
                    if (_breakupPlans.All(plan => plan.Plan.CaptureComplete)) PreflightCapturedCount();
                    if (_breakupPlans.All(plan => plan.Plan.Ready)) PreflightReplacementCount();
                    _breakupPrepareIndex = (nextPlan + 1) % _breakupPlans.Count;
                }
                if (!_breakupPrepared) return new(true, false, false, false);
            }

            if (!splitRequested || !delayElapsed) return new(true, false, false, false);

            CommitOne(owner);
            bool complete = _breakupCommitIndex >= _breakupPlans.Count;
            if (complete)
            {
                CancelBreakupPlans();
                RefreshBreakupEligibility();
                if (StatusSeverity == ForceStatusSeverity.None)
                    SetStatus($"Holding {CapturedCount} objects.", ForceStatusSeverity.None);
            }
            return new(!complete, false, true, complete);
        }
        catch (Exception error)
        {
            FailBreakup(error);
            return new(false, true, false, false);
        }
    }

    /// <summary>Cancel any warmed plan before ownership or the active gesture changes.</summary>
    internal void CancelBreakup()
    {
        CancelBreakupPlans();
        _breakupRefused = false;
    }

    private void PreflightReplacementCount()
    {
        if (_breakupPrepared) return;
        if (!_breakupCountPreflighted)
            throw new InvalidOperationException("The complete breakup count must be known before commit.");
        long resultingCount = _held.Count - _breakupPlans.Count;
        foreach (var entry in _breakupPlans)
        {
            if (!entry.Plan.Ready || entry.Plan.Count < 1 || !entry.Plan.IsCurrent())
                throw new InvalidOperationException("Every held vehicle must finish current preparation before breakup can commit.");
            resultingCount += entry.Plan.Count;
        }
        if (resultingCount > ForceCaptureLimits.Maximum)
            throw new InvalidOperationException($"Breakup would exceed {ForceCaptureLimits.Maximum} held objects.");
        _breakupPrepared = true;
    }

    private void PreflightCapturedCount()
    {
        if (_breakupCountPreflighted) return;
        long resultingCount = _held.Count - _breakupPlans.Count;
        foreach (var entry in _breakupPlans)
        {
            if (!entry.Plan.CaptureComplete || entry.Plan.Count < 1)
                throw new InvalidOperationException("Every held vehicle must finish source capture before breakup can proceed.");
            resultingCount += entry.Plan.Count;
        }
        if (resultingCount > ForceCaptureLimits.Maximum)
            throw new InvalidOperationException($"Breakup would exceed {ForceCaptureLimits.Maximum} held objects.");
        _breakupCountPreflighted = true;
    }

    private int FindNextPlanToAdvance(bool budgetAvailable)
    {
        if (budgetAvailable)
        {
            for (int offset = 0; offset < _breakupPlans.Count; offset++)
            {
                int index = (_breakupPrepareIndex + offset) % _breakupPlans.Count;
                if (!_breakupPlans[index].Plan.Ready && !_breakupPlans[index].Plan.CaptureComplete) return index;
            }
        }
        for (int offset = 0; offset < _breakupPlans.Count; offset++)
        {
            int index = (_breakupPrepareIndex + offset) % _breakupPlans.Count;
            var plan = _breakupPlans[index].Plan;
            if (!plan.Ready && (budgetAvailable || plan.CaptureComplete)) return index;
        }
        return -1;
    }

    private void CommitOne(KittenEva owner)
    {
        var entry = _breakupPlans[_breakupCommitIndex];
        if (!entry.Plan.Ready || !entry.Plan.IsCurrent())
            throw new InvalidOperationException("A held source changed before breakup could commit.");

        // Ensure the current owner transform is reflected in every held source before
        // the backend snapshots the source pose for its replacement vehicles. The
        // normal runtime update already did this, so this call usually exits early.
        Follow(Universe.GetLastSimStep().NextTime);
        if (!entry.Plan.IsCurrent())
            throw new InvalidOperationException("A held source changed before breakup could commit.");

        var pieces = entry.Plan.Commit();
        if (pieces.Length != entry.Plan.Count)
            throw new InvalidOperationException("The committed breakup did not return every prepared piece.");
        foreach (var piece in pieces) ForcePhysicsPatches.Detach(piece);

        var inverseOwner = owner.GetBody2Cci().NormalizedOrZero().Inverse();
        var ownerPosition = owner.GetPositionCci();
        TryGetPoseTargetBody(owner, out var previousAim);
        int index = _held.IndexOf(entry.Held);
        if (index < 0) throw new InvalidOperationException("A prepared source is no longer in the held group.");

        var replacements = pieces.Select(piece =>
        {
            var state = new ForceRigidState(piece.GetPositionCci(), piece.GetVelocityCci(), piece.GetBody2Cci(), piece.BodyRates);
            // Invert the currently displayed spread/translation. The pieces start at the
            // source's freshly committed pose, even if the owner moved during preparation.
            var centered = ((state.Position - ownerPosition).Transform(inverseOwner)
                - _groupCenterBody - GroupOffsetBody) / GroupSpread;
            return new Held(new ForceTarget(piece), piece.Parent, piece.Parts, piece.Parts.Parts.ToArray(),
                centered, doubleQuat.Concatenate(state.Body2Cci, inverseOwner), state);
        }).ToArray();

        _held.RemoveAt(index);
        _held.InsertRange(index, replacements);
        _primaryCenteredOffsetBody = _held[0].PositionBody;
        _anchorBody = previousAim - _primaryCenteredOffsetBody * (GroupSpread - 1) - GroupOffsetBody;
        ForcePhysicsPatches.Publish(_held.Where(item => item.Source != null).Select(item => item.Source!).ToArray());
        // Pieces inherit the just-followed source's displayed rigid pose. Let the next
        // ordinary handoff write the new group's FlightPlans instead of rebuilding all
        // held orbits a second time in this visible commit frame.
        _hasFrame = false;
        _breakupCommitIndex++;
        entry.Plan.Dispose();
        RefreshBreakupEligibility();
    }

    private void RefreshBreakupEligibility()
    {
        _breakupEligible = false;
        foreach (var held in _held)
            if (held.Source is { } source && ForceVehicleBreakup.NeedsBreakup(source))
            {
                _breakupEligible = true;
                break;
            }
    }

    private void FailBreakup(Exception error)
    {
        CancelBreakupPlans();
        _breakupRefused = true;
        RefreshBreakupEligibility();
        SetStatus("Force breakup stopped: " + error.Message, ForceStatusSeverity.Warning);
        Console.WriteLine($"[purr2d2] {Status}");
    }

    private void CancelBreakupPlans()
    {
        DisposePlans(_breakupPlans);
        _breakupPlans.Clear();
        _breakupPrepareIndex = _breakupCommitIndex = 0;
        _breakupCountPreflighted = _breakupPrepared = _breakupActive = false;
    }

    private static void DisposePlans(IEnumerable<BreakupEntry> plans)
    {
        foreach (var entry in plans)
            try { entry.Plan.Dispose(); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force breakup preparation cleanup failed: {cleanup}"); }
    }
}
