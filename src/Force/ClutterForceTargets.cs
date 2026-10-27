using BepuPhysics;
using BepuPhysics.Collidables;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal readonly record struct ClutterForceIdentity(BubbleClutterStatics Native, int Generation,
    BubbleClutterStatics.ClutterInstanceKey Key);

internal readonly record struct ClutterForceCandidate(ClutterForceTarget Target, double DistanceSquared);

/// <summary>One resident native object, identified independently of recycled Bepu handles.</summary>
internal sealed class ClutterForceTarget
{
    internal readonly PhysicsBubble Bubble;
    internal readonly BubbleClutterStatics Native;
    internal readonly ConstraintSim Sim;
    internal readonly BubbleClutterStatics.ClutterInstanceKey Key;
    internal readonly TypedIndex Shape;
    private readonly StaticHandle? _static;
    private readonly BodyHandle? _body;
    private readonly int _generation;
    private readonly string _stableOrder;
    internal IParentBody Parent { get; }
    internal CelestialSystem System { get; }
    internal ClutterForceIdentity StableKey => new(Native, _generation, Key);
    internal string StableOrder => _stableOrder;
    internal string DisplayName { get; }
    internal bool IsValid => TryResolve(out _, out _, out _, out _);

    internal ClutterForceTarget(PhysicsBubble bubble, CelestialSystem system,
        BubbleClutterStatics.ClutterInstanceKey key, TypedIndex shape, StaticHandle? staticHandle, BodyHandle? bodyHandle)
    {
        Bubble = bubble; Native = bubble.GroundClutterStatics; Sim = bubble.ConstraintSim!;
        Parent = bubble.Parent; System = system; Key = key; Shape = shape; _static = staticHandle; _body = bodyHandle;
        _generation = ClutterForceNative.Generation(Native);
        _stableOrder = $"{Key.CelestialHash.Code:D10}:{Key.EcotypeIndex:D10}:{Key.Cell.FaceId:D2}:"
            + $"{Key.Cell.X:D10}:{Key.Cell.Y:D10}:{Key.SubCellId:D10}:{_generation:D10}";
        DisplayName = ClutterForceNative.Name(Native, in key);
    }

    internal bool IsLocalTo(KittenEva owner) => ForceTargeting.IsLive(owner)
        && ReferenceEquals(owner.Parent, Parent) && ReferenceEquals(owner.System, System)
        && ReferenceEquals(ClutterForceNative.OwnerBubble(owner), Bubble);

    internal bool MatchesStatic(StaticHandle handle) => _static == handle;
    internal bool NativeAlive => ClutterForceNative.Generation(Native) == _generation
        && ReferenceEquals(Bubble.ConstraintSim, Sim) && ReferenceEquals(Bubble.GroundClutterStatics, Native)
        && ReferenceEquals(Bubble.Parent, Parent) && Key.CelestialHash == Parent.Hash;
    internal bool FrameValid => NativeAlive && ReferenceEquals(Universe.CurrentSystem, System)
        && Bubble.Origin.BubFrame == BubbleFrame.Ccf && Parent is Celestial parent
        && ClutterForceNative.Describes(Native, parent);

    internal bool TryResolve(out RigidPose pose, out BodyVelocity velocity, out StaticHandle? staticHandle, out BodyHandle? bodyHandle)
    {
        pose = default; velocity = default; staticHandle = null; bodyHandle = null;
        if (!FrameValid) return false;
        if (TryResolveBody(out var body))
        { pose = body.Pose; velocity = body.Velocity; bodyHandle = body.Handle; return true; }
        // A target originally picked as dynamic must never silently become a newly loaded static.
        if (_body.HasValue || !_static.HasValue || !Sim.Simulation.Statics.StaticExists(_static.Value)
            || !ClutterForceNative.TryStatic(Native, _static.Value, out var key, out _) || !key.Equals(Key)) return false;
        var source = Sim.Simulation.Statics[_static.Value];
        if (source.Shape != Shape) return false;
        pose = source.Pose; staticHandle = _static.Value; return true;
    }

    internal bool TryResolveBody(out BodyReference body)
    {
        body = default;
        if (!NativeAlive || !ClutterForceNative.TryBody(Native, in Key, out var handle)
            || (_body.HasValue && _body.Value != handle) || !Sim.Simulation.Bodies.BodyExists(handle)
            || !ClutterForceNative.TryDynamic(Native, handle, out var key, out var shape)
            || !key.Equals(Key) || shape != Shape) return false;
        body = Sim.Simulation.Bodies[handle];
        return body.Collidable.Shape == Shape;
    }

    internal bool TryRead(out ForceRigidState state)
    {
        state = default;
        if (!TryResolve(out var pose, out var velocity, out _, out _)) return false;
        var positionCcf = ClutterForceNative.Origin(Native) + Double3Ex.FromBepu(pose.Position);
        var rotationCcf = DoubleQuatEx.FromBepu(pose.Orientation);
        var rotation = VehicleReferenceFrameEx.CcfStatesToCci(Parent, Bubble.Origin.Time, positionCcf,
            Double3Ex.FromBepu(velocity.Linear), out var position, out var linear);
        var body2Cci = doubleQuat.Concatenate(rotationCcf, rotation).NormalizedOrZero();
        var angularCci = Double3Ex.FromBepu(velocity.Angular).Transform(rotation) + Parent.GetAngularVelocityCci();
        var rates = angularCci.Transform(body2Cci.Inverse());
        if (!ForceMath.Finite(position) || !ForceMath.Finite(linear) || !ForceMath.Finite(rates) || body2Cci == default) return false;
        state = new(position, linear, body2Cci, rates); return true;
    }

    internal bool TryCapture(out ClutterForceHold? hold, out string reason) => ClutterForceHold.TryCapture(this, out hold, out reason);
}

/// <summary>Settled-thread native queries. Safety limits reject incomplete queries; capture limits keep nearest overlaps.</summary>
internal static partial class ClutterForceTargets
{
    internal const int MaximumCandidates = 32768;
    internal const int MaximumChildTests = 250000;
    private static readonly List<ProximityHit> NearHits = new(512);
    private static readonly HashSet<CollidableReference> NearSeenCollidables = [];
    private static readonly HashSet<ClutterForceIdentity> NearSeenTargets = [];
    private static bool _installed;
    private static System.Reflection.MethodInfo? _contactMethod;

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;
        var callbacks = typeof(ConstraintSim).Assembly.GetType("KSA.NarrowPhaseCallbacks", true)!;
        _contactMethod = AccessTools.Method(callbacks, "AllowContactGeneration",
            [typeof(int), typeof(CollidableReference), typeof(CollidableReference), typeof(float).MakeByRefType()])
            ?? throw new MissingMethodException("Native clutter contact hook changed.");
        var simField = AccessTools.Field(callbacks, "Sim");
        if (simField?.FieldType != typeof(ConstraintSim) || _contactMethod.ReturnType != typeof(bool))
            throw new MissingMemberException("Native clutter contact ownership changed.");
        // Force initialization checks all private layout dependencies before any native mutation.
        _ = ClutterForceNative.Generation(new BubbleClutterStatics());
        try
        {
            harmony.Patch(_contactMethod, prefix: new HarmonyMethod(typeof(ClutterForceTargets), nameof(FilterHeldContacts)));
            harmony.Patch(ClutterForceNative.ClearMethod, prefix: new HarmonyMethod(typeof(ClutterForceTargets), nameof(BeforeNativeClear)));
            _installed = true;
        }
        catch { Remove(harmony); throw; }
    }

    internal static void Remove(Harmony harmony)
    {
        ClutterForceHold.ReleaseAll();
        if (_contactMethod != null) harmony.Unpatch(_contactMethod, AccessTools.Method(typeof(ClutterForceTargets), nameof(FilterHeldContacts)));
        harmony.Unpatch(ClutterForceNative.ClearMethod, AccessTools.Method(typeof(ClutterForceTargets), nameof(BeforeNativeClear)));
        _installed = false;
    }

    private static bool FilterHeldContacts(ConstraintSim ___Sim, CollidableReference a, CollidableReference b, ref bool __result)
    {
        if (!ClutterForceHold.Suppresses(___Sim, a, b)) return true;
        __result = false; return false;
    }
    private static void BeforeNativeClear(BubbleClutterStatics __instance) => ClutterForceNative.Invalidate(__instance);

    private static bool TryContext(KittenEva owner, out PhysicsBubble? bubble, out string reason)
    {
        bubble = null; reason = "";
        if (!_installed) { reason = "Ground-object Furce physics is unavailable."; return false; }
        if (!ForceTargeting.IsLive(owner) || !ReferenceEquals(owner, Program.ControlledVehicle) || Program.EditorFlag)
        { reason = "The controlled kitten changed."; return false; }
        bubble = ClutterForceNative.OwnerBubble(owner);
        // A clean absence is an empty resident set; vessels are still selectable outside surface bubbles.
        if (bubble?.ConstraintSim == null || bubble.Parent is not Celestial) { bubble = null; return false; }
        if (bubble.Origin.BubFrame != BubbleFrame.Ccf || !GameSettings.GetGroundClutterCollisions())
        { bubble = null; return false; }
        if (!ClutterForceNative.Describes(bubble.GroundClutterStatics, (Celestial)bubble.Parent))
        { bubble = null; return false; }
        if (bubble.GroundClutterStatics.StaticCount + bubble.GroundClutterStatics.DynamicBodyCount > MaximumCandidates)
        { bubble = null; reason = "Nearby ground geometry exceeds the Furce query budget."; return false; }
        return true;
    }

    internal static bool TryPick(KittenEva owner, Ray rayCci, out ClutterForceTarget? target,
        out double distance, out double3 hitCci, out string reason)
    {
        target = null; distance = double.PositiveInfinity; hitCci = default;
        if (!TryContext(owner, out var bubble, out reason)) return false;
        if (!ForceMath.Finite(rayCci.Origin) || !ForceMath.Finite(rayCci.Direction) || rayCci.Direction.LengthSquared() < .5)
        { reason = "The Furce pick ray is invalid."; return false; }
        var cci2Ccf = bubble!.Parent.GetCci2Ccf(bubble.Origin.Time);
        var origin = rayCci.Origin.Transform(cci2Ccf) - ClutterForceNative.Origin(bubble.GroundClutterStatics);
        var direction = double3.NormalizeOrZero(rayCci.Direction).Transform(cci2Ccf);
        if (origin.LengthSquared() > 4096.0 * 4096)
        { reason = "Ground objects must be selected near the current physics bubble."; return false; }
        if (!TryPickRange(rayCci.Origin - owner.GetPositionCci(), out float maximumDistance))
        { reason = "The Furce pick range is invalid."; return false; }
        var query = new PickQuery(bubble, owner.System);
        bubble.ConstraintSim!.Simulation.RayCast(origin.ToBepu(), direction.ToBepu(), maximumDistance,
            bubble.ConstraintSim.BufferPool, ref query);
        if (query.Exceeded) { reason = "Ground geometry exceeds the Furce pick budget."; return false; }
        if (query.Target == null) { reason = ""; return false; }
        target = query.Target; distance = query.Distance;
        hitCci = rayCci.Origin + double3.NormalizeOrZero(rayCci.Direction) * distance;
        reason = target.DisplayName; return true;
    }

    internal static bool GatherNear(KittenEva owner, double3 centerCci, double radius,
        List<ClutterForceCandidate> destination, int maximum, out string reason,
        HashSet<ClutterForceIdentity>? excluded = null)
    {
        destination.Clear();
        if (!TryContext(owner, out var bubble, out reason)) return reason.Length == 0;
        if (!ForceMath.Finite(centerCci) || !ForceMath.ValidAngryRadius(radius)
            || maximum < 0 || maximum > ForceCaptureLimits.Maximum)
        { reason = "The ground-object proximity query is invalid."; return false; }
        if (maximum == 0) { reason = ""; return true; }
        var cci2Ccf = bubble!.Parent.GetCci2Ccf(bubble.Origin.Time);
        var center = centerCci.Transform(cci2Ccf) - ClutterForceNative.Origin(bubble.GroundClutterStatics);
        if (center.LengthSquared() > 4096.0 * 4096)
        { reason = "Ground objects must remain near the current physics bubble."; return false; }
        var hits = NearHits;
        hits.Clear();
        NearSeenCollidables.Clear();
        NearSeenTargets.Clear();
        try
        {
            var query = new NearQuery<NativeClutterPickFilter>(bubble.ConstraintSim!.Simulation,
                new(bubble.GroundClutterStatics), center.ToBepu(), hits, MaximumCandidates, NearSeenCollidables);
            var pose = new RigidPose(center.ToBepu()); var velocity = default(BodyVelocity);
            // Zero motion and zero horizon perform exact native sphere overlap, including compounds.
            // Explicit convergence settings avoid the convenience overload's division by zero for zero speed.
            bubble.ConstraintSim!.Simulation.Sweep(new Sphere((float)radius), in pose, in velocity, 0,
                bubble.ConstraintSim.BufferPool, ref query, .0001f, .00001f, 32);
            if (query.Exceeded || query.Unsupported)
            {
                destination.Clear(); reason = query.Unsupported ? "A nearby ground collider cannot be queried safely."
                    : "Nearby ground geometry exceeds the Furce proximity query budget; nothing was captured.";
                return false;
            }
            SortNearest(hits);
            foreach (var hit in hits)
            {
                if (!TryIdentity(bubble.GroundClutterStatics, hit.Collidable, out var key))
                { destination.Clear(); reason = "A nearby ground object changed during the Furce query."; return false; }
                if ((excluded == null || !excluded.Contains(key)) && NearSeenTargets.Add(key))
                {
                    if (!TryTarget(bubble, owner.System, hit.Collidable, out var target) || target == null)
                    { destination.Clear(); reason = "A nearby ground object changed during the Furce query."; return false; }
                    destination.Add(new(target, hit.DistanceSquared));
                    if (destination.Count == maximum) break;
                }
            }
            reason = ""; return true;
        }
        finally
        {
            hits.Clear();
            NearSeenCollidables.Clear();
            NearSeenTargets.Clear();
        }
    }

    /// <summary>Settled-only forgiving pick, used only after exact vessel/clutter picking misses.</summary>
    internal static bool TryPickNear(KittenEva owner, Ray rayCci, double tolerance,
        out ClutterForceTarget? target, out double distance, out double3 hitCci, out string reason)
    {
        target = null; distance = double.PositiveInfinity; hitCci = default; reason = "";
        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > .5)
        { reason = "Ground-object pick tolerance must be between 0 and 0.5 m."; return false; }
        if (tolerance == 0 || !TryContext(owner, out var bubble, out reason)) return false;
        if (!ForceMath.Finite(rayCci.Origin) || !ForceMath.Finite(rayCci.Direction) || rayCci.Direction.LengthSquared() < .5)
        { reason = "The Furce pick ray is invalid."; return false; }
        var cci2Ccf = bubble!.Parent.GetCci2Ccf(bubble.Origin.Time);
        var originCcf = ClutterForceNative.Origin(bubble.GroundClutterStatics);
        var origin = rayCci.Origin.Transform(cci2Ccf) - originCcf;
        var direction = double3.NormalizeOrZero(rayCci.Direction).Transform(cci2Ccf);
        if (!ForceMath.Finite(origin) || origin.LengthSquared() > 4096.0 * 4096)
        { reason = "Ground objects must be selected near the current physics bubble."; return false; }
        if (!TryPickRange(rayCci.Origin - owner.GetPositionCci(), out float maximumDistance))
        { reason = "The Furce pick range is invalid."; return false; }
        var sim = bubble.ConstraintSim!;
        var query = new NearPickQuery<NativeClutterPickFilter>(sim.Simulation,
            new(bubble.GroundClutterStatics), origin.ToBepu(), direction.ToBepu(), (float)tolerance);
        var pose = new RigidPose(origin.ToBepu());
        var velocity = new BodyVelocity(direction.ToBepu());
        sim.Simulation.Sweep(new Sphere((float)tolerance), in pose, in velocity, maximumDistance,
            sim.BufferPool, ref query, .0001f, .00001f, 32);
        if (query.Exceeded || query.Unsupported || query.OriginOverlap)
        {
            reason = query.Exceeded ? "Ground geometry exceeds the forgiving Furce pick budget."
                : query.Unsupported ? "A nearby ground collider cannot be picked safely."
                : "The forgiving Furce pick starts inside a ground object; move the camera clear of it.";
            return false;
        }
        if (query.Hit is not { } collider) return false;
        if (!TryTarget(bubble, owner.System, collider, out target) || target == null || !target.IsValid)
        { target = null; reason = "The nearby ground object changed during selection."; return false; }
        // Native positive-time sweep locations are actual surface contacts, not
        // the swept sphere centre. Keep that off-ray point for anchoring and for
        // the caller's separate terrain check along camera-to-contact segment.
        hitCci = (originCcf + Double3Ex.FromBepu(query.Contact)).Transform(cci2Ccf.Inverse());
        distance = double3.Dot(hitCci - rayCci.Origin, double3.NormalizeOrZero(rayCci.Direction));
        if (!ForceMath.Finite(hitCci) || !double.IsFinite(distance) || distance <= 0)
        { target = null; distance = double.PositiveInfinity; hitCci = default; reason = "The nearby ground contact is invalid."; return false; }
        reason = target.DisplayName; return true;
    }

    // Triangle inequality bounds the camera-to-contact distance for every point
    // within Force range of the kitten. Final owner-to-contact checks stay in the
    // unified picker; this never widens the actual capture range or bubble safety.
    internal static bool TryPickRange(double3 ownerRelativeRayOrigin, out float maximumDistance)
    {
        maximumDistance = 0;
        if (!ForceMath.Finite(ownerRelativeRayOrigin)) return false;
        double distance = ownerRelativeRayOrigin.Length() + ForceTargeting.MaximumRange;
        if (!double.IsFinite(distance) || distance > float.MaxValue) return false;
        maximumDistance = (float)distance;
        // Float conversion must not shorten a conservative distance bound.
        if (maximumDistance < distance) maximumDistance = float.BitIncrement(maximumDistance);
        return float.IsFinite(maximumDistance);
    }

    private static bool TryTarget(PhysicsBubble bubble, CelestialSystem system, CollidableReference collidable, out ClutterForceTarget? target)
    {
        target = null; var native = bubble.GroundClutterStatics; var sim = bubble.ConstraintSim!;
        if (collidable.Mobility == CollidableMobility.Static)
        {
            if (!ClutterForceNative.TryStatic(native, collidable.StaticHandle, out var key, out _)) return false;
            var shape = sim.Simulation.Statics[collidable.StaticHandle].Shape;
            target = new(bubble, system, key, shape, collidable.StaticHandle, null); return true;
        }
        if (!ClutterForceNative.TryDynamic(native, collidable.BodyHandle, out var dynamicKey, out var dynamicShape)) return false;
        target = new(bubble, system, dynamicKey, dynamicShape, null, collidable.BodyHandle); return true;
    }

    private static bool TryIdentity(BubbleClutterStatics native, CollidableReference collidable,
        out ClutterForceIdentity identity)
    {
        BubbleClutterStatics.ClutterInstanceKey key;
        if (collidable.Mobility == CollidableMobility.Static)
        {
            if (!ClutterForceNative.TryStatic(native, collidable.StaticHandle, out key, out _))
            { identity = default; return false; }
        }
        else if (!ClutterForceNative.TryDynamic(native, collidable.BodyHandle, out key, out _))
        { identity = default; return false; }
        identity = new(native, ClutterForceNative.Generation(native), key);
        return true;
    }
}
