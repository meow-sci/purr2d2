using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal sealed partial class ForceController
{
    private bool Valid(Held held)
    {
        if (!ReferenceEquals(held.Parent, _parent)) return false;
        if (held.ClutterHold is { } hold)
            return held.Source == null && Owner is { } owner && hold.IsValid && hold.Target.IsLocalTo(owner)
                && ReferenceEquals(hold.Target.Parent, held.Parent)
                && ReferenceEquals(hold.Target.System, _system);
        if (held.Source is not { } source || !ForceTargeting.IsLive(source)
            || !ReferenceEquals(source.Parent, held.Parent) || !ReferenceEquals(source.Parts, held.Tree)) return false;
        var parts = source.Parts.Parts;
        if (parts.Length != held.Parts.Length) return false;
        for (int i = 0; i < parts.Length; i++)
            if (!ReferenceEquals(parts[i], held.Parts[i]) || !ReferenceEquals(parts[i].Tree?.OwningVehicle, source)) return false;
        return true;
    }

    private static ForceRigidState? ReadState(Held held)
    {
        if (held.Source is { } source)
            return new(source.GetPositionCci(), source.GetVelocityCci(), source.GetBody2Cci(), source.BodyRates);
        return held.ClutterHold is { } hold && hold.TryRead(out var state) ? state : null;
    }

    private ForceRigidState FollowState(Held held)
    {
        var owner = Owner!;
        if (!ForceMath.TryFollow(owner.GetPositionCci(), owner.GetVelocityCci(), owner.GetBody2Cci(), owner.BodyRates,
            _groupCenterBody, held.PositionBody, held.SourceToOwner, GroupOffsetBody, GroupSpread, out var state))
            throw new InvalidOperationException("A held rigid transform is invalid.");
        return state;
    }

    private void Follow(UniverseTime time)
    {
        var owner = Owner!;
        var position = owner.GetPositionCci(); var velocity = owner.GetVelocityCci();
        var rotation = owner.GetBody2Cci(); var rates = owner.BodyRates;
        if (_hasFrame && time == _lastTime && !_offsetDirty && !_spreadDirty && position == _lastOwnerPosition
            && velocity == _lastOwnerVelocity && rotation == _lastOwnerRotation && rates == _lastOwnerRates) return;
        JoinOrbitReaders();
        foreach (var held in _held)
        {
            if (!Valid(held)) throw new InvalidOperationException("A held object changed ownership, parent or topology.");
            var state = FollowState(held);
            if (held.Source is { } source) WriteState(source, state, time);
            else if (held.ClutterHold is { } hold) hold.Write(state);
            else throw new InvalidOperationException("A physical ground object could not be captured.");
        }
        _hasFrame = true; _offsetDirty = _spreadDirty = false; _lastTime = time;
        _lastOwnerPosition = position; _lastOwnerVelocity = velocity; _lastOwnerRotation = rotation; _lastOwnerRates = rates;
    }

    private static void JoinOrbitReaders()
    {
        JobSystems.OrbitSolvers.Wait();
        JobSystems.NearestOrbitAndPerformanceWorker.Wait();
    }

    private static void WriteState(Vehicle source, ForceRigidState state, UniverseTime time)
    {
        if (!ForceMath.Finite(state.Position) || !ForceMath.Finite(state.Velocity) || !ForceMath.Finite(state.BodyRates)
            || !ForceMath.Finite(state.Body2Cci) || state.Body2Cci.NormalizedOrZero() == default)
            throw new InvalidOperationException("The Force state is not finite.");
        // Full Teleport(orbit,...) forecasts up to 100 conics and may silently reject a ground orbit.
        // Stock separation uses these same native single-conic/analytic building blocks instead.
        var orbit = Orbit.CreateFromStateCci(source.Parent, time, state.Position, state.Velocity, source.Orbit.OrbitLineColor);
        var plan = new FlightPlan(orbit, source.Hash);
        ForcePhysicsPatches.Detach(source);
        source.SetFlightPlan(plan);
        var body2Cce = doubleQuat.Concatenate(state.Body2Cci, source.Parent.GetCci2Cce()).NormalizedOrZero();
        // Null orbit: use native attitude setters/analytic-state overwrite without forecasting again.
        source.Teleport(null, body2Cce, state.BodyRates);
        if (!ReferenceEquals(source.FlightPlan, plan) || source.HasPhysicsBubble || source.Orbit.StateVectors.StateTime != time)
            throw new InvalidOperationException("KSA did not accept the Force state.");
        source.UpdatePerFrameData();
    }
}
