using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private const int MaximumClashCandidates = 128;
    private const double ClashContactRadius = .085;
    private const double ClashReleaseRadius = .22;
    private const double ClashSeparationCooldown = .18;
    private static readonly double ClashContactRadiusSquared = ClashContactRadius * ClashContactRadius;
    private static readonly double ClashReleaseRadiusSquared = ClashReleaseRadius * ClashReleaseRadius;
    private readonly ClashCandidate[] _clashCandidates = new ClashCandidate[MaximumClashCandidates];
    private readonly SaberClashLatchTable<KittenEva> _clashPairs = new();
    private int _clashCandidateCount;
    private double _clashTime;

    private readonly record struct ClashCandidate(KittenEva Owner, KittenSaber Saber, string Id, IParentBody Parent,
        double3 PositionCce, double3 PositionEcl, SaberClashMath.Segment Segment, double MaxReach, float VisibleLength);

    /// <summary>Advances only on supported player time, then tests the last rendered sample before animation advances.</summary>
    private void UpdateSaberClashes(double supportedPlayerDt)
    {
        bool canDetect = double.IsFinite(supportedPlayerDt) && supportedPlayerDt > 0;
        if (canDetect)
            _clashTime += Math.Min(supportedPlayerDt, .1);
        if (!double.IsFinite(_clashTime) || _clashTime < 0) _clashTime = 0;

        foreach (var pair in _kittenSabers)
        {
            var owner = pair.Key;
            var saber = pair.Value;
            if (owner.IsDisposed)
            {
                RemoveClashOwner(owner);
                continue;
            }
            saber.Pose.UpdateClash(_clashTime);
            if (!saber.Drop.Held || !saber.Blade.Equipped || saber.Blade.Deployment <= .001f
                || !saber.Pose.Eligible)
                saber.Pose.ClearClash();
            if (!saber.Drop.Held || !saber.Blade.Equipped || saber.Blade.Deployment <= .001f)
                _clashPairs.Remove(owner);
        }

        if (!canDetect)
        {
            Array.Clear(_clashCandidates, 0, _clashCandidateCount);
            _clashCandidateCount = 0;
            return;
        }
        Array.Clear(_clashCandidates, 0, _clashCandidateCount);
        GatherClashCandidates();
        DetectClashes();
    }

    private void GatherClashCandidates()
    {
        _clashCandidateCount = 0;
        foreach (var pair in _kittenSabers)
        {
            KittenEva owner = pair.Key;
            KittenSaber saber = pair.Value;
            if (!saber.Drop.Held || owner.IsDisposed || !saber.Blade.Equipped || !saber.Pose.Eligible
                || !saber.Pose.HasFreshClashSnapshot) continue;
            float length = VisibleBladeLength(saber);
            if (!float.IsFinite(length) || length <= .001f
                || !saber.Pose.TryGetBladeSegment(length, out var baseBody, out var tipBody)) continue;
            double3 baseOffset = baseBody.Transform(owner.Body2Cce);
            double3 tipOffset = tipBody.Transform(owner.Body2Cce);
            SaberClashMath.Segment segment = new(
                baseOffset, tipOffset);
            if (!SaberClashMath.IsFinite(segment)) continue;
            double maxReach = Math.Max(segment.Start.Length(), segment.End.Length());
            if (!double.IsFinite(maxReach)) continue;
            var candidate = new ClashCandidate(owner, saber, owner.Id, owner.Parent,
                owner.GetPositionCce(), owner.GetPositionEcl(), segment, maxReach, length);
            InsertClashCandidateById(candidate);
        }
    }

    // Keep the lowest 128 stable kitten ids if the fixed candidate set overflows.
    private void InsertClashCandidateById(ClashCandidate candidate)
    {
        int index = 0;
        while (index < _clashCandidateCount
            && string.CompareOrdinal(_clashCandidates[index].Id, candidate.Id) <= 0) index++;
        if (_clashCandidateCount == _clashCandidates.Length)
        {
            if (index >= _clashCandidateCount) return;
            for (int i = _clashCandidateCount - 1; i > index; i--) _clashCandidates[i] = _clashCandidates[i - 1];
            _clashCandidates[index] = candidate;
            return;
        }
        for (int i = _clashCandidateCount; i > index; i--) _clashCandidates[i] = _clashCandidates[i - 1];
        _clashCandidates[index] = candidate;
        _clashCandidateCount++;
    }

    private void DetectClashes()
    {
        ObserveLatchedPairs();
        for (int i = 0; i < _clashCandidateCount; i++)
        {
            ref readonly ClashCandidate a = ref _clashCandidates[i];
            if (a.Saber.Pose.ClashHeld) continue;
            for (int j = i + 1; j < _clashCandidateCount; j++)
            {
                ref readonly ClashCandidate b = ref _clashCandidates[j];
                if (b.Saber.Pose.ClashHeld || ReferenceEquals(a.Owner, b.Owner)) continue;
                if (!TryRelativeSegments(a, b, out var segmentA, out var segmentB, out double centerDistanceSquared)) continue;
                double maxRange = a.MaxReach + b.MaxReach + ClashContactRadius;
                if (centerDistanceSquared > maxRange * maxRange
                    || SaberClashMath.AabbDistanceSquared(segmentA, segmentB) > ClashContactRadiusSquared
                    || _clashPairs.IsLatched(a.Owner, b.Owner)) continue;
                if (!SaberClashMath.TryClosest(segmentA, segmentB, out var closest)) continue;
                if (closest.DistanceSquared <= ClashContactRadiusSquared)
                {
                    BeginSaberClash(a, b, closest);
                    if (a.Saber.Pose.ClashHeld) break;
                }
            }
        }
    }

    private void ObserveLatchedPairs()
    {
        if (_clashPairs.Count == 0) return;
        for (int pairIndex = 0; pairIndex < SaberClashLatchTable<KittenEva>.MaximumPairs; pairIndex++)
        {
            if (!_clashPairs.TryGetAt(pairIndex, out var ownerA, out var ownerB)) continue;
            int indexA = FindClashCandidate(ownerA!);
            int indexB = FindClashCandidate(ownerB!);
            // Unknown samples break release continuity, even for a short gap.
            if (indexA < 0 || indexB < 0)
            {
                _clashPairs.MarkUnknown(ownerA!, ownerB!, _clashTime);
                continue;
            }
            ref readonly ClashCandidate a = ref _clashCandidates[indexA];
            ref readonly ClashCandidate b = ref _clashCandidates[indexB];
            if (!TryRelativeSegments(a, b, out var segmentA, out var segmentB, out double centerDistanceSquared))
            {
                _clashPairs.MarkUnknown(ownerA!, ownerB!, _clashTime);
                continue;
            }
            double maxRange = a.MaxReach + b.MaxReach + ClashReleaseRadius;
            if (!double.IsFinite(maxRange))
            {
                _clashPairs.MarkUnknown(ownerA!, ownerB!, _clashTime);
                continue;
            }
            if (centerDistanceSquared > maxRange * maxRange)
            {
                _clashPairs.ObserveSeparated(ownerA!, ownerB!, _clashTime, ClashSeparationCooldown);
                continue;
            }
            double aabbDistanceSquared = SaberClashMath.AabbDistanceSquared(segmentA, segmentB);
            if (!double.IsFinite(aabbDistanceSquared))
            {
                _clashPairs.MarkUnknown(ownerA!, ownerB!, _clashTime);
                continue;
            }
            if (aabbDistanceSquared > ClashReleaseRadiusSquared)
            {
                _clashPairs.ObserveSeparated(ownerA!, ownerB!, _clashTime, ClashSeparationCooldown);
                continue;
            }
            if (SaberClashMath.TryClosest(segmentA, segmentB, out var closest))
                _clashPairs.Observe(ownerA!, ownerB!, closest.DistanceSquared,
                    ClashReleaseRadiusSquared, _clashTime, ClashSeparationCooldown);
            else _clashPairs.MarkUnknown(ownerA!, ownerB!, _clashTime);
        }
    }

    private int FindClashCandidate(KittenEva owner)
    {
        for (int i = 0; i < _clashCandidateCount; i++)
            if (ReferenceEquals(_clashCandidates[i].Owner, owner)) return i;
        return -1;
    }

    private static bool TryRelativeSegments(in ClashCandidate a, in ClashCandidate b,
        out SaberClashMath.Segment segmentA, out SaberClashMath.Segment segmentB, out double centerDistanceSquared)
    {
        segmentA = segmentB = default;
        double3 relativeCenter = ReferenceEquals(a.Parent, b.Parent)
            ? b.PositionCce - a.PositionCce : b.PositionEcl - a.PositionEcl;
        centerDistanceSquared = relativeCenter.LengthSquared();
        if (!Finite(relativeCenter) || !double.IsFinite(centerDistanceSquared)) return false;
        segmentA = a.Segment;
        segmentB = new(relativeCenter + b.Segment.Start, relativeCenter + b.Segment.End);
        return SaberClashMath.IsFinite(segmentA) && SaberClashMath.IsFinite(segmentB);
    }

    private void BeginSaberClash(in ClashCandidate a, in ClashCandidate b, SaberClashMath.Closest closest)
    {
        // Relative segment coordinates are centered on A; convert the contact offset back to its body frame.
        double3 contactOffsetCce = closest.OnA + (closest.OnB - closest.OnA) * .5;
        double3 pointBody = contactOffsetCce.Transform(doubleQuat.Inverse(a.Owner.Body2Cce));
        if (!Finite(pointBody)) return;
        if (!_clashPairs.CanLatch(a.Owner, b.Owner)
            || !a.Saber.Pose.CanBeginClash(_clashTime) || !b.Saber.Pose.CanBeginClash(_clashTime)) return;
        // All checks are complete before either pose is changed; callbacks run on the settled main thread.
        if (!a.Saber.Pose.TryBeginClash(_clashTime, a.VisibleLength)) return;
        if (!b.Saber.Pose.TryBeginClash(_clashTime, b.VisibleLength))
        {
            a.Saber.Pose.ClearClash();
            return;
        }
        if (!_clashPairs.TryLatch(a.Owner, b.Owner, _clashTime))
        {
            a.Saber.Pose.ClearClash();
            b.Saber.Pose.ClearClash();
            return;
        }

        _renderer.AddClashSparks(a.Owner, pointBody, _playerTime);
        // One one-shot per latched pair; the other saber's audio channel remains independent.
        a.Saber.Audio.PlayClash(a.Owner, pointBody, _settings.Volume);
    }

    internal void RemoveClashOwner(KittenEva owner)
    {
        if (owner is null) return;
        if (_kittenSabers.TryGetValue(owner, out var saber)) saber.Pose.ClearClash();
        _clashPairs.Remove(owner);
        int write = 0;
        for (int read = 0; read < _clashCandidateCount; read++)
        {
            if (ReferenceEquals(_clashCandidates[read].Owner, owner)) continue;
            if (write != read) _clashCandidates[write] = _clashCandidates[read];
            write++;
        }
        Array.Clear(_clashCandidates, write, _clashCandidateCount - write);
        _clashCandidateCount = write;
    }

    private void ClearSaberClashes()
    {
        foreach (var pair in _kittenSabers) pair.Value.Pose.ClearClash();
        _clashPairs.Clear();
        Array.Clear(_clashCandidates);
        _clashCandidateCount = 0;
        _clashTime = 0;
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
