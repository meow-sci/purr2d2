using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Cutting;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Slicing.Detection;

internal readonly record struct ClutterCapsuleContact(ClutterForceTarget Target, double3 PointCci,
    double3 BladeCci, double3 MotionCci, double HitTime01, double AlongBlade01, bool AnchorRefined)
{
    internal SliceSweepSamples SamplesCci { get; init; }
    internal ulong Stroke { get; init; }
    internal double DetectionTime { get; init; }
}
internal readonly record struct ClutterCapsuleProgress(bool Complete, bool CoverageUnavailable, int Rays,
    int Pending, int Overflow, int Expired, int Stale, int Unresolved, int Unconfirmed, bool UnsupportedShape, string Reason);

/// <summary>A settled-thread-only experimental detector. Provisional contacts store target-local
/// motion and shape coordinates, so subsequent confirmation never repeats a historical scene sweep.</summary>
internal sealed class CapsuleClutterDetector : IDisposable
{
    private const int Limit = 512;
    private const float Radius = .01f;
    private readonly BufferPool _pool = new();
    private readonly List<ClutterForceTargets.CapsuleHit> _hits = new(Limit);
    private readonly List<ClutterForceTargets.AllRayHit> _rayHits = new(Limit);
    private readonly List<Candidate> _pending = new(Limit);
    private readonly HashSet<ClutterForceIdentity> _identities = new(Limit);

    private sealed record Candidate(ClutterForceTarget Target, double Created, Vector3 Center,
        Vector3 Axis, Vector3 Delta, Vector3 Omega, float Length, float Time,
        Vector3 Location, Vector3 Normal, bool Overlap, ulong Stroke);

    // Pass submitInterval=false after release to finish only already-observed
    // provisional contacts. The caller resets on scene/method invalidation.
    internal ClutterCapsuleProgress Detect(KittenEva owner, double3 previousBase, double3 previousTip,
        double3 currentBase, double3 currentTip, doubleQuat ccf2Cci, double now, bool submitInterval,
        HashSet<ClutterForceIdentity>? excluded, List<ClutterCapsuleContact> output,
        long budgetStarted = 0, int maximumRays = 256, ulong stroke = 0)
    {
        long started = SliceDetectionDiagnostics.Start();
        var progress = DetectCore(owner, previousBase, previousTip, currentBase, currentTip, ccf2Cci, now,
            submitInterval, excluded, output, budgetStarted, maximumRays, stroke);
        SliceDetectionDiagnostics.Record(SliceDetectorKind.CapsuleSweep, started, output.Count, !progress.Complete,
            overflow: progress.Overflow, coverageUnavailable: progress.CoverageUnavailable,
            unresolved: progress.Unresolved + progress.Unconfirmed);
        return progress;
    }

    private ClutterCapsuleProgress DetectCore(KittenEva owner, double3 previousBase, double3 previousTip,
        double3 currentBase, double3 currentTip, doubleQuat ccf2Cci, double now, bool submitInterval,
        HashSet<ClutterForceIdentity>? excluded, List<ClutterCapsuleContact> output,
        long budgetStarted, int maximumRays, ulong stroke)
    {
        output.Clear();
        if (!ClutterForceTargets.TryAllHitsContext(owner, out var bubble, out string reason) || bubble == null)
            return new(false, true, 0, _pending.Count, 0, 0, 0, 0, 0, false, reason);
        if (!double.IsFinite(now)) return new(false, false, 0, _pending.Count, 0, 0, 0, 0, 0, false, "Invalid sample time.");
        int rays = 0, overflow = 0, expired = 0, stale = 0, unresolved = 0, unconfirmed = 0;
        bool incomplete = false, unsupported = false, intervalBudgetLimited = false;
        long started = budgetStarted == 0 ? Stopwatch.GetTimestamp() : budgetStarted;
        int rayLimit = Math.Clamp(maximumRays, 0, 256);
        bool Budget() => rays < rayLimit && Stopwatch.GetElapsedTime(started).TotalMilliseconds < .5;

        // Finish only the number present at entry; new hits are frozen below.
        for (int i = 0; i < _pending.Count;)
        {
            var candidate = _pending[i];
            if (ProvisionalExpired(now, candidate.Created))
            { Remove(i); expired++; incomplete = true; continue; }
            if (excluded?.Contains(candidate.Target.StableKey) == true)
            { Remove(i); continue; }
            if (!ReferenceEquals(candidate.Target.Bubble, bubble) || !candidate.Target.IsLocalTo(owner)
                || !candidate.Target.TryResolve(out var targetPose, out _, out var stat, out var body))
            { Remove(i); stale++; incomplete = true; continue; }
            if (!Budget()) { incomplete = true; break; }
            CollidableReference collidable = stat.HasValue ? new(stat.Value)
                : bubble.ConstraintSim!.Simulation.Bodies[body!.Value].CollidableReference;
            var result = Confirm(candidate, bubble, targetPose, collidable, ccf2Cci, Budget, ref rays, out var contact);
            if (result == Confirmation.Pending) { incomplete = true; break; }
            Remove(i);
            if (result == Confirmation.Confirmed) output.Add(contact);
            else if (candidate.Overlap) { unresolved++; incomplete = true; }
            else { unconfirmed++; incomplete = true; }
        }

        if (submitInterval && Budget())
        {
            var blade0 = previousTip - previousBase; var blade1 = currentTip - currentBase;
            double l0 = blade0.Length(), l1 = blade1.Length();
            if (!double.IsFinite(l0) || !double.IsFinite(l1) || l0 < .001 || Math.Abs(l1 - l0) > .001
                || l0 > float.MaxValue)
                return new(false, false, rays, _pending.Count, overflow, expired, stale, unresolved, unconfirmed, false, "Invalid or changing blade length.");
            var axis0 = (blade0 / l0).ToBepu(); var axis1 = (blade1 / l1).ToBepu();
            if (Vector3.Dot(axis0, axis1) < -.9999f)
                return new(false, false, rays, _pending.Count, overflow, expired, stale, unresolved, unconfirmed, false, "Blade rotation is too large.");
            BepuUtilities.QuaternionEx.GetQuaternionBetweenNormalizedVectors(axis0, axis1, out var turn);
            BepuUtilities.QuaternionEx.GetAxisAngleFromQuaternion(turn, out var rotationAxis, out float angle);
            var omega = rotationAxis * angle;
            var center = (previousBase + previousTip) * .5;
            var delta = ((currentBase + currentTip) * .5 - center).ToBepu();
            bool valid = ClutterForceTargets.SweepCapsule(bubble, center, axis0, delta, omega, (float)l0,
                _pool, _hits, out bool exceeded, out unsupported, out bool full);
            if (!valid || exceeded || unsupported || full) incomplete = true;
            if (full) overflow++;
            foreach (var hit in _hits)
            {
                if (!ClutterForceTargets.TryAllHitsIdentity(bubble, hit.Collidable, out var key))
                { incomplete = true; continue; }
                if (_identities.Contains(key) || excluded?.Contains(key) == true) continue;
                if (_pending.Count >= Limit) { overflow++; incomplete = true; continue; }
                if (!ClutterForceTargets.TryAllHitsTarget(bubble, owner.System, hit.Collidable, out var target)
                    || target == null || !target.TryResolve(out var pose, out _, out _, out _))
                { stale++; incomplete = true; continue; }
                var inverse = Quaternion.Conjugate(pose.Orientation);
                var centerLocal = Vector3.Transform((center - ClutterForceNative.Origin(bubble.GroundClutterStatics)).ToBepu() - pose.Position, inverse);
                var localAxis = Vector3.Transform(axis0, inverse); var localDelta = Vector3.Transform(delta, inverse);
                var localOmega = Vector3.Transform(omega, inverse);
                var location = Vector3.Transform(hit.Location - pose.Position, inverse);
                var normal = Vector3.Transform(hit.Normal, inverse);
                _pending.Add(new(target, now, centerLocal, localAxis, localDelta, localOmega,
                    (float)l0, hit.Time, location, normal, hit.InitialOverlap, stroke));
                _identities.Add(key);
            }
            _pending.Sort((a, b) =>
            {
                int order = a.Created.CompareTo(b.Created);
                if (order == 0) order = a.Time.CompareTo(b.Time);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.Target.StableOrder, b.Target.StableOrder);
            });
        }
        else if (submitInterval) { incomplete = true; intervalBudgetLimited = true; }
        // New hits are confirmed at the next settled handoff; never query a
        // second time recursively from inside a native sweep callback.
        output.Sort((a, b) => { int c = a.HitTime01.CompareTo(b.HitTime01); if (c == 0) c = a.AlongBlade01.CompareTo(b.AlongBlade01); return c != 0 ? c : StringComparer.Ordinal.Compare(a.Target.StableOrder, b.Target.StableOrder); });
        return new(!incomplete && _pending.Count == 0, false, rays, _pending.Count, overflow, expired, stale,
            unresolved, unconfirmed, unsupported, intervalBudgetLimited ? "Frame budget exhausted before sweep." : "");
    }

    private enum Confirmation { Confirmed, Rejected, Pending }
    private Confirmation Confirm(Candidate c, PhysicsBubble bubble, RigidPose pose, CollidableReference collidable,
        doubleQuat ccf2Cci, Func<bool> budget, ref int rays, out ClutterCapsuleContact result)
    {
        result = default;
        float t = c.Time;
        Vector3 axis = Axis(c, t), center = c.Center + c.Delta * t;
        Vector3 best = default; float bestDistance = float.PositiveInfinity; bool found = false;
        if (c.Overlap)
        {
            for (int end = 0; end <= 1 && !found; end++)
            {
                t = end; axis = Axis(c, t); center = c.Center + c.Delta * t;
                Vector3 a = center - axis * (c.Length * .5f), b = center + axis * (c.Length * .5f);
                for (int reverse = 0; reverse < 2 && !found; reverse++)
                {
                    if (!budget()) return Confirmation.Pending;
                    rays++;
                    if (Ray(bubble, pose, collidable, reverse == 0 ? a : b, reverse == 0 ? b : a, out var p))
                    { best = p; found = true; }
                }
            }
        }
        else
        {
            if (!Finite(c.Normal) || c.Normal.LengthSquared() < 1e-12f || !Finite(c.Location)) return Confirmation.Rejected;
            var n = Vector3.Normalize(c.Normal); const float half = 2 * Radius + .005f;
            for (int reverse = 0; reverse < 2; reverse++)
            {
                if (!budget()) return Confirmation.Pending;
                rays++;
                var a = c.Location + n * (reverse == 0 ? -half : half);
                var b = c.Location + n * (reverse == 0 ? half : -half);
                if (Ray(bubble, pose, collidable, a, b, out var p))
                {
                    float d = Vector3.DistanceSquared(p, c.Location);
                    if (d < bestDistance) { best = p; bestDistance = d; found = true; }
                }
            }
        }
        if (!found) return Confirmation.Rejected;
        if (!TryBladeProjection(best, center, axis, c.Length, out float along)) return Confirmation.Rejected;
        var motion = c.Delta + Vector3.Cross(c.Omega, axis * ((along - .5f) * c.Length));
        var origin = ClutterForceNative.Origin(bubble.GroundClutterStatics);
        var pointCcf = origin + Double3Ex.FromBepu(pose.Position + Vector3.Transform(best, pose.Orientation));
        var bladeCcf = Double3Ex.FromBepu(Vector3.Transform(axis * c.Length, pose.Orientation));
        var motionCcf = Double3Ex.FromBepu(Vector3.Transform(motion, pose.Orientation));
        CutPoint Sample(float time, float fraction)
        {
            var local = c.Center + c.Delta * time + Axis(c, time) * ((fraction - .5f) * c.Length);
            var world = (origin + Double3Ex.FromBepu(pose.Position + Vector3.Transform(local, pose.Orientation))).Transform(ccf2Cci);
            return new(world.X, world.Y, world.Z);
        }
        var samples = new SliceSweepSamples(Sample(0, 0), Sample(0, along), Sample(0, 1),
            Sample(t, 0), Sample(t, along), Sample(t, 1), Sample(1, 0), Sample(1, along), Sample(1, 1));
        result = new(c.Target, pointCcf.Transform(ccf2Cci), bladeCcf.Transform(ccf2Cci),
            motionCcf.Transform(ccf2Cci), t, along, true) { SamplesCci = samples, Stroke = c.Stroke, DetectionTime = c.Created };
        return Confirmation.Confirmed;
    }

    private bool Ray(PhysicsBubble bubble, RigidPose pose, CollidableReference collidable,
        Vector3 from, Vector3 to, out Vector3 localHit)
    {
        localHit = default;
        var origin = ClutterForceNative.Origin(bubble.GroundClutterStatics);
        var a = origin + Double3Ex.FromBepu(pose.Position + Vector3.Transform(from, pose.Orientation));
        var b = origin + Double3Ex.FromBepu(pose.Position + Vector3.Transform(to, pose.Orientation));
        if (!ClutterForceTargets.CastAllHits(bubble, a, b, _rayHits, collidable, out bool exceeded, out bool overflow)
            || exceeded || overflow || _rayHits.Count == 0) return false;
        double length = (b - a).Length();
        if (!PositiveBoundary(_rayHits[0].Distance, length)) return false;
        var p = a + (b - a) * (_rayHits[0].Distance / length);
        localHit = Vector3.Transform((p - origin).ToBepu() - pose.Position, Quaternion.Conjugate(pose.Orientation));
        return Finite(localHit);
    }
    private static Vector3 Axis(Candidate c, float t)
    {
        float angle = c.Omega.Length();
        return angle < 1e-7f ? c.Axis : Vector3.Transform(c.Axis, Quaternion.CreateFromAxisAngle(c.Omega / angle, angle * t));
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool ProvisionalExpired(double now, double created)
        => !double.IsFinite(now) || !double.IsFinite(created) || now < created || now - created > 1;
    internal static bool PositiveBoundary(double distance, double length)
        => double.IsFinite(distance) && double.IsFinite(length) && distance > .000001 && distance <= length;
    internal static bool TryBladeProjection(Vector3 point, Vector3 center, Vector3 axis, float length, out float along)
    {
        along = 0;
        if (!Finite(point) || !Finite(center) || !Finite(axis) || !float.IsFinite(length) || length <= 0) return false;
        along = Math.Clamp(Vector3.Dot(point - (center - axis * (length * .5f)), axis) / length, 0, 1);
        var onBlade = center + axis * ((along - .5f) * length);
        return Vector3.DistanceSquared(point, onBlade) <= (Radius + .0025f) * (Radius + .0025f);
    }
    private void Remove(int index) { _identities.Remove(_pending[index].Target.StableKey); _pending.RemoveAt(index); }
    internal bool HasPending => _pending.Count != 0;
    internal void Reset() { _hits.Clear(); _rayHits.Clear(); _pending.Clear(); _identities.Clear(); }
    public void Dispose() { Reset(); _pool.Clear(); }
}
