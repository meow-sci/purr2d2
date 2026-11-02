using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Runtime;

// Native preparation/upload/isolation boundaries are stubbed; the manual trigger,
// probe, policy, gesture lifetime, rebase and two-update runtime routing are production.
internal sealed partial class SaberRuntime
{
    private KittenEva? _owner;
    private string? _fault;
    private bool OverlayVisible = true, SomersaultActive, ForceHolding, ForcePicking, _freshPoseThisTick = true, _selectingSlice;
    private bool _bfl9kUiEnabled = true;
    private bool BFL9KUiVisible => OverlayVisible && _bfl9kUiEnabled;
    private bool ClutterSliceWorkActive => false;
    private bool SaberHeldForActions => true;
    private readonly FakeBlade _blade = new();
    private readonly FakePose _pose = new();
    private readonly FakeRenderer _renderer = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeSettings _settings = new();
    private sealed class FakeSettings { internal float Volume = .6f; }
    private sealed class FakeAudio
    {
        internal int Slices, Swooshes;
        internal double3 Emitter, SwooshEmitter;
        internal float Volume, SwooshVolume;
        internal void PlaySlice(KittenEva owner, double3 emitter, float volume)
        { Slices++; Emitter = emitter; Volume = volume; }
        internal void PlayManualSwing(KittenEva owner, double3 emitter, float volume)
        { Swooshes++; SwooshEmitter = emitter; SwooshVolume = volume; }
    }
    private sealed class FakeRenderer
    {
        internal int Sparks;
        internal double3 LastPoint, LastNormal;
        internal void AddCutSparks(Part part, double3 point, double3 normal, double now)
        { Sparks++; LastPoint = point; LastNormal = normal; }
    }
    private readonly SaberOverlayDrag _overlayDrag = new();
    private readonly AssistedSliceSequence _sliceSequence = new();
    private readonly ArbitrarySlicer _slicer = new();
    private readonly PartSliceTargeting _targeting = new();
    private SliceTarget? _sliceTarget;
    private bool _sliceStarted;
    private string? _sliceError;
    private string _sliceStatus = "";
    private double _sliceMessageUntil, _playerTime;
    private sealed class FakeBlade
    { internal bool Equipped = true, TargetOn = true, ManualDeployment; internal float Deployment = 1; }
    private sealed class FakePose
    {
        internal bool Eligible = true, ClashHeld, HasFreshBodyFrame = true, BladeSegmentAvailable = true;
        internal float3 GripOffsetFromChestCm, BladeDirectionModel;
        internal double3 BladeEmitter = new(3, 4, 5);
        internal bool TryGetBladeSegment(double length, out double3 emitter, out double3 tip)
        { emitter = BladeEmitter; tip = BladeEmitter + new double3(0, length, 0); return BladeSegmentAvailable; }
    }
    private KittenEva? _overlayDragOwner;
    private FakePose? _overlayDragPose;
    private bool OverlayCanSwing => OverlayVisible && _owner != null && _fault == null
        && ReferenceEquals(_owner, Program.ControlledVehicle) && _blade.Equipped && _pose.Eligible
        && !_selectingSlice && (!_sliceSequence.Active || _sliceSequence.EndPresented);
    private float Length => _blade.Deployment;
    private void CancelAssistedSlice(string reason, bool manualAttemptCompleted = false)
    {
        bool finishManualAttempt = manualAttemptCompleted || _manualGesture.InFlight;
        _sliceSequence.Cancel(); _manualSliceActive = _manualIsolated = false;
        FinishManualContactAttempt(finishManualAttempt);
        _slicer.Cancel(); _sliceTarget = null; _sliceStarted = false; _sliceError = null; _sliceStatus = reason;
        _sliceMessageUntil = reason.Length > 0 ? _playerTime + 5 : 0;
    }
    private static SaberRuntime Scene(int misses = 0)
    {
        var runtime = new SaberRuntime { _owner = new() };
        var vessel = new Vehicle { Parent = runtime._owner.Parent };
        var part = new Part { Tree = vessel.Parts };
        var positions = new double3[(misses + 1) * 3];
        for (int i = 0; i < misses; i++) { positions[i * 3] = new(5, -1, -1); positions[i * 3 + 1] = new(5, 1, -1); positions[i * 3 + 2] = new(5, 0, 1); }
        positions[^3] = new(0, -.2, -.2); positions[^2] = new(0, .2, -.2); positions[^1] = new(0, 0, .2);
        part.Modules.Add(new MeshViewModule(new() { PositionsCompare = [positions] })); vessel.Parts.Parts.Add(part);
        Universe.CurrentSystem = new(); Universe.CurrentSystem.All.Items = [runtime._owner, vessel];
        KSA.Program.EditorFlag = false; KSA.Program.Camera = new();
        runtime._manualProbe.SoftBudgetMilliseconds = double.PositiveInfinity;
        runtime._targeting.SliceHandler = (target, _) => runtime._slicer.Commit(target);
        Program.ControlledVehicle = runtime._owner; Program.ModalOpen = Program.HasMouseInputLatch = false;
        Universe.SimulationSpeed = 1;
        return runtime;
    }
    private void Begin()
    {
        _overlayDrag.Begin(default, 10, default, new(0, 1, 0)); BeginManualSwing();
        _overlayDragOwner = _owner; _overlayDragPose = _pose; ApplyManualContactReset();
    }
    private void Tick(double x, double time, bool advancing = true, bool fresh = true, bool move = true, float? pointerX = null)
        => TickPose(new(x, -1, 0), new(x, 1, 0), time, advancing, fresh, move, pointerX);
    private void TickPose(double3 emitter, double3 tip, double time, bool advancing = true, bool fresh = true,
        bool move = true, float? pointerX = null)
    {
        _playerTime = time; _freshPoseThisTick = fresh;
        if (move) { MoveManualSwing(new(pointerX ?? (float)(time * 200), 0), (long)(time * 1000)); _overlayDrag.Advance(.02, out _, out _); }
        ApplyOverlaySwing(.02);
        UpdateManualSwingSwoosh((long)(time * 1000));
        if (fresh) PublishManualPose(emitter, tip);
        ApplyManualContactReset(); UpdateManualContact(advancing);
    }
    private static SaberRuntime SceneWithTransformedSubpart(out Part root, out double4x4 subpartTransform)
    {
        var runtime = new SaberRuntime { _owner = new() };
        var vessel = new Vehicle { Parent = runtime._owner.Parent };
        root = new Part { Tree = vessel.Parts };
        var subpart = new Part
        {
            PartParent = root,
            Scale = new(2, .5, -1.5),
            Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .61),
            PositionParentAsmb = new(1.2, -.7, .4)
        };
        subpartTransform = subpart.MatrixAsmb2VehicleAsmb;
        var mesh = new MeshReference
        {
            BoundingSphereRadius = 10,
            PositionsCompare = [[new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)]]
        };
        subpart.Modules.Add(new MeshViewModule(mesh)); root.SubParts.Add(subpart); vessel.Parts.Parts.Add(root);
        Universe.CurrentSystem = new(); Universe.CurrentSystem.All.Items = [runtime._owner, vessel];
        KSA.Program.EditorFlag = false; KSA.Program.Camera = new();
        runtime._manualProbe.SoftBudgetMilliseconds = double.PositiveInfinity;
        runtime._targeting.SliceHandler = (target, _) => runtime._slicer.Commit(target);
        Program.ControlledVehicle = runtime._owner; Program.ModalOpen = Program.HasMouseInputLatch = false;
        Universe.SimulationSpeed = 1;
        return runtime;
    }
    internal static void Run()
    {
        DebugTimings.Enabled = false;
        var r = Scene(); r.Tick(-.1, 0); r.Tick(.1, .02);
        Check(r._slicer.Begins == 0, "runtime idle poses cannot query or cut");
        r.Begin(); r.Tick(-.1, .1, move: false); r.Tick(.1, .12, move: false);
        Check(r._slicer.Begins == 0, "runtime ignition or held-but-unmoved SWING cannot cut");
        r = Scene(); r._pose.ClashHeld = true; r.Begin(); r.Tick(-.1, .0); r.Tick(.1, .02);
        Check(r._slicer.Begins == 0 && !r._manualProbe.Pending,
            "a clash hold blocks new contact sweeps and clears any pending probe");
        r = Scene(); r._blade.ManualDeployment = true; r._blade.Deployment = .5f; r.Begin();
        r.TickPose(new(-.1, -.25, 0), new(-.1, .25, 0), 0);
        r.TickPose(new(.1, -.25, 0), new(.1, .25, 0), .02);
        Check(r._manualSliceActive && r._slicer.Begins == 1,
            "a stable partial manual blade can make contact using its shorter rendered segment");
        r = Scene(); r.Begin(); r.Tick(-.1, 0); r.Tick(-.1, .01, advancing: false, fresh: false); r.Tick(.1, .02);
        Check(r._manualSliceActive && r._sliceStarted && r._slicer.Begins == 1 && r._renderer.Sparks == 1 && !r._sliceSequence.Active,
            "runtime manual contact starts exactly one async cut without assisted animation across a no-step handoff");
        Check(r._audio.Slices == 1 && r._audio.Emitter == new double3(.1, -1, 0) && r._audio.Volume == .6f,
            "a confirmed manual hit plays one slice cue at the rendered emitter with the configured volume");
        int advancesBeforeClash = r._slicer.Advances;
        r._pose.ClashHeld = true; r.Tick(.1, .025);
        Check(r._manualSliceActive && r._slicer.Advances > advancesBeforeClash,
            "an accepted manual slice keeps pumping its worker while a clash holds the pose");
        r._pose.ClashHeld = false;
        for (int i = 0; i < 5; i++) r.Tick(.1, .03 + i * .01, advancing: false, fresh: false);
        Check(r._manualSliceActive && r._slicer.Begins == 1 && r._slicer.Isolations == 0,
            "accepted worker spans repeated no-step/no-render handoffs without cancellation or native isolation");
        Check(r._audio.Slices == 1, "repeated asynchronous slice updates do not replay the hit cue");
        r.ReleaseManualSwing();
        for (int i = 0; i < 4; i++) r.ApplyOverlaySwing(.1);
        r.Tick(.1, .3); Check(r._manualSliceActive && !r._overlayDrag.Active, "actual overlay recovery and subsequent idle update preserve accepted transaction"); r._slicer.Ready = true; r.Tick(.1, .32);
        Check(r._manualSliceActive && r._slicer.Isolations == 1 && r._slicer.Commits == 0 && r._sliceTarget!.Vehicle == r._slicer.Isolation!.Owner,
            "release and completed return retain accepted work and rebase after isolation");
        r.Tick(.1, .33, advancing: false, fresh: false);
        Check(r._slicer.Commits == 0, "no-step handoff cannot commit a prepared isolated source");
        r.Tick(.1, .34);
        Check(!r._manualSliceActive && r._slicer.Commits == 1 && r._slicer.Begins == 1,
            "separate valid settled update commits once without forcing an auto-aim swing");
        Check(!r._manualPoseAvailable && !r._manualProbe.Pending && Math.Abs(r._manualGesture.CooldownUntil - .59) < 1e-9,
            "manual completion clears its cached pose and probe before starting the 250 ms cooldown");
        r.Tick(-.1, .36); r.Tick(.1, .38);
        Check(r._slicer.Begins == 1, "automatic return and released motion cannot recursively slice spawned debris");
        r.Begin(); r.Tick(-.1, .4); r.Tick(.1, .42);
        Check(r._slicer.Begins == 1 && !r._manualPoseAvailable, "a new press cannot bypass the active cooldown");
        double retryAt = r._manualGesture.CooldownUntil;
        r.Tick(-.1, retryAt, pointerX: -20); r.Tick(.1, retryAt + .02, pointerX: -20);
        Check(r._slicer.Begins == 2, "new gesture can cut after the cooldown and a fresh rendered pose");
        Check(r._audio.Slices == 2, "a successive accepted cut receives its own hit cue");
        r.InvalidateManualContact(); Check(r._manualSliceActive, "UI cancellation defers native worker cleanup to settled update");
        r.ApplyManualContactReset(); Check(!r._manualSliceActive && !r._manualProbe.Pending, "settled cancellation clears pending worker and contact history");
        r = Scene(); r.Begin(); r.Tick(-.1, .0); r.Tick(.1, .02);
        Check(r._manualSliceActive && r._slicer.Begins == 1, "first stroke starts the held-drag manual transaction");
        r._slicer.Ready = true; r.Tick(.1, .04); r.Tick(.1, .06);
        Check(!r._manualSliceActive && r._slicer.Commits == 1 && !r._manualProbe.Pending && !r._manualPoseAvailable,
            "first held-drag cut completes and discards its frozen contact source");
        r.Tick(-.1, .309);
        Check(r._slicer.Begins == 1, "held drag remains paused just before the rearm boundary");
        r.Tick(-.1, .31, pointerX: -20); r.Tick(.1, .33, pointerX: -20);
        Check(r._manualSliceActive && r._slicer.Begins == 2,
            "one held drag starts a second cut only after cooldown and a new contact sweep");
        r = Scene(); r.Begin(); r.Tick(-.1, 1); r.Tick(.1, 1.02);
        ulong firstGesture = r._manualGesture.Id;
        r.ReleaseManualSwing();
        for (int i = 0; i < 4; i++) r.ApplyOverlaySwing(.1);
        r.Begin(); ulong secondGesture = r._manualGesture.Id;
        r.Tick(-.1, 1.12); r.Tick(.1, 1.14);
        Check(r._manualSliceActive && r._slicer.Begins == 1 && secondGesture > firstGesture
            && r._manualGesture.Id == secondGesture && r._manualGesture.InFlight,
            "a new held press during old work keeps its monotonic identity without overlapping the slicer");
        r._slicer.Ready = true; r.Tick(.1, 1.16); r.Tick(.1, 1.18);
        Check(!r._manualSliceActive && r._manualGesture.Held && r._manualGesture.Id == secondGesture
            && Math.Abs(r._manualGesture.CooldownUntil - 1.43) < 1e-9,
            "finishing old work preserves the new held gesture and starts its cooldown");
        r = Scene(); r.Begin(); r.Tick(-.1, 2); r.Tick(.1, 2.02); r._slicer.Error = "preparation failed"; r.Tick(.1, 2.04);
        Check(!r._manualSliceActive && r._manualGesture.Held && !r._manualGesture.CanDetectAt(2.289)
            && r._manualGesture.CanDetectAt(r._manualGesture.CooldownUntil) && !r._manualProbe.Pending,
            "manual preparation errors clear cached contact and apply the same rearm cooldown");
        r = Scene(ManualContactProbe.TriangleBudget + 4); r.Begin(); r.Tick(-.1, 1); r.Tick(.1, 1.02);
        Check(r._manualProbe.Pending && r._slicer.Begins == 0, "runtime preserves a budgeted surface query before acceptance");
        r.Tick(.3, 1.03, fresh: false); r.Tick(.5, 1.04);
        Check(r._manualSliceActive && r._slicer.Begins == 1, "no-fresh handoff preserves query cursor and frozen endpoints until confirmation");
        r = Scene(); r.Begin(); r.Tick(-.1, 2); r.ReleaseManualSwing(); r.Tick(.1, 2.02);
        Check(r._slicer.Begins == 0 && !r._manualProbe.Pending, "release before confirmation discards detection and cannot cut during recovery");
        r = Scene(); r.Begin(); r.Tick(-.1, 3); r.Tick(.1, 3.02); r.ForceHolding = true; r.Tick(.1, 3.04);
        Check(!r._manualSliceActive && r._slicer.Commits == 0 && !r._manualGesture.Held,
            "Force capture conflict cancels contact and the held gesture without rearming");
        r = Scene(); r.Begin(); r.Tick(-.1, 4); r.Tick(.1, 4.02); r.OverlayVisible = false; r.Tick(.1, 4.04);
        Check(!r._manualSliceActive, "hidden controls cancel pending manual cut");
        r = Scene(); r.Begin(); r.Tick(-.1, 4); r.Tick(.1, 4.02); r._bfl9kUiEnabled = false; r.Tick(.1, 4.04);
        Check(r.OverlayVisible && !r._manualSliceActive && !r._manualGesture.Held,
            "hiding only the BFL9K UI cancels manual slicing while the other overlay controls stay visible");
        r = Scene(); r.Begin(); r.Tick(-.1, 5); r.Tick(.1, 5.02); r._blade.TargetOn = false; r.Tick(.1, 5.04);
        Check(!r._manualSliceActive, "retraction cancels pending manual cut");
        r = Scene(); r.Begin(); r._sliceSequence.Start(); r.Tick(-.1, 6); r.Tick(.1, 6.02);
        Check(r._slicer.Begins == 0, "assisted sequence owns the shared slicer and blocks manual requests");
        r = Scene(); r.Begin(); r.Tick(-.1, 7); r.Tick(.1, 7.02); r._sliceTarget!.Part.PositionParentAsmb = new(.2, 0, 0); r.Tick(.1, 7.04);
        Check(!r._manualSliceActive && r._slicer.Commits == 0, "changed source geometry cancels asynchronous manual completion");
        r = Scene(); r.Begin(); r.Tick(-.1, 8); r.Tick(.1, 8.02); r._owner!.IsDisposed = true; r.Tick(.1, 8.04);
        Check(!r._manualSliceActive, "disposed or boarded owner cannot retain an accepted transaction");
        r = SceneWithTransformedSubpart(out var contactRoot, out var contactTransform);
        r.Begin();
        var base0 = new double3(-.1, -.5, -.05).Transform(contactTransform);
        var tip0 = new double3(-.1, .5, .15).Transform(contactTransform);
        var base1 = new double3(.1, -.4, .05).Transform(contactTransform);
        var tip1 = new double3(.1, .6, .25).Transform(contactTransform);
        r.TickPose(base0, tip0, 9.1, pointerX: 20);
        r.TickPose(base1, tip1, 9.12, pointerX: 24);
        Check(r._slicer.Begins == 1 && r._slicer.LastPart == contactRoot && r._slicer.LastContact.HasValue,
            "manual runtime sends its confirmed subpart hit to the slicer as a contact cut");
        var forwarded = r._slicer.LastContact!.Value;
        var assemblyHit = r._renderer.LastPoint.Transform(contactTransform);
        var forwardedHit = new double3(forwarded.Point.X, forwarded.Point.Y, forwarded.Point.Z);
        var expectedBlade = double3.TransformNormal(new(0, 1, .2), contactTransform).NormalizeOrZero();
        var expectedMotion = double3.TransformNormal(new(.2, .1, .1), contactTransform).NormalizeOrZero();
        var expectedNormal = double3.NormalizeOrZero(double3.Cross(expectedBlade, expectedMotion));
        var forwardedNormal = double3.NormalizeOrZero(new(forwarded.Normal.X, forwarded.Normal.Y, forwarded.Normal.Z));
        Check((forwardedHit - assemblyHit).Length() < 1e-8,
            "manual slicer receives the actual hit point transformed from the rotated reflected subpart");
        Check(Math.Abs(double3.Dot(forwardedNormal, expectedNormal)) > .99999,
            "manual cut normal comes from blade and motion vectors after nonuniform reflected assembly transforms");
        r = Scene(); r.Begin(); var vessel = (Vehicle)Universe.CurrentSystem!.All.Items[1]; vessel.HasPhysicsBubble = false;
        r.Tick(-.1, 9, pointerX: -20); r.Tick(.1, 9.02, pointerX: -20); string refusal = r._sliceStatus;
        r.Tick(-.1, 9.04, pointerX: -20); r.Tick(.1, 9.06, pointerX: -20);
        double refusedUntil = r._manualGesture.CooldownUntil;
        Check(r._slicer.Begins == 0 && r._renderer.Sparks == 1 && r._sliceStatus == refusal
            && !r._manualGesture.CanDetectAt(refusedUntil - .001) && r._manualGesture.CanDetectAt(refusedUntil)
            && r._sliceMessageUntil > 9.02 && !r._manualProbe.Pending && !r._manualPoseAvailable,
            "refused physical contact stays cosmetic, clears its sweep, and throttles retry for exactly 250 ms");
        Check(r._audio.Slices == 0, "a cosmetic or refused contact plays no slice cue");
        r = Scene(); r.SomersaultActive = true; r.Begin(); r.Tick(-.1, 9.5); r.Tick(.1, 9.52);
        Check(r.SomersaultActive && r._manualSliceActive && r._slicer.Begins == 1,
            "somersault motion permits a live fresh manual blade contact");
        int advancesDuringJump = r._slicer.Advances;
        r.Tick(.1, 9.54, fresh: false);
        Check(r._manualSliceActive && r._slicer.Advances > advancesDuringJump && r._slicer.Isolations == 0,
            "accepted contact keeps preparing during somersault while stale poses still cannot mutate native geometry");
        r = Scene(); r.SomersaultActive = true; r.Begin(); r.Tick(-.1, 9.7); r.Tick(.1, 9.9, fresh: false);
        Check(r._slicer.Begins == 0 && !r._manualProbe.Pending,
            "somersault cannot bypass the maximum age of a rendered blade sample");
        foreach (bool moving in new[] { false, true })
        {
            r = Scene(); r._owner!.LocomotionState.Mode = LocomotionMode.Swimming;
            r._owner.LocomotionState.GroundSpeed = moving ? 4 : 0;
            r._owner.LocomotionState.SwimParked = !moving;
            r._owner.PositionEgo = new(0, 0, -20);
            r._owner.HasPhysicsBubble = moving;
            var submergedTarget = (Vehicle)Universe.CurrentSystem!.All.Items[1];
            submergedTarget.PositionEgo = r._owner.PositionEgo;
            r.Begin(); r.Tick(-.1, 9.91); r.Tick(.1, 9.93);
            Check(r._manualSliceActive && r._slicer.Begins == 1 && r._blade.TargetOn,
                moving ? "a moving submerged swimmer can swing a live blade through an underwater target"
                    : "a parked swimmer keeps valid rendered manual contacts without requiring its own physics bubble");
        }
        r = Scene(); r._owner!.LocomotionState.Mode = LocomotionMode.Swimming; r.Begin();
        r.Tick(-.1, 9.94); r.Tick(.1, 10.2, fresh: false);
        Check(r._slicer.Begins == 0 && !r._manualProbe.Pending,
            "swimming cannot bypass stale rendered-frame contact refusal");
        foreach (var gate in new (string Name, Action<SaberRuntime> Apply)[] {
            ("fault", x => x._fault = "test"),
            ("Force picker", x => x.ForcePicking = true), ("slice picker", x => x._selectingSlice = true),
            ("ineligible pose", x => x._pose.Eligible = false) })
        {
            r = Scene(); r.Begin(); r.Tick(-.1, 10); gate.Apply(r); r.Tick(.1, 10.02);
            Check(r._slicer.Begins == 0, $"runtime {gate.Name} gate blocks contact detection");
        }
        SwooshRuntimeChecks();
    }

    private static void SwooshRuntimeChecks()
    {
        var r = Scene(); r.Begin();
        r.UpdateManualSwingSwoosh(0);
        r.MoveManualSwing(new(2, 0), 1); r.UpdateManualSwingSwoosh(1);
        Check(r._audio.Swooshes == 0, "manual swoosh runtime stays silent on press and sub-threshold motion");
        r.MoveManualSwing(new(4, 0), 2); r.MoveManualSwing(new(4, 0), 3);
        r.UpdateManualSwingSwoosh(3);
        Check(r._audio.Swooshes == 1 && r._audio.SwooshEmitter == r._pose.BladeEmitter
            && r._audio.SwooshVolume == r._settings.Volume,
            "duplicate UI/native samples produce one settled swoosh at the fresh emitter and configured volume");
        r.MoveManualSwing(new(4, 0), 4); r.UpdateManualSwingSwoosh(4);
        Check(r._audio.Swooshes == 1, "stationary held manual input does not retrigger the sound");

        r = Scene(); r.Begin(); r.MoveManualSwing(new(4, 0), 10); r._pose.ClashHeld = true;
        r.UpdateManualSwingSwoosh(11); r._pose.ClashHeld = false; r.UpdateManualSwingSwoosh(12);
        Check(r._audio.Swooshes == 0, "clash-final-frame movement is discarded and cannot sound after clash release");
        r.MoveManualSwing(new(6, 0), 13); r.UpdateManualSwingSwoosh(13);
        Check(r._audio.Swooshes == 0, "post-clash motion must cross a fresh threshold");
        r.MoveManualSwing(new(8, 0), 14); r.UpdateManualSwingSwoosh(14);
        Check(r._audio.Swooshes == 1, "fresh held movement resumes the swoosh after a clash");

        r = Scene(); r.Begin(); r._pose.ClashHeld = true;
        r.MoveManualSwing(new(4, 0), 15); r._pose.ClashHeld = false;
        r.UpdateManualSwingSwoosh(15);
        Check(r._audio.Swooshes == 0, "movement sampled during clash is discarded even when the clash ends before handoff");
        r.MoveManualSwing(new(6, 0), 16); r.UpdateManualSwingSwoosh(16);
        Check(r._audio.Swooshes == 0, "same-handoff clash recovery still requires four fresh pixels");
        r.MoveManualSwing(new(8, 0), 17); r.UpdateManualSwingSwoosh(17);
        Check(r._audio.Swooshes == 1, "fresh motion after same-handoff clash recovery produces one cue");

        r = Scene(); r.Begin(); r.MoveManualSwing(new(4, 0), 20); Universe.SimulationSpeed = 0;
        r.UpdateManualSwingSwoosh(21); Universe.SimulationSpeed = 1; r.UpdateManualSwingSwoosh(22);
        Check(r._audio.Swooshes == 0, "pause discards a pending sample rather than replaying it on resume");
        r.MoveManualSwing(new(8, 0), 23); r.UpdateManualSwingSwoosh(23);
        Check(r._audio.Swooshes == 1, "fresh motion after pause can trigger once supported speed resumes");

        foreach (var block in new (string Name, Action<SaberRuntime> Apply, Action<SaberRuntime> Clear)[] {
            ("warp", _ => Universe.SimulationSpeed = 6, _ => Universe.SimulationSpeed = 1),
            ("modal", _ => Program.ModalOpen = true, _ => Program.ModalOpen = false),
            ("mouse latch", _ => Program.HasMouseInputLatch = true, _ => Program.HasMouseInputLatch = false),
            ("stale pose", x => x._pose.HasFreshBodyFrame = false, x => x._pose.HasFreshBodyFrame = true),
            ("missing blade segment", x => x._pose.BladeSegmentAvailable = false, x => x._pose.BladeSegmentAvailable = true),
            ("hidden controls", x => x.OverlayVisible = false, x => x.OverlayVisible = true),
            ("owner switch", _ => Program.ControlledVehicle = new KittenEva(), x => Program.ControlledVehicle = x._owner),
        })
        {
            r = Scene(); r.Begin(); r.MoveManualSwing(new(4, 0), 100);
            block.Apply(r); r.UpdateManualSwingSwoosh(101); block.Clear(r);
            r.UpdateManualSwingSwoosh(102);
            Check(r._audio.Swooshes == 0, $"runtime {block.Name} invalidation drops pending motion");
            r.MoveManualSwing(new(8, 0), 103); r.UpdateManualSwingSwoosh(103);
            Check(r._audio.Swooshes == 1, $"runtime {block.Name} recovery accepts fresh movement");
        }

        r = Scene(); r.Begin(); r.MoveManualSwing(new(4, 0), 200); r._freshPoseThisTick = false;
        r.UpdateManualSwingSwoosh(201); r._freshPoseThisTick = true;
        Check(r._audio.Swooshes == 0, "a non-fresh settled handoff cannot emit a swoosh");
        r.MoveManualSwing(new(8, 0), 202); r.UpdateManualSwingSwoosh(202);
        Check(r._audio.Swooshes == 1, "fresh rendered pose and fresh movement are both required at consumption");

        r = Scene(); r.Begin(); r.MoveManualSwing(new(4, 0), 300);
        r.ReleaseManualSwing(); r.UpdateManualSwingSwoosh(301);
        r._overlayDrag.Advance(.1, out _, out _); r.UpdateManualSwingSwoosh(302);
        Check(r._audio.Swooshes == 0,
            "release and automatic pose return never trigger a manual swoosh without a held mouse gesture");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
