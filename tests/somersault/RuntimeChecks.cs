using KSA;
using Brutal.GlfwApi;
using Purr2D2.Core;
using Purr2D2.Pose;
using RenderCore.Input;

namespace Purr2D2.Runtime;

// Compiles the production request/availability/lifetime partial. Only the game,
// motion and pose boundaries are fakes; their own fixtures test those adapters.
internal sealed partial class SaberRuntime
{
    private bool _ready = true, _disposed = false, _enabled = true;
    private string? _fault = null;
    private KittenEva? _owner;
    private readonly TestSettings _settings = new();
    private sealed class TestSettings
    {
        internal float JumpComboIntervalSeconds = .75f;
        internal bool SpaceSomersaultEnabled = true;
    }
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
        RunSpaceInputChecks();
        Console.WriteLine("Somersault runtime arbitration checks passed.");
    }

    private static void RunSpaceInputChecks()
    {
        Universe.CurrentSystem = new object(); Universe.Speed = 1;
        Program.EditorFlag = false; Program.PlayerTime = 1; Program.MainWindow = new();
        var runtime = new SaberRuntime();
        var first = new KittenEva(); var second = new KittenEva();
        runtime._owner = first; Program.ControlledVehicle = first;
        runtime.UpdateSomersault();
        var window = Program.GetWindow();
        Check(runtime._settings.SpaceSomersaultEnabled,
            "the keyboard takeover is enabled by default");

        window.SetKey(GlfwKey.Up, GlfwKeyAction.Press);
        bool consumed = runtime.FilterSpaceSomersaultKey(first,
            new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space));
        var original = runtime._somersaultRequest;
        Check(consumed && original is { Direction: SomersaultDirection.Forward, FromSpace: true, PressTime: 1 },
            "a real Space press captures the held arrow direction and exact monotonic press time");
        runtime.QueueSomersault(SomersaultDirection.Right);
        Check(runtime._somersaultRequest == original,
            "a pending Space intent blocks a same-frame overlay-button request");
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(null!, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest == original,
            "a null-window Space event passes through even while an actor has a pending request");
        Program.PlayerTime = 1.1;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Repeat, GlfwKey.Space))
            && !runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Release, GlfwKey.Space))
            && runtime._somersaultRequest == original,
            "Space repeats and release pass through without replacing or duplicating the captured request");
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest == original,
            "a second press before handoff is consumed while the first immutable intent wins");
        runtime.UpdateSomersault();
        int launches = runtime._somersaultMotion.Launches;
        Check(launches == 1 && runtime._somersaultMotion.Direction == SomersaultDirection.Forward,
            "settled handoff starts the captured Space action exactly once");
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Repeat, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "holding Space cannot queue a landing auto-jump");
        window.SetKey(GlfwKey.Up, GlfwKeyAction.Release);

        runtime.ResetSomersault(); runtime.UpdateSomersault();
        runtime.QueueSomersault(SomersaultDirection.Backward);
        var buttonFirst = runtime._somersaultRequest;
        Check(buttonFirst is { FromSpace: false, Direction: SomersaultDirection.Backward }
            && runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest == buttonFirst,
            "a pending overlay-button request blocks a later same-frame Space request");
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Direction == SomersaultDirection.Backward,
            "the first button request keeps its original direction when Space follows");

        CheckSpaceDirection(runtime, first, window, [GlfwKey.Left, GlfwKey.Right], SomersaultDirection.Forward);
        CheckSpaceDirection(runtime, first, window, [GlfwKey.Left], SomersaultDirection.Left);
        CheckSpaceDirection(runtime, first, window, [GlfwKey.Right], SomersaultDirection.Right);
        CheckSpaceDirection(runtime, first, window, [GlfwKey.Up, GlfwKey.Down], SomersaultDirection.Forward);
        CheckSpaceDirection(runtime, first, window, [GlfwKey.Down], SomersaultDirection.Backward);
        CheckSpaceDirection(runtime, first, window, [], SomersaultDirection.Forward);

        int beforeBlocked = runtime._somersaultMotion.Launches;
        runtime._sliceSequence.Start();
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "an unfinished slice consumes Space without a stock fallback or deferred request");
        runtime.UpdateSomersault(); runtime._sliceSequence.Cancel();
        runtime._pose.Swinging = true;
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "a live saber swing consumes but refuses Space without deferring it");
        runtime._pose.Swinging = false;
        Universe.Speed = 0;
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "a paused Space press is consumed and refused immediately");
        Universe.Speed = 1; runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Launches == beforeBlocked,
            "resuming after blocked presses never launches a deferred jump");

        first.LocomotionState.Mode = LocomotionMode.Airborne;
        runtime._somersaultMotion.Active = true;
        runtime._somersaultMotion.FollowupAvailable = false;
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "an exhausted airborne combo consumes Space without launching a fallback");
        runtime._somersaultMotion.Reset(); first.LocomotionState.Mode = LocomotionMode.Grounded;

        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(new GlfwWindow(), GlfwKeyAction.Press, GlfwKey.Space)),
            "a key event from another window passes through");
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Up)),
            "arrow-key events pass through without becoming Space requests");
        Check(!runtime.FilterSpaceSomersaultKey(second,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "a noncontrolled EVA passes through");
        second.IsDisposed = true;
        Check(!runtime.FilterSpaceSomersaultKey(second,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "a disposed EVA passes through");
        second.IsDisposed = false;
        Program.EditorFlag = true;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "editor input passes through");
        Program.EditorFlag = false;
        foreach (var mode in new[] { LocomotionMode.Mmu, LocomotionMode.Ladder, LocomotionMode.Swimming, LocomotionMode.Tumbling })
        {
            first.LocomotionState.Mode = mode;
            Check(!runtime.FilterSpaceSomersaultKey(first,
                    new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
                $"{mode} input passes through");
        }
        first.LocomotionState.Mode = LocomotionMode.Grounded;
        first.FlightComputer.RCSMode = FlightComputerRCSMode.Enabled;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "RCS input passes through");
        first.FlightComputer.RCSMode = FlightComputerRCSMode.Disabled;
        runtime._enabled = false;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "a disabled mod passes Space through unchanged");
        runtime._enabled = true;
        runtime._ready = false;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "an uninitialized runtime passes Space through unchanged");
        runtime._ready = true;
        runtime._disposed = true;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "a disposed runtime passes Space through unchanged");
        runtime._disposed = false;
        runtime._fault = "test fault";
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space)),
            "a faulted runtime passes Space through unchanged");
        runtime._fault = null;
        runtime._settings.SpaceSomersaultEnabled = false;
        Check(!runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && !runtime._somersaultRequest.HasValue,
            "turning off the keyboard toggle restores stock Space handling");
        foreach (SomersaultDirection direction in Enum.GetValues<SomersaultDirection>())
        {
            runtime.ResetSomersault(); runtime.UpdateSomersault();
            runtime.QueueSomersault(direction);
            runtime.UpdateSomersault();
            Check(runtime._somersaultMotion.Active && runtime._somersaultMotion.Direction == direction,
                $"the {direction} overlay button remains available while the keyboard toggle is off");
        }
        runtime.ResetSomersault(); runtime._settings.SpaceSomersaultEnabled = true;
        runtime.UpdateSomersault();

        Program.PlayerTime = 19;
        runtime.FilterSpaceSomersaultKey(first,
            new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space));
        runtime._settings.SpaceSomersaultEnabled = false;
        runtime.QueueSomersault(SomersaultDirection.Backward);
        Check(runtime._somersaultRequest is { FromSpace: false, Direction: SomersaultDirection.Backward },
            "a disabled keyboard intent releases the pending slot for an overlay-button request");
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Direction == SomersaultDirection.Backward,
            "the button request still hands off after disabling the keyboard toggle");
        runtime.ResetSomersault(); runtime._settings.SpaceSomersaultEnabled = true;
        runtime.UpdateSomersault();
        int buttonLaunches = runtime._somersaultMotion.Launches;

        Program.PlayerTime = 20;
        Check(runtime.FilterSpaceSomersaultKey(first,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest is { FromSpace: true },
            "an enabled Space press is pending before the settled handoff");
        runtime._settings.SpaceSomersaultEnabled = false;
        runtime.UpdateSomersault();
        Check(!runtime._somersaultRequest.HasValue && runtime._somersaultMotion.Launches == buttonLaunches,
            "disabling the toggle between press and handoff cancels the keyboard-owned request");
        runtime._settings.SpaceSomersaultEnabled = true;

        Program.PlayerTime = 21;
        runtime.FilterSpaceSomersaultKey(first, new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space));
        runtime.CancelPendingSpaceSomersault();
        runtime.UpdateSomersault();
        Check(!runtime._somersaultRequest.HasValue,
            "focus-loss cancellation discards an unhanded keyboard intent");

        Program.PlayerTime = 22;
        runtime.FilterSpaceSomersaultKey(first, new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space));
        Program.ControlledVehicle = second;
        runtime.UpdateSomersault();
        Check(!runtime._somersaultRequest.HasValue && runtime._somersaultMotion.LastOwner != second,
            "an owner change drops the stale actor's Space intent");

        Program.ControlledVehicle = second;
        Program.PlayerTime = 23;
        Check(runtime.FilterSpaceSomersaultKey(second,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest is { FromSpace: true, Owner: var queuedOwner }
            && ReferenceEquals(queuedOwner, second),
            "an immediate Space press for a newly controlled actor captures that actor despite the stale runtime owner");
        runtime._owner = second;
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.LastOwner == second && runtime._somersaultMotion.Launches == buttonLaunches + 1,
            "settled pose rebind preserves and revalidates the new actor's fresh Space intent");

        runtime.ResetSomersault(); runtime.UpdateSomersault();
        Program.PlayerTime = 24;
        Check(runtime.FilterSpaceSomersaultKey(second,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest.HasValue,
            "a current actor can queue a fresh intent before a world change");
        Universe.CurrentSystem = new object();
        runtime.UpdateSomersault();
        Check(!runtime._somersaultRequest.HasValue && runtime._somersaultMotion.Launches == buttonLaunches + 1,
            "changing worlds cancels a queued actor's Space intent before handoff");

        second.System = Universe.CurrentSystem;
        runtime.ResetSomersault(); runtime.UpdateSomersault();
        Program.PlayerTime = 25;
        runtime.FilterSpaceSomersaultKey(second, new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space));
        runtime.ResetSomersault(); runtime._owner = null; runtime.UpdateSomersault();
        Check(!runtime._somersaultMotion.Active && !runtime._somersaultRequest.HasValue,
            "runtime reset cancels pending keyboard ownership");
    }

    private static void CheckSpaceDirection(SaberRuntime runtime, KittenEva owner, GlfwWindow window,
        GlfwKey[] held, SomersaultDirection expected)
    {
        runtime.ResetSomersault(); runtime.UpdateSomersault();
        foreach (GlfwKey key in new[] { GlfwKey.Up, GlfwKey.Down, GlfwKey.Left, GlfwKey.Right })
            window.SetKey(key, GlfwKeyAction.Release);
        foreach (GlfwKey key in held) window.SetKey(key, GlfwKeyAction.Press);
        Program.PlayerTime += .1;
        double pressed = Program.PlayerTime;
        Check(runtime.FilterSpaceSomersaultKey(owner,
                new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
            && runtime._somersaultRequest is { Direction: var direction, PressTime: var time }
            && direction == expected && time == pressed,
            $"Space snapshots arrow precedence as {expected}");
        runtime.UpdateSomersault();
        Check(runtime._somersaultMotion.Direction == expected,
            $"the settled motion uses the {expected} direction sampled on Space press");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
