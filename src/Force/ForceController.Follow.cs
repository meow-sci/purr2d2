using Brutal.Numerics;
using KSA;

namespace Purr2D2.Force;

internal sealed partial class ForceController
{
    private bool Valid(Held held)
    {
        if (!ForceTargeting.IsLive(held.Source) || !ReferenceEquals(held.Parent, _parent)
            || !ReferenceEquals(held.Source.Parent, held.Parent) || !ReferenceEquals(held.Source.Parts, held.Tree)) return false;
        var parts = held.Source.Parts.Parts;
        if (parts.Length != held.Parts.Length) return false;
        for (int i = 0; i < parts.Length; i++)
            if (!ReferenceEquals(parts[i], held.Parts[i]) || !ReferenceEquals(parts[i].Tree?.OwningVehicle, held.Source)) return false;
        return true;
    }

    private ForceRigidState FollowState(Held held)
    {
        var owner = Owner!;
        if (!ForceMath.TryFollow(owner.GetPositionCci(), owner.GetVelocityCci(), owner.GetBody2Cci(), owner.BodyRates,
            held.PositionBody, held.SourceToOwner, GroupOffsetBody, out var state))
            throw new InvalidOperationException("A held rigid transform is invalid.");
        return state;
    }

    private void Follow(UniverseTime time)
    {
        var owner = Owner!;
        var position = owner.GetPositionCci(); var velocity = owner.GetVelocityCci();
        var rotation = owner.GetBody2Cci(); var rates = owner.BodyRates;
        if (_hasFrame && time == _lastTime && !_offsetDirty && position == _lastOwnerPosition
            && velocity == _lastOwnerVelocity && rotation == _lastOwnerRotation && rates == _lastOwnerRates) return;
        JoinOrbitReaders();
        foreach (var held in _held)
        {
            if (!Valid(held)) throw new InvalidOperationException("A held object changed ownership, parent or topology.");
            WriteState(held.Source, FollowState(held), time);
        }
        _hasFrame = true; _offsetDirty = false; _lastTime = time;
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
