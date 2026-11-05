using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using Purr2D2.Slicing.Planes;

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
    private sealed class FakeSettings
    {
        internal float Volume = .6f;
        internal bool PassiveContactCutsEnabled;
        internal string EffectiveContactPlaneStrategy = "legacy-contact";
        internal string SliceExecutionMode = "legacy-serial", VesselContactDetector = "legacy-ribbon";
        internal int SliceWorkerCount = 2, SlicePendingTargetLimit = 128, SliceWorkingMemoryMiB = 256;
    }
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
    private ClutterSliceQueue? _clutterQueue = null;
    private int _clutterQueueWorkers = 2, _clutterQueueLimit = 128, _clutterQueueMemory = 256;
    private bool _cancelQueuedClutterRequested = false;
    // Replay capture is exercised in its isolated writer project; the manual runtime
    // fixture keeps only the production detection/queue transaction boundaries.
    private void OfferSliceReplayContact(ManualContactProbe.Contact contact, string detector) { }
    private void AdvanceSliceReplayCapture() { }
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
        r = Scene(); r._settings.PassiveContactCutsEnabled = true;
        r.Tick(-.1, 0, move: false); r.Tick(.1, .02, move: false);
        Check(r._manualSliceActive && r._slicer.Begins == 1, "opt-in passive blade motion can contact a vessel without a drag");
        r._settings.PassiveContactCutsEnabled = false; r.Tick(.1, .03, move: false);
        Check(r._manualSliceActive, "turning passive off retains an accepted cut");
        r = Scene(); r.Tick(-.1, 0, move: false); r._settings.PassiveContactCutsEnabled = true;
        r.Tick(.1, .02, move: false);
        Check(r._slicer.Begins == 0, "enabling passive detection cannot sweep across the disabled interval");
        r.Tick(-.1, .04, move: false);
        Check(r._slicer.Begins == 1, "passive detection starts after a fresh sample pair");
        r = Scene(); r._settings.PassiveContactCutsEnabled = true; r.Begin();
        r.Tick(-.1, 0, move: false); r.Tick(.1, .02, move: false);
        Check(r._slicer.Begins == 0, "stationary SWING takes precedence over passive motion");
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
        QueueSharedLaneChecks();
        QueueRetiredUploadLane();
        QueueSplitVehicleLease();
        QueueReadyValidationPriority();
        QueueProvenanceSurvivesReplacement();
        FinalRenderedReleaseChecks();
        SwooshRuntimeChecks();
    }

    // Real frames publish after the handoff; a GLFW release is polled before the
    // next handoff. Keep these phases separate so tests exercise that final pose.
    private void RenderHeldContactPose(double x = .1, double time = 1.02)
    {
        _playerTime = time;
        MoveManualSwing(new(24, 0), (long)(time * 1000));
        ApplyOverlaySwing(.02);
        PublishManualPose(new(x, -1, 0), new(x, 1, 0));
    }

    private void ReleaseContactHandoff(double time = 1.03, bool advancing = true, bool fresh = true)
    {
        _playerTime = time; _freshPoseThisTick = fresh;
        ApplyManualContactReset(); ApplyOverlaySwing(.02); UpdateManualContact(advancing);
    }

    private static SaberRuntime ReleasedScene(bool queued, string detector, int misses = 0)
    {
        var r = Scene(misses);
        r._settings.VesselContactDetector = detector;
        if (queued) { r._settings.SliceExecutionMode = "queued"; r._clutterQueue = new(); }
        r.Begin(); r.Tick(-.1, 1);
        r.RenderHeldContactPose(); r.ReleaseManualSwing();
        return r;
    }

    private static void FinalRenderedReleaseChecks()
    {
        foreach (bool queued in new[] { false, true })
        foreach (string detector in new[] { "legacy-ribbon", "cached-ribbon" })
        {
            string mode = (queued ? "queued " : "serial ") + detector;
            bool Started(SaberRuntime r) => queued ? r._vesselQueue is { Count: > 0 } : r._slicer.Begins > 0;
            var r = ReleasedScene(queued, detector);
            Check(r._manualReleasedPosePending && !r._manualGesture.Held,
                mode + " release retains only its unconsumed rendered held pose");
            r.ReleaseContactHandoff();
            Check(Started(r) && r._audio.Slices == 1 && r._audio.Emitter == new double3(.1, -1, 0)
                && !r._manualReleasedPosePending && r._sliceActivation.Kind.ToString() == "ManualContact",
                mode + " final rendered crossing starts one manual cut after ordinary release");
            r.ReleaseContactHandoff(1.04);
            Check(r._audio.Slices == 1, mode + " final rendered event cannot be submitted twice");

            r = Scene();
            r._settings.VesselContactDetector = detector;
            if (queued) { r._settings.SliceExecutionMode = "queued"; r._clutterQueue = new(); }
            r.Begin(); r.Tick(-.1, 1); r.ReleaseManualSwing();
            r.ReleaseContactHandoff(); r.Tick(.1, 1.04);
            Check(!Started(r) && !r._manualProbe.Pending,
                mode + " an already consumed pose and later automatic return cannot create a released cut");

            foreach (var gate in new (string Name, Action<SaberRuntime> Apply)[] {
                ("cancel", x => x.InvalidateManualContact()),
                ("hidden controls", x => x._bfl9kUiEnabled = false),
                ("ineligible pose", x => x._pose.Eligible = false),
                ("stale body pose", x => x._pose.HasFreshBodyFrame = false),
                ("clash", x => x._pose.ClashHeld = true),
                ("Force hold", x => x.ForceHolding = true),
                ("busy slicer", x => x._slicer.Busy = true),
                ("cooldown", x => x._manualGesture.FinishAttempt(1.02)),
                ("changed owner", x => x._owner = new()),
                ("changed controls", x => Program.ControlledVehicle = new KittenEva()),
                ("new gesture", x => x.Begin()),
                ("paused", x => Universe.SimulationSpeed = 0),
                ("unsupported warp", x => Universe.SimulationSpeed = 6),
                ("modal", x => Program.ModalOpen = true),
                ("mouse latch", x => Program.HasMouseInputLatch = true),
                ("changed source", x => ((Vehicle)Universe.CurrentSystem!.All.Items[1]).Parts.Parts[0].PositionParentAsmb = new(.02, 0, 0)) })
            {
                r = ReleasedScene(queued, detector); gate.Apply(r); r.ReleaseContactHandoff();
                Check(!Started(r) && !r._manualReleasedPosePending,
                    mode + " " + gate.Name + " refuses the final release event");
            }

            foreach (var gap in new[] { "no step", "no fresh pose", "expired pose" })
            {
                r = ReleasedScene(queued, detector);
                r.ReleaseContactHandoff(gap == "expired pose" ? 1.2 : 1.03,
                    advancing: gap != "no step", fresh: gap != "no fresh pose");
                r.ReleaseContactHandoff(1.21);
                Check(!Started(r) && !r._manualProbe.Pending && !r._manualReleasedPosePending,
                    mode + " " + gap + " discards the final event without replaying it later");
            }

            r = ReleasedScene(queued, detector); r._settings.PassiveContactCutsEnabled = true;
            // A later passive render cannot replace the held event selected at release.
            r._playerTime = 1.025; r.PublishManualPose(new(-.3, -1, 0), new(-.3, 1, 0));
            r.ReleaseContactHandoff();
            Check(Started(r) && r._audio.Emitter == new double3(.1, -1, 0)
                && r._sliceActivation.Kind.ToString() == "ManualContact",
                mode + " passive authorization cannot relabel or overwrite the final manual event");

            r = ReleasedScene(queued, detector, ManualContactProbe.TriangleBudget + 4);
            r.ReleaseContactHandoff();
            if (queued)
            {
                Check(!Started(r) && r._manualProbe.Pending && r._manualDrainPending,
                    mode + " final dense query defers under the existing shared work budget");
                r.ReleaseContactHandoff(1.04, fresh: false);
                Check(Started(r) && !r._manualProbe.Pending,
                    mode + " deferred final query drains its frozen held interval exactly once");
            }
            else
            {
                Check(!Started(r) && !r._manualProbe.Pending,
                    mode + " final dense query retains serial release cancellation at its work limit");
                r = Scene(ManualContactProbe.TriangleBudget + 4);
                r._settings.VesselContactDetector = detector;
                r.Begin(); r.Tick(-.1, 1); r.Tick(.1, 1.02);
                Check(r._manualProbe.Pending, mode + " earlier held query is deferred before release");
                r.RenderHeldContactPose(.3, 1.03); r.ReleaseManualSwing(); r.ReleaseContactHandoff(1.04);
                Check(!Started(r) && !r._manualProbe.Pending,
                    mode + " unconsumed final pose cannot revive earlier serial work cancelled by release");
            }
        }
    }

    private static (Part Part, SliceTarget Target, MeshReference Mesh) QueueTarget(KittenEva actor, Vehicle? vehicle = null)
    {
        vehicle ??= new Vehicle { Parent = actor.Parent };
        var part = new Part { Tree = vehicle.Parts };
        var mesh = new MeshReference { PositionsCompare = [[new(0, 0, 0), new(0, 1, 0), new(0, 0, 1)]] };
        part.Modules.Add(new MeshViewModule(mesh)); vehicle.Parts.Parts.Add(part);
        return (part, new(part, vehicle, default, part.MatrixAsmb2VehicleAsmb), mesh);
    }
    private static void Enqueue(VesselSliceQueue queue, KittenEva actor, (Part Part, SliceTarget Target, MeshReference Mesh) item)
    {
        var request = new SlicePlaneRequest("legacy-contact", "ManualContact", "Vessel", default, new(0, 1, 0), new(1, 0, 0));
        Check(queue.Begin(actor, item.Target, request, 0, 1, 0, item.Mesh, item.Mesh.PositionsCompare, () => true), "vessel test intent admitted");
    }
    private static void TickQueue(VesselSliceQueue queue, KittenEva actor, bool commit = false) =>
        queue.Update(actor, commit, 1, true, true, 0, 0, 0, 0);

    private static void QueueRetiredUploadLane()
    {
        var scene = Scene(); var actor = scene._owner!; ArbitrarySlicer.ResetQueueFixture();
        using var queue = new VesselSliceQueue(TaskScheduler.Default, TaskScheduler.Default, 2, 16, 64);
        var first = QueueTarget(actor); var second = QueueTarget(actor);
        Enqueue(queue, actor, first); Enqueue(queue, actor, second);
        for (int i = 0; i < 80 && (ArbitrarySlicer.CaptureOperations < 2 || ArbitrarySlicer.UploadOperations < 1); i++) TickQueue(queue, actor);
        Check(ArbitrarySlicer.CaptureOperations == 2 && ArbitrarySlicer.UploadOperations == 1, "two vessel sources reach one shared upload lane");
        first.Part.PositionParentAsmb = new(1, 0, 0); // stale source cancels the first upload
        TickQueue(queue, actor);
        TickQueue(queue, actor);
        Check(ArbitrarySlicer.UploadRetirements == 1 && ArbitrarySlicer.UploadOperations == 1,
            "retiring a cancelled vessel upload consumes the entire handoff upload lane");
        TickQueue(queue, actor);
        Check(ArbitrarySlicer.UploadOperations == 2, "the next vessel upload starts on a later handoff");
    }

    private static void QueueSplitVehicleLease()
    {
        var scene = Scene(); var actor = scene._owner!; ArbitrarySlicer.ResetQueueFixture();
        using var queue = new VesselSliceQueue(TaskScheduler.Default, TaskScheduler.Default, 2, 16, 64);
        var owner = new Vehicle { Parent = actor.Parent };
        var first = QueueTarget(actor, owner); var pending = QueueTarget(actor, owner);
        Enqueue(queue, actor, first); Enqueue(queue, actor, pending);
        ArbitrarySlicer.NextIsolationMovedPart = pending.Part;
        for (int i = 0; i < 80 && ArbitrarySlicer.LastIsolationNeighbor == null; i++) TickQueue(queue, actor, true);
        Check(ArbitrarySlicer.LastIsolationNeighbor != null, "coordinator isolation rebases a pending sibling onto a neighbor vehicle");
        var later = QueueTarget(actor, ArbitrarySlicer.LastIsolationNeighbor);
        Enqueue(queue, actor, later);
        for (int i = 0; i < 80 && ArbitrarySlicer.CommitOperations == 0; i++) TickQueue(queue, actor, true);
        Check(ArbitrarySlicer.CommitOperations == 1, "the first isolated source commits before its siblings");
        for (int i = 0; i < 80 && ArbitrarySlicer.Instances < 2; i++) TickQueue(queue, actor);
        Check(ArbitrarySlicer.Instances == 2 && ReferenceEquals(ArbitrarySlicer.LastInstance?.LastPart, pending.Part),
            "the original queued sibling acquires the new vehicle lease first");
        for (int i = 0; i < 20; i++) TickQueue(queue, actor);
        Check(ArbitrarySlicer.Instances == 2, "a later intent cannot capture concurrently on that same rebased vehicle");
    }

    private static void QueueReadyValidationPriority()
    {
        var scene = Scene(); var actor = scene._owner!; ArbitrarySlicer.ResetQueueFixture();
        using var queue = new VesselSliceQueue(TaskScheduler.Default, TaskScheduler.Default, 2, 16, 64);
        var first = QueueTarget(actor); Enqueue(queue, actor, first);
        for (int i = 0; i < 40 && ArbitrarySlicer.CaptureOperations == 0; i++)
            queue.Update(actor, false, 1, true, false, 0, 0, 0, 0);
        for (int i = 0; i < 10 && ArbitrarySlicer.UploadOperations == 0; i++)
            queue.Update(actor, false, 1, false, true, 0, 0, 0, 0);
        Check(queue.WantsCapture && !queue.WantsCommit, "ready source awaits incremental validation");
        Enqueue(queue, actor, QueueTarget(actor));
        queue.Update(actor, false, 1, true, false, 0, 0, 0, 0);
        Check(queue.WantsCommit && ArbitrarySlicer.Instances == 1,
            "ready source validation takes priority over a newly queued source");
    }

    private static void QueueProvenanceSurvivesReplacement()
    {
        var scene = Scene(); var actor = scene._owner!; var item = QueueTarget(actor);
        var provenance = new SliceStrokeProvenance<Part>(); provenance.Observe(44);
        using (var first = new VesselSliceQueue(TaskScheduler.Default, TaskScheduler.Default, 2, 16, 64, provenance))
            first.MarkGenerated(item.Part, 44);
        using var replacement = new VesselSliceQueue(TaskScheduler.Default, TaskScheduler.Default, 2, 16, 64, provenance);
        var request = new SlicePlaneRequest("legacy-contact", "ManualContact", "Vessel", default, new(0, 1, 0), new(1, 0, 0));
        Check(!replacement.Begin(actor, item.Target, request, 0, 44, 0, item.Mesh, item.Mesh.PositionsCompare, () => true),
            "queue replacement retains same-stroke fragment suppression");
        provenance.Observe(45);
        Check(replacement.Begin(actor, item.Target, request, 0, 45, 0, item.Mesh, item.Mesh.PositionsCompare, () => true),
            "a later stroke can cut the fragment after queue replacement");
    }

    private static void QueueSharedLaneChecks()
    {
        var r = Scene();
        r._settings.SliceExecutionMode = "queued";
        r._clutterQueue = new ClutterSliceQueue { Active = true, ReservationCount = 2, LoadSlots = 2 };
        r.Begin(); r.Tick(-.1, 0); r.Tick(.1, .02);
        Check(r._vesselQueue is { Count: 1 }, $"queued manual contact enters the production vessel queue alongside clutter work (count {r._vesselQueue?.Count ?? -1}, status '{r._sliceStatus}', audio {r._audio.Slices})");

        ArbitrarySlicer.ResetQueueFixture();
        double now = .03;
        for (int i = 0; i < 40 && (ArbitrarySlicer.LastInstance == null || !ArbitrarySlicer.LastInstance.EstimateDone); i++, now += .01)
            r.Tick(.1, now, move: false);
        var slicer = ArbitrarySlicer.LastInstance;
        Check(slicer != null && slicer.EstimateDone && r._vesselQueue is { ReservationCount: 0 }
            && ArbitrarySlicer.CaptureOperations == 0,
            $"vessel capture waits while the shared clutter reservation budget is full (slicer {slicer != null}, estimate {slicer?.EstimateDone}, reservations {r._vesselQueue?.ReservationCount}, captures {ArbitrarySlicer.CaptureOperations}, queue '{r._vesselQueue?.Status}', now {now:F2})");

        r._clutterQueue.ReservationCount = 0;
        for (int i = 0; i < 20 && ArbitrarySlicer.CaptureOperations == 0; i++, now += .01)
            r.Tick(.1, now, move: false);
        Check(ArbitrarySlicer.CaptureOperations == 1 && ArbitrarySlicer.LastAdvanceCapture
            && !ArbitrarySlicer.LastAdvanceLoadSlot && slicer is { HoldsLoadSlot: false },
            "vessel source capture proceeds when a reservation frees but respects clutter's saturated loader slots");

        r._clutterQueue.HasUpload = true;
        r.Tick(.1, now += .01, move: false);
        Check(!r._vesselUploadLane && !ArbitrarySlicer.LastAdvanceUpload && ArbitrarySlicer.UploadOperations == 0,
            "clutter's active upload prevents a simultaneous vessel upload");

        r._clutterQueue.HasUpload = false;
        r._clutterQueue.LoadSlots = 0;
        r.Tick(.1, now += .01, move: false);
        Check(r._vesselUploadLane && ArbitrarySlicer.LastAdvanceUpload && ArbitrarySlicer.UploadOperations == 1
            && ArbitrarySlicer.LastAdvanceLoadSlot && slicer is { HoldsLoadSlot: true },
            "vessel upload and loader admission resume after clutter releases their shared lanes");

        Check(r._vesselQueue is { WantsCapture: true },
            "a ready vessel remains capture-lane eligible until its source signature is revalidated");
        r._clutterQueue.Active = false;
        r.Tick(.1, now += .01, advancing: false, move: false);
        Check(r._vesselCaptureLane && r._vesselQueue is { WantsCommit: true } && !r._vesselCommitLane,
            "ready-but-unverified vessel work uses the capture lane for revalidation while no-step keeps commit closed");
        r.Tick(.1, now += .01, advancing: true, move: false);
        Check(r._vesselCommitLane && slicer is { Isolations: 1, Commits: 0 }
            && r._vesselQueue is { WantsCapture: true },
            "a settled vessel handoff isolates once, then returns to capture-lane source verification before commit");
        r.Tick(.1, now += .01, advancing: false, move: false);
        Check(r._vesselCaptureLane && !r._vesselCommitLane && r._vesselQueue is { WantsCommit: true },
            "post-isolation verification preserves the no-step commit barrier");
        r.Tick(.1, now += .01, advancing: true, move: false);
        Check(r._vesselCommitLane && ArbitrarySlicer.CommitOperations == 1,
            "vessel replacement commits on a later permitted settled handoff");
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
