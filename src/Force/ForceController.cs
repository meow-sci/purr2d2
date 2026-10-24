using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Force;

internal enum ForceStatusSeverity { None, Warning, Error }

/// <summary>One controlled kitten's transient Force hold. Mutations run only in the settled handoff.</summary>
internal sealed partial class ForceController : IDisposable
{
    private readonly List<Held> _held = new(ForceCaptureLimits.Default);
    private Harmony? _harmony;
    private bool _available, _disposed, _hasFrame, _offsetDirty;
    private CelestialSystem? _system;
    private IParentBody? _parent;
    private double3 _anchorBody;
    private UniverseTime _lastTime;
    private double3 _lastOwnerPosition, _lastOwnerVelocity, _lastOwnerRates;
    private doubleQuat _lastOwnerRotation;

    private sealed record Held(ForceTarget Target, IParentBody Parent, PartTree? Tree, Part[] Parts,
        double3 PositionBody, doubleQuat SourceToOwner, ForceRigidState Original)
    {
        internal Vehicle? Source => Target.Vehicle;
        internal ClutterForceHold? ClutterHold { get; set; }
        internal string DisplayName => Source?.Id ?? Target.Clutter?.DisplayName ?? "Unavailable object";
    }

    internal int CapturedCount => _held.Count;
    internal bool HasCapture => _held.Count != 0;
    internal KittenEva? Owner { get; private set; }
    internal double3 GroupOffsetBody { get; private set; }
    internal string Status { get; private set; } = "";
    internal ForceStatusSeverity StatusSeverity { get; private set; }

    internal void Install(Harmony harmony)
    {
        if (_available || _disposed) return;
        try
        {
            ForcePhysicsPatches.Install(harmony);
            ClutterForceTargets.Install(harmony);
            _harmony = harmony; _available = true;
            SetStatus("", ForceStatusSeverity.None);
        }
        catch (Exception ex)
        {
            try { ClutterForceTargets.Remove(harmony); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force clutter patch cleanup failed: {cleanup}"); }
            try { ForcePhysicsPatches.Remove(harmony); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force patch cleanup failed: {cleanup}"); }
            SetStatus("Force physics is incompatible with this game build.", ForceStatusSeverity.Error);
            Console.WriteLine($"[purr2d2] Force installation failed: {ex}");
        }
    }

    internal bool Capture(KittenEva owner, ForcePick target, bool angry, double angryRadius = ForceMath.DefaultAngryRadius,
        int captureLimit = ForceCaptureLimits.Default)
    {
        if (!_available || _disposed) { SetStatus("Force physics is unavailable.", ForceStatusSeverity.Warning); return false; }
        if (HasCapture) { SetStatus("Release the held objects before another capture.", ForceStatusSeverity.Warning); return false; }
        if (!ForceCaptureLimits.Valid(captureLimit))
        { SetStatus("The Force capture limit must be between 1 and 512 objects; nothing was captured.", ForceStatusSeverity.Warning); return false; }
        var candidates = new List<ForceTarget>(captureLimit);
        try
        {
            if (!ForceTargeting.Gather(owner, target, angry, candidates, out var refusal, angryRadius, captureLimit))
            { SetStatus(refusal, ForceStatusSeverity.Warning); return false; }
            if (candidates.Count < 1 || candidates.Count > captureLimit)
            { SetStatus($"The Force target group exceeds the {captureLimit}-object capture limit; nothing was captured.", ForceStatusSeverity.Warning); return false; }
        }
        catch (Exception ex) { SetStatus("Force target query failed: " + ex.Message, ForceStatusSeverity.Error); return false; }
        try
        {
            var ownerRotation = owner.GetBody2Cci().NormalizedOrZero();
            var ownerPosition = owner.GetPositionCci();
            if (ownerRotation == default || !ForceMath.Finite(ownerPosition)) throw new InvalidOperationException("Kitten frame is invalid.");
            // Snapshot the entire group before promoting or detaching any candidate.
            foreach (var candidate in candidates)
            {
                ForceRigidState original;
                IParentBody parent;
                PartTree? tree = null;
                Part[] parts = [];
                if (candidate.Vehicle is { } source && candidate.Clutter == null)
                {
                    parent = source.Parent; tree = source.Parts; parts = source.Parts.Parts.ToArray();
                    original = new(source.GetPositionCci(), source.GetVelocityCci(), source.GetBody2Cci(), source.BodyRates);
                }
                else if (candidate.Vehicle == null && candidate.Clutter is { } clutter && clutter.IsValid && clutter.IsLocalTo(owner)
                    && clutter.TryRead(out original)) parent = clutter.Parent;
                else throw new InvalidOperationException("A selected object is no longer available.");
                var position = original.Position;
                var rotation = original.Body2Cci.NormalizedOrZero();
                if (rotation == default || !ForceMath.Finite(position) || !ForceMath.Finite(original.Velocity)
                    || !ForceMath.Finite(original.BodyRates)) throw new InvalidOperationException("An object has invalid physics state.");
                if (!ReferenceEquals(parent, owner.Parent)) throw new InvalidOperationException("An object changed parent frame.");
                _held.Add(new(candidate, parent, tree, parts,
                    (position - ownerPosition).Transform(ownerRotation.Inverse()),
                    doubleQuat.Concatenate(rotation, ownerRotation.Inverse()), original));
            }
            Owner = owner; _system = owner.System; _parent = owner.Parent;
            GroupOffsetBody = double3.Zero;
            _anchorBody = (target.HitCci - ownerPosition).Transform(ownerRotation.Inverse());
            foreach (var held in _held)
                if (held.Target.Clutter is { } clutter)
                {
                    if (!clutter.TryCapture(out var hold, out var refusal) || hold == null)
                        throw new InvalidOperationException(refusal);
                    held.ClutterHold = hold;
                }
            Follow(Universe.GetLastSimStep().NextTime);
            // Publish only after every source has actually left its bubble and accepted its held state.
            ForcePhysicsPatches.Publish(_held.Where(item => item.Source != null).Select(item => item.Source!).ToArray());
            SetStatus($"Holding {CapturedCount} object{(CapturedCount == 1 ? "" : "s")}.", ForceStatusSeverity.None);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                JoinOrbitReaders();
                foreach (var held in _held)
                {
                    try
                    {
                        if (held.Source is { } source && ForceTargeting.IsLive(source))
                            WriteState(source, held.Original, Universe.GetLastSimStep().NextTime);
                        else held.ClutterHold?.Release(held.Original);
                    }
                    catch (Exception restoreError) { Console.WriteLine($"[purr2d2] Force capture rollback failed: {restoreError}"); }
                }
            }
            catch (Exception restoreError) { Console.WriteLine($"[purr2d2] Force capture rollback failed: {restoreError}"); }
            finally { Clear(); }
            SetStatus("Force capture failed: " + ex.Message, ForceStatusSeverity.Error);
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
        {
            Release(false);
            if (StatusSeverity == ForceStatusSeverity.None)
                SetStatus("Force released after an owner, world or object change.", ForceStatusSeverity.Warning);
            return;
        }
        try { Follow(Universe.GetLastSimStep().NextTime); }
        catch (Exception ex)
        {
            // Never strand a vehicle outside the next ordinary worker-adoption pass.
            try { Release(false); }
            finally { Clear(); SetStatus("Force ended: " + ex.Message, ForceStatusSeverity.Error); }
            Console.WriteLine($"[purr2d2] {Status}");
        }
        if (HasCapture && StatusSeverity != ForceStatusSeverity.None)
            SetStatus($"Holding {CapturedCount} object{(CapturedCount == 1 ? "" : "s")}.", ForceStatusSeverity.None);
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
        bool joinFailed = false;
        try
        {
            JoinOrbitReaders();
            var time = Universe.GetLastSimStep().NextTime;
            foreach (var held in _held)
            {
                try
                {
                    if (held.Source is { } source && !ForceTargeting.IsLive(source)) continue;
                    bool follows = Owner is { } owner && ForceTargeting.IsLive(owner) && ReferenceEquals(owner.Parent, held.Parent)
                        && ReferenceEquals(_system, Universe.CurrentSystem) && Valid(held);
                    ForceRigidState? state = follows ? FollowState(held) : ReadState(held);
                    if (throwObjects && follows)
                        state = state!.Value with { Velocity = ForceMath.ThrowVelocity(state.Value, Owner!.GetPositionCci(), Owner.GetBody2Cci()) };
                    if (held.Source is { } vehicle && state.HasValue) WriteState(vehicle, state.Value, time);
                    else if (held.ClutterHold is { } hold && !hold.Release(state))
                        throw new InvalidOperationException("The physical ground object left the local physics bubble.");
                }
                catch (Exception ex) { failure = ex.Message; Console.WriteLine($"[purr2d2] Force release failed for {held.DisplayName}: {ex}"); }
            }
        }
        catch (Exception ex) { failure = ex.Message; joinFailed = true; Console.WriteLine($"[purr2d2] Force release could not join native readers: {ex}"); }
        finally { Clear(); }
        SetStatus(failure != null ? "Force released; an object could not be updated: " + failure
            : $"{(throwObjects ? "Threw" : "Released")} {count} object{(count == 1 ? "" : "s")}.",
            failure == null ? ForceStatusSeverity.None : joinFailed ? ForceStatusSeverity.Error : ForceStatusSeverity.Warning);
    }

    // Teardown can arrive outside the settled seam. Join before restoring any native clutter body.
    internal void Reset()
    {
        if (HasCapture)
        {
            try { JobSystems.VehicleSolver.Wait(); Release(false); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force reset could not release native readers: {cleanup}"); }
            finally { Clear(); }
        }
        else Clear();
        if (_available) SetStatus("", ForceStatusSeverity.None);
    }
    private void Clear()
    {
        ForcePhysicsPatches.Clear();
        // Also handles partial capture, a lost bubble and exceptions before ordinary Release reached every peer.
        foreach (var held in _held)
            try { held.ClutterHold?.Dispose(); }
            catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force clutter release cleanup failed: {cleanup}"); }
        _held.Clear(); Owner = null; _system = null; _parent = null;
        GroupOffsetBody = double3.Zero; _anchorBody = double3.Zero; _hasFrame = false; _offsetDirty = false;
    }

    private void SetStatus(string status, ForceStatusSeverity severity)
    {
        Status = status;
        StatusSeverity = severity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Reset(); }
        finally
        {
            try { if (_harmony != null) ClutterForceTargets.Remove(_harmony); }
            finally
            {
                try { if (_harmony != null) ForcePhysicsPatches.Remove(_harmony); }
                finally { _available = false; _disposed = true; }
            }
        }
    }
}
