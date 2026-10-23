using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace Purr2D2.Force;

/// <summary>One controlled kitten's transient Force hold. Mutations run only in the settled handoff.</summary>
internal sealed partial class ForceController : IDisposable
{
    internal const int MaximumCaptured = 32;
    private readonly List<Held> _held = new(MaximumCaptured);
    private Harmony? _harmony;
    private bool _available, _disposed, _hasFrame, _offsetDirty;
    private CelestialSystem? _system;
    private IParentBody? _parent;
    private double3 _anchorBody;
    private UniverseTime _lastTime;
    private double3 _lastOwnerPosition, _lastOwnerVelocity, _lastOwnerRates;
    private doubleQuat _lastOwnerRotation;

    private sealed record Held(Vehicle Source, IParentBody Parent, PartTree Tree, Part[] Parts,
        double3 PositionBody, doubleQuat SourceToOwner, ForceRigidState Original);

    internal int CapturedCount => _held.Count;
    internal bool HasCapture => _held.Count != 0;
    internal KittenEva? Owner { get; private set; }
    internal double3 GroupOffsetBody { get; private set; }
    internal string Status { get; private set; } = "";

    internal void Install(Harmony harmony)
    {
        if (_available || _disposed) return;
        try { ForcePhysicsPatches.Install(harmony); _harmony = harmony; _available = true; }
        catch (Exception ex)
        {
            try { ForcePhysicsPatches.Remove(harmony); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force patch cleanup failed: {cleanup}"); }
            Status = "Force physics is incompatible with this game build.";
            Console.WriteLine($"[purr2d2] Force installation failed: {ex}");
        }
    }

    internal bool Capture(KittenEva owner, ForcePick target, bool angry)
    {
        if (!_available || _disposed) { Status = "Force physics is unavailable."; return false; }
        if (HasCapture) { Status = "Release the held objects before another capture."; return false; }
        var candidates = new List<Vehicle>(MaximumCaptured);
        try
        {
            if (!ForceTargeting.Gather(owner, target, angry, candidates, out var refusal)) { Status = refusal; return false; }
        }
        catch (Exception ex) { Status = "Force target query failed: " + ex.Message; return false; }
        try
        {
            var ownerRotation = owner.GetBody2Cci().NormalizedOrZero();
            var ownerPosition = owner.GetPositionCci();
            if (ownerRotation == default || !ForceMath.Finite(ownerPosition)) throw new InvalidOperationException("Kitten frame is invalid.");
            foreach (var source in candidates)
            {
                var position = source.GetPositionCci();
                var rotation = source.GetBody2Cci().NormalizedOrZero();
                var original = new ForceRigidState(position, source.GetVelocityCci(), rotation, source.BodyRates);
                if (rotation == default || !ForceMath.Finite(position) || !ForceMath.Finite(original.Velocity)
                    || !ForceMath.Finite(original.BodyRates)) throw new InvalidOperationException("An object has invalid physics state.");
                _held.Add(new(source, source.Parent, source.Parts, source.Parts.Parts.ToArray(),
                    (position - ownerPosition).Transform(ownerRotation.Inverse()),
                    doubleQuat.Concatenate(rotation, ownerRotation.Inverse()), original));
            }
            Owner = owner; _system = owner.System; _parent = owner.Parent;
            GroupOffsetBody = double3.Zero;
            var hit = target.Vehicle.GetPositionCci() + target.HitBody.Transform(target.Vehicle.GetBody2Cci());
            _anchorBody = (hit - ownerPosition).Transform(ownerRotation.Inverse());
            Follow(Universe.GetLastSimStep().NextTime);
            // Publish only after every source has actually left its bubble and accepted its held state.
            ForcePhysicsPatches.Publish(_held.Select(item => item.Source).ToArray());
            Status = $"Holding {CapturedCount} object{(CapturedCount == 1 ? "" : "s")}.";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                JoinOrbitReaders();
                foreach (var held in _held)
                {
                    try { if (ForceTargeting.IsLive(held.Source)) WriteState(held.Source, held.Original, Universe.GetLastSimStep().NextTime); }
                    catch (Exception restoreError) { Console.WriteLine($"[purr2d2] Force capture rollback failed: {restoreError}"); }
                }
            }
            catch (Exception restoreError) { Console.WriteLine($"[purr2d2] Force capture rollback failed: {restoreError}"); }
            finally { Clear(); }
            Status = "Force capture failed: " + ex.Message;
            Console.WriteLine($"[purr2d2] {Status}");
            return false;
        }
    }

    internal void Update(KittenEva? controlledOwner)
    {
        if (_disposed || !HasCapture) return;
        if (Owner == null || !ReferenceEquals(Owner, controlledOwner) || !ReferenceEquals(Owner, Program.ControlledVehicle)
            || !ForceTargeting.IsLive(Owner) || !ReferenceEquals(_system, Universe.CurrentSystem) || Program.EditorFlag
            || !ReferenceEquals(Owner.Parent, _parent) || _held.Any(item => !Valid(item)))
        { Release(false); Status = "Force released after an owner, world or object change."; return; }
        try { Follow(Universe.GetLastSimStep().NextTime); }
        catch (Exception ex)
        {
            // Never strand a vehicle outside the next ordinary worker-adoption pass.
            try { Release(false); }
            finally { Clear(); Status = "Force ended: " + ex.Message; }
            Console.WriteLine($"[purr2d2] {Status}");
        }
    }

    internal void SetOffset(double3 groupOffsetBody)
    {
        if (!HasCapture || !ForceMath.ValidOffset(groupOffsetBody)) return;
        if (groupOffsetBody.LengthSquared() > ForceMath.MaximumOffset * ForceMath.MaximumOffset)
            groupOffsetBody = double3.NormalizeOrZero(groupOffsetBody) * ForceMath.MaximumOffset;
        if (GroupOffsetBody == groupOffsetBody) return;
        GroupOffsetBody = groupOffsetBody;
        _offsetDirty = true;
    }

    internal bool TryGetPoseTargetBody(KittenEva owner, out double3 targetBody)
    {
        targetBody = _anchorBody + GroupOffsetBody;
        return HasCapture && ReferenceEquals(owner, Owner) && ForceMath.Finite(targetBody);
    }

    internal void Release(bool throwObjects)
    {
        if (!HasCapture) return;
        int count = CapturedCount;
        string? failure = null;
        try
        {
            JoinOrbitReaders();
            var time = Universe.GetLastSimStep().NextTime;
            foreach (var held in _held)
            {
                if (!ForceTargeting.IsLive(held.Source)) continue;
                try
                {
                    bool follows = Owner is { } owner && ForceTargeting.IsLive(owner) && ReferenceEquals(owner.Parent, held.Parent)
                        && ReferenceEquals(_system, Universe.CurrentSystem) && Valid(held);
                    var state = follows ? FollowState(held) : new ForceRigidState(held.Source.GetPositionCci(),
                        held.Source.GetVelocityCci(), held.Source.GetBody2Cci(), held.Source.BodyRates);
                    if (throwObjects && follows)
                        state = state with { Velocity = ForceMath.ThrowVelocity(state, Owner!.GetPositionCci(), Owner.GetBody2Cci()) };
                    WriteState(held.Source, state, time);
                }
                catch (Exception ex) { failure = ex.Message; Console.WriteLine($"[purr2d2] Force release failed for {held.Source.Id}: {ex}"); }
            }
        }
        catch (Exception ex) { failure = ex.Message; Console.WriteLine($"[purr2d2] Force release could not join native readers: {ex}"); }
        finally { Clear(); }
        Status = failure != null ? "Force released; an object could not be updated: " + failure
            : $"{(throwObjects ? "Threw" : "Released")} {count} object{(count == 1 ? "" : "s")}.";
    }

    // During world teardown only ownership is discarded; native state remains game-owned.
    internal void Reset() { Clear(); Status = ""; }
    private void Clear()
    {
        ForcePhysicsPatches.Clear();
        _held.Clear(); Owner = null; _system = null; _parent = null;
        GroupOffsetBody = double3.Zero; _anchorBody = double3.Zero; _hasFrame = false; _offsetDirty = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        if (_harmony != null) ForcePhysicsPatches.Remove(_harmony);
        _available = false; _disposed = true;
    }
}
