using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal sealed partial class ForceController
{
    private readonly List<ForceTarget> _growthCandidates = new(ForceCaptureLimits.Maximum);
    private readonly List<Held> _growthAdditions = new(ForceCaptureLimits.Maximum);
    private readonly HashSet<string> _growthVehicleIds = new(StringComparer.Ordinal);
    private readonly HashSet<ClutterForceIdentity> _growthClutterIds = [];

    /// <summary>The selected point follows the captured group in the owner's body frame.</summary>
    internal bool TryGetCaptureCenterCci(KittenEva owner, out double3 centerCci)
    {
        centerCci = default;
        if (!TryGetPoseTargetBody(owner, out var centerBody)) return false;
        centerCci = owner.GetPositionCci() + centerBody.Transform(owner.GetBody2Cci());
        return ForceMath.Finite(centerCci);
    }

    /// <summary>
    /// Append the new nearest objects inside an expanded selected-point sphere. This is a settled-only
    /// transaction: the existing hold and its published worker array stay intact if any addition fails.
    /// </summary>
    internal bool GrowCapture(KittenEva owner, double radius, int captureLimit = ForceCaptureLimits.Default)
    {
        if (!_available || _disposed) { SetStatus("Force physics is unavailable.", ForceStatusSeverity.Warning); return false; }
        if (!HasCapture || !ReferenceEquals(Owner, owner) || !ReferenceEquals(owner, Program.ControlledVehicle)
            || !ForceTargeting.IsLive(owner) || !ReferenceEquals(_system, Universe.CurrentSystem)
            || !ReferenceEquals(owner.Parent, _parent) || Program.EditorFlag)
        { SetStatus("Force growth was discarded because the held group changed.", ForceStatusSeverity.Warning); return false; }
        if (!double.IsFinite(radius) || radius < CaptureRadius || radius > ForceMath.MaximumAngryRadius)
        { SetStatus("Force capture radius can only grow from its current value to 50 m.", ForceStatusSeverity.Warning); return false; }
        if (!ForceCaptureLimits.Valid(captureLimit))
        { SetStatus("The Force capture limit must be between 1 and 512 objects; no additional objects were captured.", ForceStatusSeverity.Warning); return false; }
        if (_breakupActive)
        { SetStatus("Force growth is waiting for breakup to finish.", ForceStatusSeverity.Warning); return false; }
        if (radius == CaptureRadius) return true;

        foreach (var held in _held)
            if (!Valid(held))
            { SetStatus("Force growth was cancelled because a held object changed.", ForceStatusSeverity.Warning); return false; }

        // The gesture's visible radius is accepted even at capacity or when a bounded query refuses.
        // A later, larger pointer value is a distinct attempt; the same value is never retried per frame.
        CaptureRadius = radius;
        int remaining = Math.Min(ForceCaptureLimits.Maximum - CapturedCount,
            Math.Max(0, captureLimit - CapturedCount));
        if (remaining == 0) return true;

        _growthCandidates.Clear();
        _growthAdditions.Clear();
        _growthVehicleIds.Clear();
        _growthClutterIds.Clear();
        try
        {
            JoinOrbitReaders();
            if (!TryGetCaptureCenterCci(owner, out var centerCci))
                throw new InvalidOperationException("The selected point left the held group frame.");
            foreach (var held in _held)
            {
                if (held.Source is { } source) _growthVehicleIds.Add(source.Id);
                else if (held.Target.Clutter is { } clutter) _growthClutterIds.Add(clutter.StableKey);
            }
            if (!ForceTargeting.GatherAdditional(owner, centerCci, radius, _growthVehicleIds, _growthClutterIds,
                _growthCandidates, remaining, out var refusal))
            {
                SetStatus(refusal, ForceStatusSeverity.Warning);
                return false;
            }
            if (_growthCandidates.Count == 0) return true;

            SnapshotGrowthAdditions(owner, _growthCandidates);
            AcquireGrowthClutter();
            CommitGrowthAdditions(owner);
            return true;
        }
        catch (Exception error)
        {
            RollbackGrowthAdditions();
            SetStatus("Force growth failed; the existing hold was preserved: " + error.Message,
                ForceStatusSeverity.Warning);
            Console.WriteLine($"[purr2d2] {Status}");
            return false;
        }
        finally
        {
            _growthCandidates.Clear();
            _growthAdditions.Clear();
            _growthVehicleIds.Clear();
            _growthClutterIds.Clear();
        }
    }

    private void SnapshotGrowthAdditions(KittenEva owner, List<ForceTarget> candidates)
    {
        var ownerRotation = owner.GetBody2Cci().NormalizedOrZero();
        var inverseOwner = ownerRotation.Inverse();
        var ownerPosition = owner.GetPositionCci();
        if (ownerRotation == default || !ForceMath.Finite(ownerPosition))
            throw new InvalidOperationException("Kitten frame is invalid.");

        // Snapshot every candidate before the first clutter promotion or object write.
        foreach (var candidate in candidates)
        {
            ForceRigidState original;
            IParentBody parent;
            PartTree? tree = null;
            Part[] parts = [];
            if (candidate.Vehicle is { } source && candidate.Clutter == null
                && ForceTargeting.IsLive(source) && !ReferenceEquals(source, owner)
                && ReferenceEquals(source.System, owner.System) && ReferenceEquals(source.Parent, owner.Parent))
            {
                parent = source.Parent;
                tree = source.Parts;
                parts = source.Parts.Parts.ToArray();
                original = new(source.GetPositionCci(), source.GetVelocityCci(), source.GetBody2Cci(), source.BodyRates);
            }
            else if (candidate.Vehicle == null && candidate.Clutter is { } clutter
                && clutter.IsValid && clutter.IsLocalTo(owner) && ReferenceEquals(clutter.Parent, owner.Parent)
                && ReferenceEquals(clutter.System, owner.System) && clutter.TryRead(out original)) parent = clutter.Parent;
            else throw new InvalidOperationException("A selected object changed before growth could snapshot it.");

            if (!ReferenceEquals(parent, owner.Parent) || original.Body2Cci.NormalizedOrZero() == default
                || !ForceMath.Finite(original.Position) || !ForceMath.Finite(original.Velocity)
                || !ForceMath.Finite(original.BodyRates))
                throw new InvalidOperationException("A new Force object has an invalid rigid state.");

            // Invert the current spread/translation so its existing world pose is reproduced exactly.
            var centered = ((original.Position - ownerPosition).Transform(inverseOwner)
                - _groupCenterBody - GroupOffsetBody) / GroupSpread;
            var sourceToOwner = doubleQuat.Concatenate(original.Body2Cci, inverseOwner).NormalizedOrZero();
            if (!ForceMath.Finite(centered) || sourceToOwner == default)
                throw new InvalidOperationException("A new Force object has an invalid group transform.");
            _growthAdditions.Add(new(candidate, parent, tree, parts, centered, sourceToOwner, original));
        }
    }

    private void AcquireGrowthClutter()
    {
        foreach (var held in _growthAdditions)
        {
            if (held.Target.Clutter is not { } clutter) continue;
            if (!clutter.TryCapture(out var hold, out var refusal) || hold == null)
                throw new InvalidOperationException(refusal);
            held.ClutterHold = hold;
        }
    }

    private void CommitGrowthAdditions(KittenEva owner)
    {
        var time = Universe.GetLastSimStep().NextTime;
        var ownerPosition = owner.GetPositionCci();
        var ownerVelocity = owner.GetVelocityCci();
        var ownerRotation = owner.GetBody2Cci();
        var ownerRates = owner.BodyRates;
        var resultingCount = CapturedCount + _growthAdditions.Count;
        if (resultingCount > ForceCaptureLimits.Maximum)
            throw new InvalidOperationException("The held group reached its 512-object safety cap.");

        int sourceCount = 0;
        bool breakupEligible = _breakupEligible;
        foreach (var held in _held)
            if (held.Source is { } source)
            {
                sourceCount++;
                if (!breakupEligible && ForceVehicleBreakup.NeedsBreakup(source)) breakupEligible = true;
            }
        foreach (var held in _growthAdditions)
            if (held.Source is { } source)
            {
                sourceCount++;
                if (!breakupEligible && ForceVehicleBreakup.NeedsBreakup(source)) breakupEligible = true;
            }

        // Prepare the immutable worker publication before the first write, but publish it only after
        // every addition has accepted its current rigid state.
        var allSources = new Vehicle[sourceCount];
        int sourceIndex = 0;
        foreach (var held in _held) if (held.Source is { } source) allSources[sourceIndex++] = source;
        foreach (var held in _growthAdditions) if (held.Source is { } source) allSources[sourceIndex++] = source;

        foreach (var held in _growthAdditions)
        {
            if (!ForceMath.TryFollow(ownerPosition, ownerVelocity, ownerRotation, ownerRates,
                _groupCenterBody, held.PositionBody, held.SourceToOwner, GroupOffsetBody, GroupSpread, out var state))
                throw new InvalidOperationException("A new Force object could not follow the held group.");
            if (held.Source is { } source)
            {
                if (!ForceTargeting.IsLive(source) || !ReferenceEquals(source.Parent, held.Parent)
                    || !ReferenceEquals(source.Parts, held.Tree))
                    throw new InvalidOperationException("A new vessel changed before its Force state could commit.");
                held.GrowthMutationStarted = true;
                WriteState(source, state, time);
            }
            else if (held.ClutterHold is { } hold)
            {
                held.GrowthMutationStarted = true;
                hold.Write(state);
            }
            else throw new InvalidOperationException("A new ground object was not promoted for its Force hold.");
        }

        string committedStatus = $"Holding {resultingCount} objects.";
        _held.AddRange(_growthAdditions);
        ForcePhysicsPatches.Publish(allSources);
        _hasFrame = false;
        _breakupEligible = breakupEligible;
        SetStatus(committedStatus, ForceStatusSeverity.None);
    }

    private void RollbackGrowthAdditions()
    {
        try { JoinOrbitReaders(); }
        catch (Exception error) { Console.WriteLine($"[purr2d2] Force growth rollback could not join readers: {error}"); }
        foreach (var held in _growthAdditions)
        {
            try
            {
                if (held.GrowthMutationStarted && held.Source is { } source && ForceTargeting.IsLive(source))
                    WriteState(source, held.Original, Universe.GetLastSimStep().NextTime);
                if (held.ClutterHold is { } hold)
                {
                    if (!hold.Release(held.Original)) hold.Dispose();
                }
            }
            catch (Exception error) { Console.WriteLine($"[purr2d2] Force growth rollback failed for {held.DisplayName}: {error}"); }
            finally
            {
                try { held.ClutterHold?.Dispose(); }
                catch (Exception error) { Console.WriteLine($"[purr2d2] Force growth cleanup failed for {held.DisplayName}: {error}"); }
            }
        }
    }
}
