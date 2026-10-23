using System.Threading;
using BepuPhysics;
using BepuPhysics.Collidables;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

/// <summary>A session-only kinematic lease over a native displaced body; the game owns its entire lifetime.</summary>
internal sealed class ClutterForceHold : IDisposable
{
    private static readonly object RegistryLock = new();
    private static ClutterForceHold[] _active = [];
    private readonly BodyHandle _handle;
    private readonly BodyInertia _inertia;
    private bool _released;
    private ForceRigidState _lastState;
    private int _cleared;
    internal ClutterForceTarget Target { get; }
    internal bool IsValid => !_released && Volatile.Read(ref _cleared) == 0 && Target.FrameValid
        && Target.TryResolveBody(out var body) && body.Handle == _handle && body.Kinematic;

    private ClutterForceHold(ClutterForceTarget target, BodyReference body, ForceRigidState original)
    { Target = target; _handle = body.Handle; _inertia = body.LocalInertia; _lastState = original; }

    internal static bool TryCapture(ClutterForceTarget target, out ClutterForceHold? hold, out string reason)
    {
        hold = null; reason = "The selected ground object is no longer available.";
        if (!target.IsValid || Volatile.Read(ref _active).Any(item => !item._released && item.Target.StableKey == target.StableKey)) return false;
        if (!ClutterForceNative.Promote(target, out var handle, out reason) || !target.TryResolveBody(out var body)
            || body.Handle != handle) return false;
        if (body.Kinematic || !float.IsFinite(body.LocalInertia.InverseMass) || body.LocalInertia.InverseMass <= 0)
        { reason = "The ground object has no usable native mass."; return false; }
        if (!target.TryRead(out var original)) return false;
        var capture = new ClutterForceHold(target, body, original);
        try
        {
            // Publish contact suppression before changing mobility. Solver dispatch cannot occur in
            // this settled transaction, but the order also keeps exceptional cleanup conservative.
            Register(capture);
            body.BecomeKinematic(); body.Velocity = default; body.Awake = true; body.UpdateBounds();
            if (!body.Kinematic) throw new InvalidOperationException("KSA did not accept a kinematic clutter hold.");
            hold = capture; reason = ""; return true;
        }
        catch
        {
            capture.Release();
            throw;
        }
    }

    internal bool TryRead(out ForceRigidState state)
    { state = _lastState; return IsValid; }

    internal void Write(ForceRigidState state)
    {
        if (!IsValid || !Target.TryResolveBody(out var body)) throw new InvalidOperationException("The held ground object left its native physics bubble.");
        WriteBody(Target, body, state, moving: false);
        _lastState = state;
    }

    private static void WriteBody(ClutterForceTarget target, BodyReference body, ForceRigidState state, bool moving)
    {
        if (!ForceMath.Finite(state.Position) || !ForceMath.Finite(state.Velocity) || !ForceMath.Finite(state.BodyRates)
            || !ForceMath.Finite(state.Body2Cci) || state.Body2Cci.NormalizedOrZero() == default)
            throw new InvalidOperationException("The held ground object's rigid state is invalid.");
        var cci2Ccf = VehicleReferenceFrameEx.CciStatesToCcf(target.Parent, target.Bubble.Origin.Time,
            state.Position, state.Velocity, out var positionCcf, out var velocityCcf);
        var local = positionCcf - ClutterForceNative.Origin(target.Native);
        // A native body is deliberately confined to its resident local bubble. Streaming or orbital
        // promotion is a separate feature; do not write inaccurate floats or leave an unserviced body.
        if (!ForceMath.Finite(local) || local.LengthSquared() > 4096.0 * 4096)
            throw new InvalidOperationException("The ground object exceeded the local Force physics range.");
        var rotationCcf = doubleQuat.Concatenate(state.Body2Cci, cci2Ccf).NormalizedOrZero();
        var angularCci = state.BodyRates.Transform(state.Body2Cci) - target.Parent.GetAngularVelocityCci();
        var angularCcf = angularCci.Transform(cci2Ccf);
        var pose = new RigidPose(local.ToBepu(), rotationCcf.ToBepu());
        var velocity = new BodyVelocity(velocityCcf.ToBepu(), angularCcf.ToBepu());
        if (!Finite(pose.Position) || !Finite(velocity.Linear) || !Finite(velocity.Angular))
            throw new InvalidOperationException("The ground object's native state overflowed.");
        // Bepu integrates kinematic poses even though it skips their gravity integration. Keep
        // the held body stationary between settled follow writes; inherit rates only on release.
        body.Awake = true; body.Pose = pose; body.Velocity = moving ? velocity : default; body.UpdateBounds();
    }

    internal bool Release(ForceRigidState? state = null)
    {
        if (_released) return true;
        bool restored = true;
        try
        {
            // An owner/world/frame change can invalidate following while the original body still
            // exists. Restore its inertia anyway. A native Clear removes it, so no handle is touched.
            if (Volatile.Read(ref _cleared) == 0 && Target.TryResolveBody(out var body) && body.Handle == _handle)
            {
                try { if (Target.FrameValid) WriteBody(Target, body, state ?? _lastState, moving: true); }
                finally { body.SetLocalInertia(in _inertia); body.Awake = true; body.UpdateBounds(); }
            }
        }
        catch (Exception ex)
        {
            restored = false; Console.WriteLine($"[purr2d2] Native ground-object release failed: {ex}");
            // Retain the registry lease if a reachable kinematic body could not restore. A subsequent
            // Dispose/ReleaseAll retries before its collision filter can be unpatched.
            if (Volatile.Read(ref _cleared) == 0 && Target.TryResolveBody(out var body)
                && body.Handle == _handle && body.Kinematic) return false;
        }
        _released = true; Unregister(this); return restored;
    }

    public void Dispose()
    {
        if (_released) return;
        JobSystems.VehicleSolver.Wait();
        Release();
    }

    internal static bool Suppresses(ConstraintSim sim, CollidableReference a, CollidableReference b)
    {
        var entries = Volatile.Read(ref _active);
        foreach (var entry in entries)
        {
            if (Volatile.Read(ref entry._cleared) != 0 || !ReferenceEquals(entry.Target.Sim, sim)) continue;
            if (a.Mobility != CollidableMobility.Static && a.BodyHandle == entry._handle
                || b.Mobility != CollidableMobility.Static && b.BodyHandle == entry._handle) return true;
        }
        return false;
    }

    internal static void NativeClearing(BubbleClutterStatics native)
    {
        lock (RegistryLock)
        {
            var entries = _active;
            foreach (var entry in entries)
                if (ReferenceEquals(entry.Target.Native, native)) Volatile.Write(ref entry._cleared, 1);
            Volatile.Write(ref _active, entries.Where(item => !ReferenceEquals(item.Target.Native, native)).ToArray());
        }
    }
    internal static void ReleaseAll()
    {
        JobSystems.VehicleSolver.Wait();
        foreach (var entry in Volatile.Read(ref _active)) entry.Release();
        if (Volatile.Read(ref _active).Length != 0)
            throw new InvalidOperationException("A native ground object could not leave its Force hold; its contact hook must remain installed.");
    }
    private static void Register(ClutterForceHold hold)
    { lock (RegistryLock) Volatile.Write(ref _active, [.. _active, hold]); }
    private static void Unregister(ClutterForceHold hold)
    { lock (RegistryLock) Volatile.Write(ref _active, _active.Where(item => !ReferenceEquals(item, hold)).ToArray()); }
    private static bool Finite(System.Numerics.Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
