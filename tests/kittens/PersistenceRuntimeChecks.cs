using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Capes;
using Purr2D2.Core;
using Purr2D2.Pose;
using RenderCore;
using RenderCore.Animation;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private enum Command { CancelSlice }
    // A narrow fixture for the other runtime partials. Ownership/queue/pruning and
    // all avatar binding/publication code under test come from production sources.
    private BladeState _blade;
    private SaberPoseController _pose;
    private SaberAudio _audio;
    private KittenEva? _owner;
    private readonly FakeRenderer _renderer = new();
    private readonly Resettable _targeting = new(), _contactProbe = new();
    private readonly AssistedSliceSequence _sliceSequence = new();
    private bool SliceWorkActive => _sliceSequence.Active;
    private readonly SaberSettings _settings = new();
    private readonly SaberOverlayDrag _overlayDrag = new();
    private bool _ready = true, _enabled = true, _disposed, _selectingSlice, _freshPoseThisTick, _hasPreviousTip;
    private string? _fault;
    private float _swingSpeed;
    private ulong _consumedPoseGeneration;
    private KittenEva? _resetGestureOwner;
    private KittenEva? _saberDeploymentDragOwner;
    private KittenEva? _autoSwingDragOwner;
    private KittenEva? _kittenColorPickerOwner;
    private void DismissKittenColorPicker() => _kittenColorPickerOwner = null;
    private void CancelSaberDeploymentDrag() => _saberDeploymentDragOwner = null;
    private void CancelSaberAutoSwingDrag() => _autoSwingDragOwner = null;
    private void RemoveClashOwner(KittenEva owner)
    {
        if (_kittenSabers.TryGetValue(owner, out var saber)) saber.Pose.ClearClash();
    }
    private static void Report(string context, Exception error) { }

    private SaberRuntime()
    {
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _disposed = false; _fault = null; _selectingSlice = false;
    }
    private void CancelAssistedSlice(string reason) { _sliceSequence.Cancel(); _selectingSlice = false; _pose.ClearSliceSwing(); }
    private void ResetSomersault() { }
    private void ResetOverlaySwing() { _overlayDrag.Reset(); _resetGestureOwner = _pose.Kitten; }
    private void Queue(Command command) { }
    private void DrawSaber(KittenEva owner, IViewport viewport, float4x4 emitter)
    {
        if (!_kittenSabers.TryGetValue(owner, out var saber)) return;
        saber.PreviousTipTime = Program.FrameNumber;
        saber.PreviousTipBody = double3.Unpack(emitter.Translation);
    }
    private static void Cleanup(Action action, string component) => action();
    private sealed class Resettable { internal void Reset() { } }
    private sealed class FakeRenderer
    {
        internal readonly HashSet<KittenEva> Emitters = [];
        internal readonly Dictionary<KittenEva, float3> Colors = [];
        internal void RemoveEmitter(KittenEva owner) => Emitters.Remove(owner);
        internal void SetEmitter(KittenEva owner, double4x4 frame, float length, float3 color)
        {
            Emitters.Add(owner);
            Colors[owner] = color;
        }
    }

    public static void Main()
    {
        var runtime = new SaberRuntime();
        var system = new CelestialSystem();
        Universe.CurrentSystem = system;
        Universe.KittenRoster = new();
        var roster = new KittenRosterEntryData("Leia", "Stock");
        Universe.KittenRoster.Kittens.Add(roster);
        var leia = new KittenEva("Leia");
        var han = new KittenEva("Han");
        system.All.Items = [leia, han];
        runtime.SceneSaves.ObserveSystem(system);
        runtime.RefreshOverlayKittens();
        runtime.SetKittenSaberColor(leia, new(.3f, .7f, .2f));
        runtime.SetKittenSaberType(leia, SaberType.Dark);
        runtime.SetKittenCapeEnabled(leia, true);
        runtime.SetKittenSaberLength(leia, 2.8f);
        runtime.QueueKittenSaber(leia, true);
        runtime.CaptureKittenChoices();
        Check(!runtime._hasPreviousTip && runtime._swingSpeed == 0, "restoration starts without historical blade contact velocity");
        var captured = runtime.SceneSaves.Profiles.Capture();
        var savedLeia = captured.Kittens.Single(item => item.Identity.Id == "Leia");
        Check(savedLeia.Identity.Kind == "roster" && Read(savedLeia).SaberEnabled
            && Read(savedLeia).BladeLengthOverride == 2.8f,
            "pending enable and all appearance choices capture before avatar mutation");
        Check(leia.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "capturing queued intent does not execute the pose or allocate cape resources");

        runtime.ApplyKittenSaberRequests();
        roster.AssignedVehicleId = "Falcon";
        leia.IsDisposed = true;
        system.All.Items = [han];
        runtime.RefreshOverlayKittens();
        Check(!runtime._kittenSabers.ContainsKey(leia) && leia.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && runtime.SceneSaves.Profiles.Capture().Kittens.Count == 2,
            "boarding releases live bindings while retaining the roster person's profile");
        runtime._enabled = false;
        runtime.ResetKittenSabers();
        var seatedSave = runtime.SceneSaves.Profiles.Capture();
        Check(Read(seatedSave.Kittens.Single(item => item.Identity.Id == "Leia")).SaberEnabled,
            "saving while seated and disabled retains desired equipment");

        var replacementSystem = new CelestialSystem();
        var data = new object();
        var token = runtime.SceneSaves.BeginReconstruction(data, system, false);
        Universe.CurrentSystem = replacementSystem;
        runtime.SceneSaves.FinishReconstruction(token, replacementSystem, null, false);
        runtime.SceneSaves.Profiles.Import(seatedSave, runtime.SceneSaves.Warn);
        runtime.SceneSaves.ObserveSystem(replacementSystem);
        Check(runtime.SceneSaves.Profiles.Capture().Kittens.Count == 2,
            "first settled system observation cannot erase committed disabled profiles");
        var restored = new KittenEva("Leia");
        roster.AssignedVehicleId = KittenRosterEntryData.EvaAssignmentId;
        replacementSystem.All.Items = [restored];
        runtime._enabled = true;
        runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(restored) == new float3(.3f, .7f, .2f)
            && runtime.SaberTypeFor(restored) == SaberType.Dark && runtime.CapeEnabledFor(restored)
            && runtime.SaberEnabledFor(restored) && runtime._kittenSaberRequests.Count == 0
            && runtime.SaberLengthFor(restored) == 2.8f,
            "new EVA instance restores roster appearance, blade length and equipment through normal ownership");
        var restoredProcessors = restored.Renderable.Avatar.Core.CharacterModel.AnimProcessors;
        Check(restoredProcessors.Count == 2 && SaberPoseController.IsProcessorFor(restoredProcessors[0], restored)
            && SaberPoseController.IsClashTailFor(restoredProcessors[1], restored) && runtime._capeControllers.Count == 0,
            "restore creates the pose/clash pair and leaves cape rendering and cloth lazy");
        runtime.ResetKittenSabers(); runtime.RefreshOverlayKittens();
        restoredProcessors = restored.Renderable.Avatar.Core.CharacterModel.AnimProcessors;
        Check(restoredProcessors.Count == 2 && SaberPoseController.IsProcessorFor(restoredProcessors[0], restored)
            && SaberPoseController.IsClashTailFor(restoredProcessors[1], restored),
            "disable/re-enable and repeated cleanup do not duplicate pose/clash processors");

        runtime._kittenSabers[restored].Blade.Reset();
        runtime._kittenSabers[restored].TogglePhase = SaberTogglePhase.None;
        runtime.SetKittenSaberColor(restored, new(.4f, .5f, .6f));
        runtime.CaptureKittenChoices();
        Check(Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single(item => item.Identity.Id == "Leia")).SaberEnabled,
            "missing runtime equipment and unrelated appearance edits cannot erase enabled intent");

        runtime.QueueKittenSaber(restored, false);
        runtime.CaptureKittenChoices();
        Check(!Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single(item => item.Identity.Id == "Leia")).SaberEnabled,
            "pending retraction captures final user intent rather than intermediate lit state");
        runtime.QueueKittenBladeDeployment(restored, .4f);
        runtime.CaptureKittenChoices();
        Check(Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single(item => item.Identity.Id == "Leia")).SaberEnabled,
            "save capture includes the latest pending manual deployment intent before the safe handoff");
        runtime.ResetKittenSabers();
        runtime.SceneSaves.ObserveSystem(new CelestialSystem());
        Check(runtime.SceneSaves.Profiles.Capture().Kittens.Count == 0, "new scene clears logical profiles");

        CheckIdentityGuards();
        CheckLargeRestore();
        Console.WriteLine("PASS: kitten persistence runtime integration");
    }

    private static void CheckIdentityGuards()
    {
        var kitten = new KittenEva("Unique");
        var system = new CelestialSystem();
        Universe.CurrentSystem = system;
        system.All.Items = [kitten];
        Universe.KittenRoster = new();
        Check(new KittenIdentityIndex().Resolve(kitten)?.Kind == "standalone-eva", "rosterless EVA gets an exact standalone identity");
        var entry = new KittenRosterEntryData("Unique", "Stock");
        Universe.KittenRoster.Kittens.Add(entry);
        Check(new KittenIdentityIndex().Resolve(kitten)?.Kind == "roster", "compatible unique roster entry resolves");
        entry.Character = "Other";
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "character mismatch cannot fall back to standalone");
        entry.Character = "Stock"; entry.AssignedVehicleId = "Seated";
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "seated assignment cannot become another EVA identity");
        entry.AssignedVehicleId = "EVA"; entry.Kia = true;
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "KIA records remain dormant");
        entry.Kia = false;
        var vessel = new Vehicle("Vessel") { Seats = [new IVASeat { AssignedKittenHash = kitten.Hash }] };
        system.All.Items = [kitten, vessel];
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "simultaneous occupied seat and EVA is ambiguous");
        system.All.Items = [kitten];
        Universe.KittenRoster.Kittens.Add(new("UNIQUE", "Stock"));
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "case-insensitive hash collision rejects ambiguous roster identity");
        Universe.KittenRoster.Kittens.RemoveAt(1);
        system.All.Items = [kitten, new KittenEva("Unique")];
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "duplicate live identities reject rebind");
        system.All.Items = [kitten];
        entry.NameHash = KeyHash.Make("Stale");
        Check(new KittenIdentityIndex().Resolve(kitten) is null, "stale native roster hash is rejected");
        entry.NameHash = KeyHash.Make(entry.Name);
        var runtime = new SaberRuntime();
        runtime.SceneSaves.ObserveSystem(system);
        runtime.RefreshOverlayKittens();
        runtime.SetKittenSaberColor(kitten, new(.8f, .1f, .4f));
        system.All.Items = [kitten, vessel];
        runtime.SetKittenSaberType(kitten, SaberType.Dark);
        Check(Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single()).Type == SaberType.Light,
            "a new seat conflict invalidates UI writes before they can alter the saved person");
        system.All.Items = [kitten];
        kitten.Id = "Renamed"; kitten.Hash = KeyHash.Make(kitten.Id);
        runtime.SetKittenSaberColor(kitten, new(1, 1, 1));
        runtime.CaptureKittenChoices();
        var original = Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        Check(original.Red == .8f && original.Green == .1f, "foreign rename cannot overwrite the old person's saved choices");
        runtime.ResetKittenSabers();

        var changed = new KittenEva("Unique") { Character = new("New stock") };
        entry.Character = "New stock";
        system.All.Items = [changed];
        runtime.RefreshOverlayKittens();
        Check(runtime.SceneSaves.Profiles.PreservedCount == 1 && !runtime._profileBindings.ContainsKey(changed),
            "character changes preserve the original recipe and expose explicit recovery");
        runtime.SetKittenSaberColor(changed, new(.2f, .4f, .6f));
        runtime._discardUnavailableKittenRecords = true;
        runtime.ApplyKittenProfileRecovery();
        var adopted = runtime.SceneSaves.Profiles.Capture().Kittens.Single();
        Check(adopted.Identity.Character == "New stock" && Read(adopted).Red == .2f
            && runtime.SceneSaves.Profiles.PreservedCount == 0 && runtime._profileBindings.ContainsKey(changed),
            "explicit recovery adopts current choices without creating duplicate live resources");
        runtime._discardUnavailableKittenRecords = true;
        runtime.ResetKittenSabers();
        Check(!runtime._discardUnavailableKittenRecords, "cleanup cancels a pending old-scene recovery action");

        CheckRejectedIdentityWrites();
        CheckDeferredIntentSurvivesCleanup();
    }

    private static void CheckRejectedIdentityWrites()
    {
        var length = NewBoundIdentityAction("Length setter");
        var before = Read(length.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        AddSeatConflict(length.System, length.Kitten);
        length.Runtime.SetKittenSaberLength(length.Kitten, 2.8f);
        var after = Read(length.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        Check(!length.Runtime._profileBindings.ContainsKey(length.Kitten)
            && after.BladeLengthOverride == before.BladeLengthOverride,
            "a new seat conflict invalidates the length setter before it can rewrite the saved profile");
        length.Runtime.ResetKittenSabers();

        var reset = NewBoundIdentityAction("Length reset");
        reset.Runtime.SetKittenSaberLength(reset.Kitten, 2.8f);
        before = Read(reset.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        AddSeatConflict(reset.System, reset.Kitten);
        reset.Runtime.UseDefaultKittenSaberLength(reset.Kitten);
        after = Read(reset.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        Check(before.BladeLengthOverride == 2.8f && !reset.Runtime._profileBindings.ContainsKey(reset.Kitten)
            && after.BladeLengthOverride == before.BladeLengthOverride,
            "a new seat conflict invalidates the default-length reset before it can rewrite the saved profile");
        reset.Runtime.ResetKittenSabers();

        var toggle = NewBoundIdentityAction("Queued toggle");
        before = Read(toggle.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        AddSeatConflict(toggle.System, toggle.Kitten);
        toggle.Runtime.QueueKittenSaber(toggle.Kitten, true);
        toggle.Runtime.ApplyKittenSaberRequests();
        after = Read(toggle.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        Check(!before.SaberEnabled && !toggle.Runtime._profileBindings.ContainsKey(toggle.Kitten)
            && after.SaberEnabled == before.SaberEnabled,
            "a queued toggle revalidates its bound identity before writing enabled intent");
        toggle.Runtime.ResetKittenSabers();

        var manual = NewBoundIdentityAction("Cursor deployment");
        before = Read(manual.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        AddSeatConflict(manual.System, manual.Kitten);
        // Ordinary cursor motion carries RememberIntent:false; applying the changed
        // manual state still updates enabled intent, so the binding must be checked.
        manual.Runtime.QueueKittenBladeDeployment(manual.Kitten, .4f, rememberIntent: false);
        manual.Runtime.ApplyKittenSaberRequests();
        after = Read(manual.Runtime.SceneSaves.Profiles.Capture().Kittens.Single());
        Check(!before.SaberEnabled && !manual.Runtime._profileBindings.ContainsKey(manual.Kitten)
            && after.SaberEnabled == before.SaberEnabled,
            "manual cursor deployment revalidates identity before its profile intent write");
        manual.Runtime.ResetKittenSabers();
    }

    private static (SaberRuntime Runtime, KittenEva Kitten, CelestialSystem System) NewBoundIdentityAction(string name)
    {
        var runtime = new SaberRuntime();
        var system = new CelestialSystem();
        Universe.CurrentSystem = system;
        Universe.KittenRoster = new();
        var kitten = new KittenEva(name);
        Universe.KittenRoster.Kittens.Add(new KittenRosterEntryData(name, "Stock"));
        system.All.Items = [kitten];
        runtime.SceneSaves.ObserveSystem(system);
        runtime.RefreshOverlayKittens();
        Check(runtime._profileBindings.ContainsKey(kitten), $"{name} scenario starts with a fresh live profile binding");
        return (runtime, kitten, system);
    }

    private static void AddSeatConflict(CelestialSystem system, KittenEva kitten)
    {
        system.All.Items = [kitten, new Vehicle("Occupied") { Seats = [new IVASeat { AssignedKittenHash = kitten.Hash }] }];
    }

    private static void CheckDeferredIntentSurvivesCleanup()
    {
        var scenario = NewBoundIdentityAction("Deferred zero");
        var runtime = scenario.Runtime;
        var kitten = scenario.Kitten;
        runtime.QueueKittenSaber(kitten, true);
        runtime.ApplyKittenSaberRequests();
        var saber = runtime._kittenSabers[kitten];
        for (int i = 0; i < 6; i++)
        {
            runtime.ConfigureSaberPose(saber.Pose);
            runtime.AdvanceKittenSaber(saber, .1);
            Program.FrameNumber++;
            PublishVisiblePose(kitten);
        }
        Check(saber.Pose.HasFreshClashSnapshot
            && saber.Pose.TryBeginClash(Program.FrameNumber, runtime.VisibleBladeLength(saber)),
            "fresh bound pose can hold the currently enabled saber during persistence cleanup setup");

        runtime.QueueKittenBladeDeployment(kitten, 0, rememberIntent: false);
        runtime.ApplyKittenSaberRequests();
        Check(saber.DeferredManualDeployment == 0 && saber.Blade.Deployment > 0
            && !Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single()).SaberEnabled,
            "a held zero deployment persists its off intent while the blade remains frozen");
        runtime.ResetKittenSabers();
        runtime.RefreshOverlayKittens();
        Check(!runtime.SaberEnabledFor(kitten)
            && !Read(runtime.SceneSaves.Profiles.Capture().Kittens.Single()).SaberEnabled,
            "cleanup and profile rebind retain a deferred zero intent without an explicit gesture release");
        runtime.ResetKittenSabers();
    }

    private static void PublishVisiblePose(KittenEva kitten)
    {
        Program.RenderedViewport = Program.MainViewport;
        var model = kitten.Renderable.Avatar.Core.CharacterModel;
        var localPose = new TransformTRS[model.Skeleton.BoneCount];
        foreach (var processor in model.AnimProcessors)
            processor.UpdateLocalPose(float4x4.Identity, model.Skeleton, localPose, 0);
        foreach (var processor in model.AnimProcessors)
            processor.UpdateSkeleton(float4x4.Identity, model.Skeleton, 0);
    }

    private static void CheckLargeRestore()
    {
        var runtime = new SaberRuntime();
        var system = new CelestialSystem();
        Universe.CurrentSystem = system;
        Universe.KittenRoster = new();
        var kittens = Enumerable.Range(0, 129).Select(index => new KittenEva("Saved " + index)).ToArray();
        system.All.Items = kittens;
        runtime.SceneSaves.ObserveSystem(system);
        foreach (var kitten in kittens)
            runtime.SceneSaves.Profiles.GetOrCreate(new("standalone-eva", kitten.Id, "Stock"),
                new() { Red = .5f, SaberEnabled = true }, runtime.SceneSaves.Warn);
        runtime.RefreshOverlayKittens();
        Check(kittens.All(runtime.SaberEnabledFor) && runtime._kittenSaberRequests.Count == 0,
            "129 saved enabled sabers restore without overflowing the 128-entry UI request queue");
        int scans = HostCollection.SnapshotReads;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 64; index++) runtime.RefreshOverlayKittens();
        Check(GC.GetAllocatedBytesForCurrentThread() == before && HostCollection.SnapshotReads - scans == 64,
            "stable persistent bindings retain zero allocations and one fleet scan per refresh");
        runtime.ResetKittenSabers();
        Check(runtime.SceneSaves.Profiles.Capture().Kittens.All(record => Read(record).SaberEnabled),
            "bulk restored enabled intent survives resource cleanup");
    }

    private static Purr2D2.Persistence.KittenProfile Read(Purr2D2.Persistence.KittenSaveRecord record) =>
        System.Text.Json.JsonSerializer.Deserialize<Purr2D2.Persistence.KittenProfile>(record.State,
            Purr2D2.Persistence.KittenSaveStorage.Options)!;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Kitten persistence: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
