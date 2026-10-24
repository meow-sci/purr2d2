using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

// Compiles the production request/availability/lifetime partial. Only the game,
// motion and pose boundaries are fakes; their own fixtures test those adapters.
internal sealed partial class SaberRuntime
{
    private bool _ready = true, _disposed = false;
    private string? _fault = null;
    private KittenEva? _owner;
    private readonly TestSettings _settings = new();
    private sealed class TestSettings { internal float JumpComboIntervalSeconds = .75f; }
    private readonly SaberPoseController _pose = new();
    private readonly AssistedSliceSequence _sliceSequence = new();
    private bool _selectingSlice;
    private int _cancelledSlices, _resetDrags;
    private void CancelAssistedSlice(string reason)
    { _selectingSlice = false; _sliceSequence.Cancel(); _cancelledSlices++; }
    private void ResetOverlaySwing() => _resetDrags++;

    public static void Main()
    {
        var runtime = new SaberRuntime();
        KittenEva first = new(), second = new();
        runtime._owner = first; Program.ControlledVehicle = first;
        runtime.UpdateSomersault();
        Check(runtime._somersaultPose.Eligible && runtime.CanStartSomersault(out _)
            && runtime.ShowSomersaultButtons && runtime._somersaultMotion.ComboIntervalSeconds == .75,
            "controlled landed kitten binds an independent movement pose and the default combo interval");
        runtime._settings.JumpComboIntervalSeconds = 1.5f;
        Check(runtime.ShowSomersaultButtons && runtime._somersaultMotion.ComboIntervalSeconds == 1.5,
            "the visible controls path propagates the configured maximum interval");
        runtime._settings.JumpComboIntervalSeconds = .1f;
        Check(runtime.CanStartSomersault(out _) && Math.Abs(runtime._somersaultMotion.ComboIntervalSeconds - .1) < 1e-6,
            "availability checks propagate the configured minimum interval");
        runtime.UpdateSomersault();
        Check(Math.Abs(runtime._somersaultMotion.ComboIntervalSeconds - .1) < 1e-6,
            "the settled somersault update keeps controller timing synchronized with settings");
        foreach (var direction in Enum.GetValues<SomersaultDirection>())
        {
            runtime.QueueSomersault(direction);
            runtime.QueueSomersault(SomersaultDirection.Right);
            Check(runtime.SomersaultActive && runtime._somersaultMotion.Launches == (int)direction,
                "queued clicks do not mutate physics and busy clicks cannot replace the pending direction");
            runtime.UpdateSomersault();
            Check(runtime._somersaultMotion.Direction == direction
                && runtime._somersaultMotion.Launches == (int)direction + 1,
                "each accepted direction is dispatched exactly once");
            runtime.UpdateSomersault();
            runtime.QueueSomersault(direction);
            Check(runtime._somersaultMotion.Launches == (int)direction + 1
                && !runtime._somersaultRequest.HasValue, "active flight never queues a second jump");
            runtime._somersaultMotion.Reset();
        }

        first.LocomotionState.Mode = LocomotionMode.Airborne;
        first.Situation = Situation.Freefall;
        Check(runtime.ShowSomersaultButtons && !runtime.CanStartSomersault(out _),
            "airborne controls remain visible without permitting an airborne launch");
        for (int i = 0; i < 10; i++)
        {
            first.LocomotionState.Mode = LocomotionMode.Grounded;
            first.Situation = Situation.Rolling;
            Check(runtime.ShowSomersaultButtons && runtime.CanStartSomersault(out _), "surface contact enables the stable controls");
            first.Situation = Situation.Freefall;
            Check(runtime.ShowSomersaultButtons && !runtime.CanStartSomersault(out _), "a contact gap cannot hide the controls or authorize a jump");
            runtime.QueueSomersault(SomersaultDirection.Forward);
            Check(!runtime._somersaultRequest.HasValue, "clicking during a contact gap cannot queue a deferred or midair jump");
            first.LocomotionState.Mode = LocomotionMode.Airborne;
            Check(runtime.ShowSomersaultButtons, "repeated low-gravity hops preserve control visibility");
        }
        foreach (var mode in new[] { LocomotionMode.Mmu, LocomotionMode.Ladder, LocomotionMode.Swimming, LocomotionMode.Tumbling })
        {
            first.LocomotionState.Mode = mode; first.Situation = Situation.Landed;
            Check(runtime.ShowSomersaultButtons && !runtime.CanStartSomersault(out _),
                "other movement modes retain the controls without enabling a jump");
        }
        first.LocomotionState.Mode = LocomotionMode.Grounded;
        first.Situation = Situation.Rolling;
        Check(runtime.ShowSomersaultButtons, "walking on terrain shows the controls");

        int launches = runtime._somersaultMotion.Launches;
        runtime.QueueSomersault(SomersaultDirection.Forward);
        Program.ControlledVehicle = second;
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Launches == launches && !runtime._somersaultRequest.HasValue
            && !runtime.ShowSomersaultButtons, "changed controlled vehicle invalidates stale queued ownership");
        runtime._owner = second;
        runtime.UpdateSomersault();
        Check(runtime._somersaultPose.Owner == second && !runtime.SomersaultActive,
            "owner switch releases the old pose and binds the new owner");

        runtime._sliceSequence.Start();
        runtime.QueueSomersault(SomersaultDirection.Forward);
        Check(!runtime._somersaultRequest.HasValue && !runtime.CanStartSomersault(out _),
            "an unfinished slice stroke blocks movement");
        runtime._sliceSequence.Advance(.1, true, true, false);
        for (int i = 0; i < 100; i++) runtime._sliceSequence.Advance(.1, true, true, false);
        runtime._sliceSequence.NotifyRendered();
        Check(runtime._sliceSequence.EndPresented, "fixture has a completed stroke with pending cut work");
        runtime.QueueSomersault(SomersaultDirection.Left);
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Active && runtime._sliceSequence.Active && runtime._cancelledSlices == 0,
            "moving after a displayed stroke preserves pending cut work");
        runtime._somersaultMotion.Reset(); runtime._sliceSequence.Cancel();

        runtime._selectingSlice = true;
        runtime.QueueSomersault(SomersaultDirection.Backward);
        runtime.UpdateSomersault();
        Check(!runtime._selectingSlice && runtime._cancelledSlices == 1 && runtime._resetDrags == 0,
            "accepted movement releases uncommitted slice selection without resetting the drag pose");
        runtime._somersaultMotion.Reset();

        runtime._pose.Swinging = true;
        Check(!runtime.CanStartSomersault(out _), "ordinary swing finishes before a flip acquires the pose");
        runtime._pose.Swinging = false;
        runtime.QueueSomersault(SomersaultDirection.Forward);
        runtime._somersaultMotion.Available = false;
        launches = runtime._somersaultMotion.Launches;
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Launches == launches && !runtime._somersaultRequest.HasValue,
            "availability is rechecked at handoff and a rejected request is not deferred");
        runtime._somersaultMotion.Available = true;

        Program.PlayerTime = 10;
        runtime.QueueSomersault(SomersaultDirection.Forward); runtime.UpdateSomersault();
        runtime._somersaultMotion.FollowupAvailable = true;
        runtime._somersaultMotion.AllowedUntil = 10.5;
        Program.PlayerTime = 10.5;
        runtime.QueueSomersault(SomersaultDirection.Forward);
        Program.PlayerTime = 10.6;
        runtime.QueueSomersault(SomersaultDirection.Right);
        Check(runtime._somersaultRequest is { PressTime: 10.5, Direction: SomersaultDirection.Forward },
            "a pending duplicate cannot overwrite the deliberate press timestamp or direction");
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.LastPressTime == 10.5,
            "production runtime preserves the in-window press through a later settled handoff");
        runtime._somersaultMotion.FollowupAvailable = false;
        runtime._somersaultMotion.AllowedUntil = double.PositiveInfinity;
        runtime._somersaultMotion.Reset();

        runtime.QueueSomersault(SomersaultDirection.Forward); runtime.UpdateSomersault();
        runtime._somersaultPose.Eligible = false;
        runtime.UpdateSomersault();
        Check(!runtime._somersaultMotion.Active, "lost visual rig releases owned movement without a second launch");
        runtime.ResetSomersault(); runtime.ResetSomersault();
        Check(!runtime.SomersaultActive && runtime._somersaultPose.Owner == null,
            "world/unload cleanup is idempotent");
        runtime._disposed = true;
        Check(!runtime.CanStartSomersault(out _), "disposed runtime rejects new commands");
        Program.EditorFlag = true;
        Check(!runtime.ShowSomersaultButtons, "editor entry hides movement controls");
        Program.EditorFlag = false; second.IsDisposed = true;
        Check(!runtime.ShowSomersaultButtons, "a disposed EVA cannot retain movement controls");
        Console.WriteLine("Somersault runtime arbitration checks passed.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
