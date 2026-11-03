using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;

namespace Purr2D2.Slicing.Detection;

internal readonly record struct ClutterRayContact(ClutterForceTarget Target, double3 PointCci,
    double3 BladeCci, double3 MotionCci, double HitTime01, double AlongBlade01);

internal readonly record struct ClutterRayProgress(bool Complete, bool CoverageUnavailable,
    int RayInvocations, int UnprocessedRays, int CandidateOverflow, int UnresolvedOverlaps, string Reason);

/// <summary>Settled-thread sampled-ray comparison detector. It owns reusable scratch storage;
/// the caller owns and clears the returned contact list and controls admission.</summary>
internal sealed class AllHitClutterDetector
{
    private const int MaxRays = 256, MaxCandidates = 512;
    private readonly List<ClutterForceTargets.AllRayHit> _hits = new(MaxCandidates);
    private readonly List<ClutterForceTargets.AllRayHit> _reverse = new(MaxCandidates);
    private readonly Dictionary<ClutterForceIdentity, int> _indices = new(MaxCandidates);
    private int _rotation;

    // Endpoints are measured in CCF; ccf2Cci is the rotation for this settled
    // sample. The caller supplies fresh, guarded samples and authorization.
    internal ClutterRayProgress Detect(KittenEva owner, double3 previousBase, double3 previousTip,
        double3 currentBase, double3 currentTip, doubleQuat ccf2Cci,
        HashSet<ClutterForceIdentity>? pending, List<ClutterRayContact> output)
    {
        long started = SliceDetectionDiagnostics.Start();
        var progress = DetectCore(owner, previousBase, previousTip, currentBase, currentTip, ccf2Cci, pending, output);
        SliceDetectionDiagnostics.Record(SliceDetectorKind.AllHitRays, started, output.Count, !progress.Complete,
            progress.UnprocessedRays, progress.CandidateOverflow, progress.CoverageUnavailable, progress.UnresolvedOverlaps);
        return progress;
    }

    private ClutterRayProgress DetectCore(KittenEva owner, double3 previousBase, double3 previousTip,
        double3 currentBase, double3 currentTip, doubleQuat ccf2Cci,
        HashSet<ClutterForceIdentity>? pending, List<ClutterRayContact> output)
    {
        output.Clear(); _indices.Clear();
        if (!ClutterForceTargets.TryAllHitsContext(owner, out var bubble, out string reason) || bubble == null)
            return new(false, true, 0, 0, 0, 0, reason);
        double length0 = (previousTip - previousBase).Length(), length1 = (currentTip - currentBase).Length();
        if (!double.IsFinite(length0) || !double.IsFinite(length1) || length0 < .001 || length1 < .001)
            return new(false, false, 0, 0, 0, 0, "Invalid blade segment.");
        int n = (int)Math.Clamp(Math.Ceiling(Math.Max(length0, length1) / .25), 8, 256);
        int paths = n + 3, rays = 0, processed = 0, overflow = 0, unresolved = 0;
        bool incomplete = false;
        long start = Stopwatch.GetTimestamp();
        bool Budget() => rays < MaxRays && Stopwatch.GetElapsedTime(start).TotalMilliseconds < .5;
        for (int step = 0; step < paths; step++)
        {
            if (!Budget()) { incomplete = true; break; }
            int index = (_rotation + step) % paths;
            bool bladePath = index < 2;
            double s = bladePath ? 0 : (index - 2) / (double)n;
            double3 from = index == 0 ? previousBase : index == 1 ? currentBase : previousBase + (previousTip - previousBase) * s;
            double3 to = index == 0 ? previousTip : index == 1 ? currentTip : currentBase + (currentTip - currentBase) * s;
            double distance = (to - from).Length();
            processed++;
            if (distance < .001) continue;
            rays++;
            if (!ClutterForceTargets.CastAllHits(bubble, from, to, _hits, null, out bool exceeded, out bool full))
            { incomplete = true; continue; }
            if (exceeded || full) incomplete = true;
            if (full) overflow++;
            foreach (var hit in _hits)
            {
                if (!ClutterForceTargets.TryAllHitsIdentity(bubble, hit.Collidable, out var identity))
                { incomplete = true; continue; }
                if (pending?.Contains(identity) == true) continue;
                double hitDistance = hit.Distance;
                if (hitDistance <= .000001)
                {
                    // Inside-origin callbacks are not boundary points. Reverse
                    // only against the same collider, and require a positive hit.
                    if (!Budget()) { unresolved++; incomplete = true; continue; }
                    rays++;
                    bool valid = ClutterForceTargets.CastAllHits(bubble, to, from, _reverse, hit.Collidable,
                        out bool reverseExceeded, out bool reverseFull);
                    if (reverseExceeded || reverseFull) incomplete = true;
                    if (!valid || _reverse.Count == 0 || _reverse[0].Distance <= .000001)
                    { unresolved++; incomplete = true; continue; }
                    hitDistance = distance - _reverse[0].Distance;
                }
                if (hitDistance < 0 || hitDistance > distance) { incomplete = true; continue; }
                double fraction = Math.Clamp(hitDistance / distance, 0, 1);
                double time = bladePath ? index : fraction;
                double along = bladePath ? fraction : s;
                var blade = (previousTip - previousBase) * (1 - time) + (currentTip - currentBase) * time;
                var motion = (currentBase - previousBase) * (1 - along) + (currentTip - previousTip) * along;
                var contact = from + (to - from) * fraction;
                bool known = _indices.TryGetValue(identity, out int old);
                ClutterForceTarget? target = known ? output[old].Target : null;
                if (!known && (!ClutterForceTargets.TryAllHitsTarget(bubble, owner.System, hit.Collidable, out target) || target == null))
                { incomplete = true; continue; }
                if (target == null) { incomplete = true; continue; }
                var candidate = new ClutterRayContact(target, contact.Transform(ccf2Cci), blade.Transform(ccf2Cci),
                    motion.Transform(ccf2Cci), time, along);
                if (known)
                {
                    if (Compare(candidate, output[old]) < 0) output[old] = candidate;
                }
                else if (output.Count < MaxCandidates)
                { _indices.Add(identity, output.Count); output.Add(candidate); }
                else { overflow++; incomplete = true; }
            }
        }
        _rotation = (_rotation + Math.Max(1, processed)) % paths;
        output.Sort(Compare);
        return new(!incomplete && processed == paths, false, rays, paths - processed, overflow, unresolved, "");
    }

    internal void Reset() { _rotation = 0; _hits.Clear(); _reverse.Clear(); _indices.Clear(); }
    private static int Compare(ClutterRayContact a, ClutterRayContact b)
    {
        int order = a.HitTime01.CompareTo(b.HitTime01);
        if (order == 0) order = a.AlongBlade01.CompareTo(b.AlongBlade01);
        return order != 0 ? order : StringComparer.Ordinal.Compare(a.Target.StableOrder, b.Target.StableOrder);
    }
}
