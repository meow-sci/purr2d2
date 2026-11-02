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
    private readonly ManualSwingSwooshGate _manualSwingSwoosh = new();
    private string _sliceStatus = "";
    private double _sliceMessageUntil;
    private double _playerTime = 0;
    private float _swingSpeed;
    private ulong _consumedPoseGeneration;
    private KittenEva? _resetGestureOwner;
    private KittenEva? _autoSwingDragOwner;
    private int _queuedSliceCancels;
    // The overlay's UI partial owns this state in-game; this lifecycle harness
    // stubs only its cancellation boundary and does not claim to cover UI input.
    private KittenEva? _saberDeploymentDragOwner;
    private KittenEva? _kittenColorPickerOwner;
    private void DismissKittenColorPicker() => _kittenColorPickerOwner = null;
    private void CancelSaberDeploymentDrag() => _saberDeploymentDragOwner = null;
    private KittenEva? _igniteDragOwner;
    private void CancelSaberIgniteDrag() => _igniteDragOwner = null;
    private void CancelSaberAutoSwingDrag() => _autoSwingDragOwner = null;
    private KittenEva? _dropSpinDragOwner;
    private void CancelSaberDropSpinDrag() => _dropSpinDragOwner = null;
    private void RemoveClashOwner(KittenEva owner)
    {
        if (_kittenSabers.TryGetValue(owner, out var saber)) saber.Pose.ClearClash();
    }
    private static void Report(string context, Exception error) { }

    private SaberRuntime()
    {
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _disposed = false; _fault = null; _selectingSlice = false; DebugTimings.Enabled = false;
    }
    private void CancelAssistedSlice(string reason) { _sliceSequence.Cancel(); _selectingSlice = false; _pose.ClearSliceSwing(); }
    private void ResetSomersault() { }
    private void ResetOverlaySwing() { _overlayDrag.Reset(); _resetGestureOwner = _pose.Kitten; }
    private void Queue(Command command) { if (command == Command.CancelSlice) _queuedSliceCancels++; }
    private void DrawSaber(KittenEva owner, IViewport viewport, float4x4 emitter)
    {
        if (!_kittenSabers.TryGetValue(owner, out var saber)) return;
        saber.PreviousTipTime = Program.FrameNumber;
        saber.PreviousTipBody = double3.Unpack(emitter.Translation);
    }
    private static bool Finite(double3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static void Cleanup(Action action, string component) => action();
    private sealed class Resettable { internal void Reset() { } }
    private sealed class FakeRenderer
    {
        internal readonly HashSet<KittenEva> Emitters = [];
        internal readonly Dictionary<KittenEva, float3> Colors = [];
        internal readonly Dictionary<KittenEva, double4x4> Detached = [];
        internal void SetDetachedSaber(KittenEva owner, double4x4 frame, float length, float3 color, SaberType type) => Detached[owner] = frame;
        internal void RemoveDetachedSaber(KittenEva owner) => Detached.Remove(owner);
        internal void RemoveEmitter(KittenEva owner) => Emitters.Remove(owner);
        internal void SetEmitter(KittenEva owner, double4x4 frame, float length, float3 color)
        {
            Emitters.Add(owner);
            Colors[owner] = color;
        }
    }

    public static void Main()
    {
        CheckLocomotionTransitions();
        CheckSwimmingLifecycle();
        CheckDropLifecycle();
        CheckDropSpinSelection();
        var runtime = new SaberRuntime();
        KittenEva first = new("First"), second = new("Second");
        second.Renderable.Avatar.Attachments.Mmu.Enabled = false;
        Program.ControlledVehicle = new object();
        var system = new CelestialSystem();
        system.All.Items = [first, second]; Universe.CurrentSystem = system;
        runtime.RefreshOverlayKittens();
        Check(runtime.OverlayKittens.Count == 2 && runtime._kittenSabers.Count == 2
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && second.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && !first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled,
            "settled fleet snapshot hides MMUs for equipped and uncontrolled EVA records without attaching poses");
        first.Renderable.Avatar.Attachments.Mmu.Enabled = true;
        runtime.RefreshOverlayKittens();
        Check(!first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled,
            "the active overlay lease re-hides an MMU enabled by native or other mod code");
        runtime.RefreshOverlayKittens(); // Warm the stable two-kitten snapshot and avatar leases.
        int scansBeforeRefreshLoop = HostCollection.SnapshotReads;
        long allocatedBeforeRefreshLoop = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++) runtime.RefreshOverlayKittens();
        long refreshAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRefreshLoop;
        int refreshFleetScans = HostCollection.SnapshotReads - scansBeforeRefreshLoop;
        Check(refreshAllocatedBytes == 0 && refreshFleetScans == 128,
            "warmed overlay MMU refreshes allocate 0 bytes and perform only one existing fleet scan each");
        var firstPalette = new float3(143 / 255f, 16 / 255f, 16 / 255f);
        var secondPalette = new float3(13 / 255f, 81 / 255f, 1 / 255f);
        Check(runtime.SaberColorFor(first) == firstPalette && runtime.SaberColorFor(second) == secondPalette,
            "first and second EVA kittens receive exact normalized red and green palette entries");
        Check(runtime.SaberTypeFor(first) == SaberType.Light && runtime.SaberTypeFor(second) == SaberType.Light,
            "every newly observed kitten starts with the Light-type BFL9K style");
        runtime.SetKittenSaberColor(first, new(1, 0, .2f));
        Check(runtime.SaberColorFor(first) == new float3(1, 0, .2f) && runtime.SaberColorFor(second) == secondPalette
            && !runtime.SaberEnabledFor(first) && runtime._kittenSaberRequests.Count == 0
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "changing one row color neither changes another kitten nor equips, ignites or binds its saber");
        runtime.SetKittenSaberType(first, SaberType.Dark);
        Check(runtime.SaberTypeFor(first) == SaberType.Dark && runtime.SaberTypeFor(second) == SaberType.Light
            && runtime.SaberColorFor(first) == new float3(1, 0, .2f) && runtime.SaberColorFor(second) == secondPalette
            && !runtime.SaberEnabledFor(first) && runtime._kittenSaberRequests.Count == 0
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "choosing a kitten's Dark style preserves color, other kittens and equipment intent");
        runtime.SetKittenSaberType(first, (SaberType)999);
        Check(runtime.SaberTypeFor(first) == SaberType.Light,
            "invalid type values recover the supported Light style");
        runtime.SetKittenSaberType(first, SaberType.Dark);
        runtime.SetKittenSaberType(second, SaberType.Dark);
        var sharedDark = runtime._settings.EffectsFor(runtime.SaberTypeFor(first));
        sharedDark.ArcCount = 7;
        Check(ReferenceEquals(sharedDark, runtime._settings.EffectsFor(runtime.SaberTypeFor(second)))
            && runtime._settings.EffectsFor(runtime.SaberTypeFor(second)).ArcCount == 7
            && runtime._settings.LightEffects.ArcCount == 8,
            "two Dark kittens resolve the same editable profile without changing Light's profile");
        runtime.SetKittenSaberType(second, SaberType.Light);
        Program.ControlledVehicle = second;
        var savedFallback = runtime.DefaultSaberColor;
        runtime.SetKittenSaberColor(second, new(.4f, 1, .3f));
        Check(runtime.SaberColorFor(second) == new float3(.4f, 1, .3f) && runtime.DefaultSaberColor == savedFallback
            && runtime.SaberColorFor(first) == new float3(1, 0, .2f),
            "a popup changes its selected kitten's session color without changing saved fallback or peers");
        KittenEva third = new("Third"), fourth = new("Fourth"), fifth = new("Fifth");
        system.All.Items = [first, second, third, fourth, fifth]; runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(third) == new float3(49 / 255f, 49 / 255f, 5 / 255f)
            && runtime.SaberColorFor(fourth) == new float3(98 / 255f, 9 / 255f, 96 / 255f)
            && runtime.SaberColorFor(fifth) == firstPalette,
            "new EVA colors cycle yellow, purple and red independently of the saved preference");
        runtime.RefreshOverlayKittens();
        Check(runtime.SaberTypeFor(first) == SaberType.Dark && runtime.SaberTypeFor(second) == SaberType.Light,
            "fleet refresh preserves each kitten's selected saber type");
        Check(runtime.SaberColorFor(first) == new float3(1, 0, .2f)
            && runtime.SaberColorFor(second) == new float3(.4f, 1, .3f) && runtime._nextKittenColor == 1,
            "repeated snapshots preserve manual colors and do not advance the palette");
        runtime.SetKittenSaberColor(third, new(float.NaN, 12, -2));
        Check(runtime.SaberColorFor(third) == new float3(.12f, 1, 0), "session colors reject nonfinite values and clamp every channel");
        system.All.Items = [first, second]; runtime.RefreshOverlayKittens();

        KittenEva discoveredWhileDisabled = new("Discovered while disabled");
        system.All.Items = [first, second, discoveredWhileDisabled];
        runtime._enabled = false;
        runtime.RefreshOverlayKittens();
        Check(first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled
            && discoveredWhileDisabled.Renderable.Avatar.Attachments.Mmu.Enabled,
            "a disabled overlay restores existing originals and leaves newly seen EVA MMUs unchanged");
        runtime._enabled = true;
        runtime.RefreshOverlayKittens();
        Check(!first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled
            && !discoveredWhileDisabled.Renderable.Avatar.Attachments.Mmu.Enabled,
            "quick overlay reactivation reacquires one hidden MMU lease for every current EVA");
        runtime._enabled = false;
        runtime.RefreshOverlayKittens();
        Check(first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled
            && discoveredWhileDisabled.Renderable.Avatar.Attachments.Mmu.Enabled,
            "quick overlay deactivation restores true and originally false MMU states exactly");
        runtime._enabled = true;
        system.All.Items = [first, second]; runtime.RefreshOverlayKittens();
        runtime._fault = "simulated runtime fault";
        runtime.RefreshOverlayKittens();
        Check(first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled,
            "fault cleanup restores each EVA's original MMU state");
        runtime.RefreshOverlayKittens();
        Check(first.Renderable.Avatar.Attachments.Mmu.Enabled,
            "a faulted runtime does not reacquire MMU visibility leases");
        runtime._fault = null;
        runtime.RefreshOverlayKittens();
        Check(!first.Renderable.Avatar.Attachments.Mmu.Enabled && !second.Renderable.Avatar.Attachments.Mmu.Enabled,
            "clearing a fault allows the enabled overlay to reacquire its MMU leases");

        runtime.QueueKittenSaber(first, true);
        Check(first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && runtime.SaberEnabledFor(first), "checkbox reflects its queued state without mutating the avatar");
        runtime.ApplyKittenSaberRequests();
        var firstSaber = runtime._kittenSabers[first];
        runtime.UpdateOtherSabers(0);
        Check(firstSaber.Blade.Equipped && !firstSaber.Blade.TargetOn && firstSaber.Pose.Eligible
            && firstSaber.Pose.Blend == 0 && firstSaber.Blade.Deployment == 0,
            "enabling while paused retains a checked request without raising the arm or igniting");
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        Check(ReferenceEquals(firstSaber, runtime._kittenSabers[first])
            && HasSaberPosePair(first),
            "repeated enable reuses the record and actual Bind installs the hand solve before the clash tail");

        runtime.QueueKittenSaber(second, true); runtime.ApplyKittenSaberRequests();
        var secondSaber = runtime._kittenSabers[second];
        runtime.UpdateOtherSabers(.1);
        Check(firstSaber.Pose.Blend > 0 && firstSaber.Pose.Blend < 1
            && !firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment == 0 && runtime.SaberEnabledFor(first),
            "enabling first raises the arm with an unlit hilt and keeps the checkbox checked");
        runtime.UpdateOtherSabers(.1);
        Check(firstSaber.Pose.Blend == 1 && !firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment == 0,
            "the fully equipped pose precedes ignition");
        runtime.UpdateOtherSabers(0);
        Check(!firstSaber.Blade.TargetOn, "pausing at full equip delays ignition until time resumes");
        runtime.UpdateOtherSabers(.1);
        Check(firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment > 0 && firstSaber.Blade.Deployment < 1,
            "ignition extends continuously after the equip motion finishes");
        for (int i = 0; i < 3; i++) runtime.UpdateOtherSabers(.1);
        var phaseBeforeType = firstSaber.TogglePhase;
        float deploymentBeforeType = firstSaber.Blade.Deployment;
        var colorBeforeType = firstSaber.Color;
        runtime.SetKittenSaberType(first, SaberType.Light);
        Check(firstSaber.Blade.Equipped && firstSaber.Blade.TargetOn
            && firstSaber.Blade.Deployment == deploymentBeforeType && firstSaber.TogglePhase == phaseBeforeType
            && firstSaber.Color == colorBeforeType && runtime._kittenSaberRequests.Count == 0
            && HasSaberPosePair(first)
            && secondSaber.Type == SaberType.Light,
            "changing a lit saber's style preserves its deployment, ignition, pose, color and other owners");
        runtime.SetKittenSaberType(first, SaberType.Dark);
        runtime._kittenColorPickerOwner = first;
        runtime.SetControlledKitten(first);
        runtime._igniteDragOwner = first;
        runtime.SetControlledKitten(second);
        Check(ReferenceEquals(runtime._blade, secondSaber.Blade) && ReferenceEquals(runtime._pose, secondSaber.Pose)
            && ReferenceEquals(runtime._audio, secondSaber.Audio) && ReferenceEquals(runtime._owner, second)
            && firstSaber.Blade.TargetOn && secondSaber.Blade.TargetOn
            && !runtime._freshPoseThisTick && !runtime._hasPreviousTip && runtime._swingSpeed == 0
            && runtime.Color == secondSaber.Color && runtime.Color != firstSaber.Color
            && runtime._igniteDragOwner == null
            && runtime._kittenColorPickerOwner == null
            && HasSaberPosePair(first),
            "switching control changes active aliases and preserves both equipped sabers");
        Check(ReferenceEquals(runtime._resetGestureOwner, first),
            "control switch resets the old kitten's gesture before changing pose aliases");

        Program.ControlledVehicle = second;
        Program.FrameNumber = 7; Program.RenderedViewport = Program.MainViewport;
        firstSaber.Pose.Update(.1, true);
        var firstModel = first.Renderable.Avatar.Core.CharacterModel;
        firstModel.AnimProcessors[0].UpdateSkeleton(StockModelTransform(first), firstModel.Skeleton, 0);
        Check(firstSaber.Pose.HasFreshBodyFrame && firstSaber.Pose.MainPoseGeneration == 1,
            "actual pose publication accepts a visible EVA kitten that is not controlled");
        runtime.UpdateOtherSabers(0);
        Check(runtime._renderer.Colors.TryGetValue(first, out var renderedColor) && renderedColor == firstSaber.Color
            && renderedColor != secondSaber.Color, "an uncontrolled kitten's point light receives its own color");
        Program.RenderedViewport = new Viewport(ViewportType.Secondary);
        Program.FrameNumber = 8;
        firstModel.AnimProcessors[0].UpdateSkeleton(StockModelTransform(first), firstModel.Skeleton, 0);
        Check(firstSaber.Pose.MainPoseGeneration == 1, "secondary view cannot refresh destructive-contact pose generation");
        Program.FrameNumber = 10;
        Check(!firstSaber.Pose.HasFreshBodyFrame, "noncontrolled poses retain the same stale-frame rejection");

        first.Renderable.Avatar.Attachments.Mmu.Enabled = true;
        runtime.RefreshOverlayKittens();
        Check(!first.Renderable.Avatar.Attachments.Mmu.Enabled,
            "a native MMU re-enable is corrected before saber stow begins");
        int stopsBeforeDisable = firstSaber.Audio.Stops;
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        Check(firstSaber.Blade.Equipped && !firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment == 1
            && firstSaber.Pose.Blend == 1 && HasSaberPosePair(first)
            && firstSaber.Audio.Stops == stopsBeforeDisable && !runtime.SaberEnabledFor(first),
            "unchecking requests retraction without removing the hilt, pose or sound");
        runtime.UpdateOtherSabers(0);
        Check(firstSaber.Blade.Deployment == 1 && firstSaber.Pose.Blend == 1,
            "disabling while paused preserves the visible blade and arm until time resumes");
        Program.RenderedViewport = Program.MainViewport; Program.FrameNumber = 11;
        firstModel.AnimProcessors[0].UpdateSkeleton(StockModelTransform(first), firstModel.Skeleton, 0);
        runtime.UpdateOtherSabers(.05); runtime.UpdateOtherSaberAudio();
        Check(firstSaber.Blade.Deployment > 0 && firstSaber.Blade.Deployment < 1 && firstSaber.Pose.Blend == 1
            && firstSaber.Audio.Updates > 0 && !firstSaber.Audio.LastTargetOn
            && firstSaber.Audio.LastDeployment == firstSaber.Blade.Deployment && firstSaber.Audio.Stops == stopsBeforeDisable,
            "the arm stays raised while the shrinking blade reaches the existing retraction audio path");
        Check(firstSaber.Audio.LastType == SaberType.Dark,
            "noncontrolled saber audio receives the kitten's Dark style during retraction");
        runtime.UpdateOtherSabers(.1); runtime.UpdateOtherSabers(.1);
        Check(firstSaber.Blade.Deployment == 0 && firstSaber.Blade.Equipped && firstSaber.Pose.Blend == 1,
            "the blade retracts completely before arm lowering starts");
        runtime.UpdateOtherSabers(.05);
        Check(firstSaber.Pose.Blend > 0 && firstSaber.Pose.Blend < 1 && firstSaber.Blade.Equipped
            && HasSaberPosePair(first) && !first.Renderable.Avatar.Attachments.Mmu.Enabled
            && firstSaber.Audio.Stops == stopsBeforeDisable,
            "unequipping lowers the arm while the overlay-wide MMU lease and audio channels remain active");
        float loweringBlend = firstSaber.Pose.Blend;
        runtime.UpdateOtherSabers(0);
        Check(firstSaber.Pose.Blend == loweringBlend && firstSaber.Blade.Equipped,
            "pausing during unequip freezes the lowering pose without hiding the hilt");
        Program.FrameNumber = 12;
        firstModel.AnimProcessors[0].UpdateSkeleton(StockModelTransform(first), firstModel.Skeleton, 0);
        Check(firstSaber.Pose.HasFreshBodyFrame, "the lowering hilt still publishes a fresh hand transform");
        runtime.UpdateOtherSabers(.1); runtime.UpdateOtherSabers(.05);
        Check(!firstSaber.Blade.Equipped && firstModel.AnimProcessors.Count == 0
            && !first.Renderable.Avatar.Attachments.Mmu.Enabled && firstSaber.Audio.Stops > stopsBeforeDisable,
            "stowing the saber releases its pose but leaves the overlay-wide MMU lease active");

        CheckToggleReversals(runtime, first, second);
        CheckControlledToggle(runtime, first, second);
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        runtime.UpdateOtherSabers(.1);
        Check(HasSaberPosePair(first) && !first.Renderable.Avatar.Attachments.Mmu.Enabled,
            "reenabling binds one fresh hand/clash processor pair and hides the MMU again");

        runtime._kittenColorPickerOwner = first;
        runtime._renderer.Emitters.Add(first); first.IsDisposed = true;
        system.All.Items = [second]; runtime.RefreshOverlayKittens();
        Check(!runtime._kittenSabers.ContainsKey(first) && firstModel.AnimProcessors.Count == 0
            && first.Renderable.Avatar.Attachments.Mmu.Enabled && firstSaber.Audio.Shutdowns == 1
            && firstSaber.Audio.Disposals == 1 && !runtime._renderer.Emitters.Contains(first)
            && secondSaber.Blade.TargetOn && runtime._kittenColorPickerOwner == null,
            "boarding or disposing an uncontrolled EVA releases its owned resources and closes its color picker");
        runtime.RefreshOverlayKittens();
        Check(firstSaber.Audio.Disposals == 1, "repeated fleet refresh cannot dispose a removed kitten twice");

        runtime.QueueKittenSaber(first, true);
        runtime.SetKittenSaberColor(first, float3.Zero);
        runtime.SetKittenSaberType(first, SaberType.Dark);
        Check(runtime._kittenSaberRequests.Count == 0 && !runtime._kittenSabers.ContainsKey(first),
            "stale removed kittens cannot enqueue a saber or recreate an appearance record");
        var unseen = new KittenEva("Unseen");
        runtime.SetKittenSaberType(unseen, SaberType.Dark);
        Check(runtime.SaberTypeFor(unseen) == SaberType.Light && !runtime._kittenSabers.ContainsKey(unseen),
            "a kitten outside the settled fleet cannot gain a session record from a stale type selection");
        runtime.QueueKittenSaber(second, false); runtime.ResetKittenSabers();
        Check(runtime._kittenSabers.Count == 0 && runtime._kittenSaberRequests.Count == 0
            && runtime.OverlayKittens.Count == 0 && runtime._activeSaber == null
            && ReferenceEquals(runtime._pose, runtime._idleSaber.Pose)
            && second.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && !second.Renderable.Avatar.Attachments.Mmu.Enabled
            && secondSaber.Audio.Shutdowns == 1 && secondSaber.Audio.Disposals == 1,
            "world reset disposes every remaining kitten, restores original MMU state, clears queued UI work and restores idle aliases");
        KittenEva afterReset = new("After reset");
        system.All.Items = [afterReset]; runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(afterReset) == firstPalette && runtime.SaberTypeFor(afterReset) == SaberType.Light,
            "world reset starts a new Light-type BFL9K session and restarts palette assignment at red");
        runtime.ResetKittenSabers();
        CheckPerKittenLengthAndManualDeployment();
        CheckAutoSwingAndDeferredDeployment();
        CheckCapeRuntime();
    }

    private static void CheckPerKittenLengthAndManualDeployment()
    {
        var runtime = new SaberRuntime();
        var first = new KittenEva("Manual one");
        var second = new KittenEva("Manual two");
        var system = new CelestialSystem();
        system.All.Items = [first, second]; Universe.CurrentSystem = system;
        Program.ControlledVehicle = new object();
        runtime.RefreshOverlayKittens();
        runtime.SetKittenSaberLength(first, 2.5f);
        var firstSaber = runtime._kittenSabers[first];
        var secondSaber = runtime._kittenSabers[second];
        firstSaber.Blade.Equip(true); firstSaber.Blade.HoldDeployment(.4f);
        secondSaber.Blade.Equip(true); secondSaber.Blade.HoldDeployment(.4f);
        Check(runtime.VisibleBladeLength(firstSaber) == 1 && MathF.Abs(runtime.VisibleBladeLength(secondSaber) - .56f) < 1e-6f,
            "each kitten uses its own blade length override while peers inherit the shared default");
        runtime._settings.BladeLength = 3.1f;
        Check(runtime.VisibleBladeLength(firstSaber) == 1 && MathF.Abs(runtime.VisibleBladeLength(secondSaber) - 1.24f) < 1e-6f,
            "a null per-kitten override tracks later default-length changes dynamically");
        runtime.UseDefaultKittenSaberLength(first);
        Check(runtime.SaberLengthOverrideFor(first) is null
            && MathF.Abs(runtime.VisibleBladeLength(firstSaber) - 1.24f) < 1e-6f,
            "Use default clears an override and immediately inherits the current default");

        for (int i = 0; i < 300; i++)
        {
            runtime.QueueKittenBladeDeployment(first, (i % 97) / 96f);
            runtime.QueueKittenSaber(first, (i & 1) != 0);
        }
        runtime.QueueKittenBladeDeployment(first, .73f);
        Check(runtime._kittenSaberRequests.Count <= runtime._currentKittens.Count * 2
            && runtime.SaberEnabledFor(first),
            "rapid alternating toggle/manual commands compact to a bounded stream without losing the latest deployment endpoint");
        runtime.ApplyKittenSaberRequests();
        Check(firstSaber.Blade.Equipped && firstSaber.Blade.ManualDeployment
            && firstSaber.Blade.Deployment == .4f && firstSaber.Blade.ManualTargetDeployment == .73f
            && firstSaber.Blade.TargetOn
            && firstSaber.TogglePhase == SaberTogglePhase.None,
            "the last manual request wins after an ordered toggle/manual burst and keeps its exact target");
        runtime.UpdateOtherSabers(.01);
        Check(firstSaber.Blade.Deployment > .4f && firstSaber.Blade.Deployment < .73f
            && firstSaber.Blade.ManualTargetDeployment == .73f && firstSaber.Blade.ManualDeployment,
            "background saber advancement animates toward the retained manual fraction");
        float beforeDuplicateRequest = firstSaber.Blade.Deployment;
        Check(!runtime.ApplyManualDeploymentNow(first, firstSaber, .73f)
            && firstSaber.Blade.Deployment == beforeDuplicateRequest
            && firstSaber.Blade.ManualTargetDeployment == .73f,
            "repeating a target during catch-up does not report a changed intent or restart motion");
        runtime.UpdateOtherSabers(.1);
        Check(firstSaber.Blade.Deployment == .73f,
            "manual background advancement reaches the exact partial target without overshoot");

        runtime.QueueKittenBladeDeployment(first, 0);
        runtime.ApplyKittenSaberRequests();
        Check(firstSaber.Blade.Equipped && firstSaber.Blade.ManualDeployment
            && firstSaber.Blade.Deployment == .73f && firstSaber.Blade.ManualTargetDeployment == 0
            && !firstSaber.Blade.TargetOn && firstSaber.Blade.Phase == "Retracting"
            && firstSaber.Pose.Kitten == first,
            "zero deployment smoothly retracts the blade while keeping the kitten's hilt and pose equipped");
        runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.Blade.Deployment > 0 && firstSaber.Blade.Deployment < .73f
            && firstSaber.Blade.ManualTargetDeployment == 0,
            "manual zero advances without snapping the current blade fraction");
        runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.Blade.Deployment == 0 && firstSaber.Blade.Equipped,
            "manual zero reaches exact retraction without lowering the equipped hilt");
        runtime.QueueKittenSaber(first, true);
        runtime.ApplyKittenSaberRequests();
        for (int i = 0; i < 20; i++) runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.Blade.TargetOn && !firstSaber.Blade.ManualDeployment
            && firstSaber.Blade.Deployment == 1,
            "the next ordinary enable clears manual hold and resumes full automatic ignition");
        Check(secondSaber.Blade.ManualDeployment && secondSaber.Blade.Deployment == .4f,
            "holding or enabling one kitten leaves another kitten's blade fraction independent");
        runtime.ResetKittenSabers();
    }

    private static void CheckAutoSwingAndDeferredDeployment()
    {
        var runtime = new SaberRuntime();
        var first = new KittenEva("Auto swing one");
        var second = new KittenEva("Auto swing two");
        var system = new CelestialSystem();
        system.All.Items = [first, second]; Universe.CurrentSystem = system;
        Program.ControlledVehicle = first; Program.EditorFlag = false; Universe.Speed = 1;
        runtime.RefreshOverlayKittens(); runtime.SetControlledKitten(first);
        var firstSaber = runtime._kittenSabers[first];
        var secondSaber = runtime._kittenSabers[second];

        Check(runtime.SetSaberAutoSwing(first, true) && firstSaber.AutoSwing.Enabled
            && runtime._kittenSaberRequests.Count == 1 && runtime._kittenSaberRequests[0].Enabled
            && !firstSaber.Blade.Equipped && !secondSaber.AutoSwing.Enabled,
            "enabling automatic swing on a retracted kitten queues ordinary equip while keeping state per kitten");
        runtime.ApplyKittenSaberRequests();
        for (int i = 0; i < 6; i++)
        {
            runtime.ConfigureSaberPose(firstSaber.Pose);
            runtime.AdvanceSaberAutoSwing(firstSaber, .1);
            runtime.AdvanceKittenSaber(firstSaber, .1);
            Program.FrameNumber++;
            PublishVisiblePose(first);
        }
        Check(firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment > 0
            && firstSaber.AutoSwing.Enabled && firstSaber.AutoSwing.Phase > 0
            && firstSaber.Pose.GripOffsetFromChestCm != new float3(runtime._settings.GripX, runtime._settings.GripY, runtime._settings.GripZ),
            "the controlled auto swing advances only after its blade is visible and applies an offset from neutral");

        Check(runtime.SetSaberAutoSwing(second, true), "a background kitten can retain an independent auto-swing state");
        runtime.ApplyKittenSaberRequests();
        secondSaber.Blade.HoldDeployment(.4f); secondSaber.TogglePhase = SaberTogglePhase.None;
        secondSaber.Pose.Update(.1, true); Program.FrameNumber++; PublishVisiblePose(second);
        double backgroundPhase = secondSaber.AutoSwing.Phase;
        runtime.UpdateOtherSabers(.1);
        Check(secondSaber.AutoSwing.Phase > backgroundPhase && secondSaber.Blade.Deployment == .4f
            && secondSaber.Blade.ManualDeployment,
            "background automatic swing continues on its own visible partial manual blade");

        runtime.SetControlledKitten(second);
        Check(firstSaber.AutoSwing.Enabled && secondSaber.AutoSwing.Enabled,
            "switching control preserves both kittens' independent automatic motion state");
        Program.ControlledVehicle = first; runtime.SetControlledKitten(first);
        firstSaber.AutoSwing.SetDuration(1.25f);
        runtime.DisableSaberAutoSwing(firstSaber);
        _ = runtime._overlayDrag.Begin(float2.Zero, 80, float3.Zero, float3.UnitZ);
        runtime._sliceSequence.Start(); runtime._selectingSlice = true;
        int cancelBefore = runtime._queuedSliceCancels;
        Check(runtime.SetSaberAutoSwing(first, true) && firstSaber.AutoSwing.Enabled
            && !runtime._overlayDrag.Active && runtime._queuedSliceCancels == cancelBefore + 1,
            "re-enabling auto cancels the active manual drag and queues assisted-selection cleanup safely");
        runtime.CancelAssistedSlice("");
        runtime.DisableSaberAutoSwing(firstSaber);
        runtime.ConfigureSaberPose(firstSaber.Pose);
        Check(firstSaber.Pose.GripOffsetFromChestCm == new float3(runtime._settings.GripX, runtime._settings.GripY, runtime._settings.GripZ)
            && firstSaber.Pose.BladeDirectionModel == new float3(runtime._settings.AimX, runtime._settings.AimY, runtime._settings.AimZ),
            "manual takeover restores the saved neutral grip and aim without accumulating auto offsets");

        firstSaber.Blade.HoldDeployment(.4f);
        firstSaber.Pose.Update(.1, true); Program.FrameNumber++; PublishVisiblePose(first);
        float visibleBeforeClash = runtime.VisibleBladeLength(firstSaber);
        Check(firstSaber.Pose.HasFreshClashSnapshot && firstSaber.Pose.TryBeginClash(0, visibleBeforeClash),
            "a fresh visible pose can begin a held-blade clash sample");
        float deploymentBeforeHold = firstSaber.Blade.Deployment;
        runtime.QueueKittenBladeDeployment(first, .82f);
        runtime.QueueKittenBladeDeployment(first, .23f);
        runtime.ApplyKittenSaberRequests();
        Check(firstSaber.Pose.ClashHeld && firstSaber.DeferredManualDeployment == .23f
            && firstSaber.Blade.Deployment == deploymentBeforeHold,
            "manual requests during hit-stop defer to the latest fraction without changing the held blade");
        runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.Pose.ClashHeld && firstSaber.Blade.Deployment == deploymentBeforeHold
            && firstSaber.DeferredManualDeployment == .23f,
            "deferred manual deployment remains untouched throughout the 0.4-second visual hold");
        firstSaber.Pose.UpdateClash(.4);
        runtime.AdvanceKittenSaber(firstSaber, .01);
        Check(!firstSaber.Pose.ClashHeld && firstSaber.DeferredManualDeployment is null
            && firstSaber.Blade.ManualDeployment && firstSaber.Blade.ManualTargetDeployment == .23f
            && firstSaber.Blade.Deployment > .23f && firstSaber.Blade.Deployment < deploymentBeforeHold,
            "the latest deferred target applies after clash and begins animating from the held fraction");
        runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.Blade.Deployment == .23f,
            "the deferred manual target settles exactly after the clash hold");

        firstSaber.Pose.Update(.1, true); Program.FrameNumber++; PublishVisiblePose(first);
        Check(firstSaber.Pose.TryBeginClash(.5, runtime.VisibleBladeLength(firstSaber)),
            "the same owner can start a subsequent clash from a fresh sample");
        runtime.QueueKittenBladeDeployment(first, .7f); runtime.ApplyKittenSaberRequests();
        Check(firstSaber.DeferredManualDeployment == .7f && firstSaber.Blade.Deployment == .23f,
            "a second hit-stop retains a pending slider value without advancing its visible blade");
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        runtime.AdvanceKittenSaber(firstSaber, .1);
        Check(firstSaber.DeferredManualDeployment is null && !firstSaber.AutoSwing.Enabled
            && firstSaber.AutoSwing.DurationSeconds == 1.25f
            && !firstSaber.Blade.TargetOn && !firstSaber.Blade.ManualDeployment,
            "ordinary stow supersedes deferred manual input so an old endpoint cannot re-ignite the saber");
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
            processor.UpdateSkeleton(StockModelTransform(kitten), model.Skeleton, 0);
    }

    private static bool HasSaberPosePair(KittenEva kitten)
    {
        List<IAnimProcessor> processors = kitten.Renderable.Avatar.Core.CharacterModel.AnimProcessors;
        return processors.Count == 2
            && SaberPoseController.IsProcessorFor(processors[0], kitten)
            && SaberPoseController.IsClashTailFor(processors[1], kitten);
    }

    private static void CheckCapeRuntime()
    {
        CapeController.ResetMetrics();
        Universe.Speed = 1;
        Program.EditorFlag = false;
        Program.FrameNumber = 100;
        var main = new Viewport(ViewportType.Main);
        Program.MainViewport = main;

        var runtime = new SaberRuntime();
        var kittens = new KittenEva[6];
        for (int i = 0; i < kittens.Length; i++)
        {
            kittens[i] = new KittenEva($"Cape {i}") { PositionEcl = new double3(i == 5 ? 80 : i + 1, 0, 0) };
        }
        var system = new CelestialSystem();
        system.All.Items = kittens;
        Universe.CurrentSystem = system;
        runtime.RefreshOverlayKittens();
        HostCollection.SnapshotReads = Universe.SpeedReads = Viewport.CameraReads = ViewportRegistry.GameViewReads = 0;
        foreach (KittenEva kitten in kittens) kitten.PositionReads = kitten.VelocityReads = 0;
        for (int i = 0; i < 8; i++) runtime.UpdateCapes(.1);
        long emptyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++) runtime.UpdateCapes(.1);
        long emptyAllocated = GC.GetAllocatedBytesForCurrentThread() - emptyBefore;
        Check(CapeController.Constructions == 0 && kittens.All(k => runtime._kittenSabers[k].Cape is null),
            "cape-off by default creates no cloth, renderer or per-kitten cape controller");
        Check(!runtime.HasCapeWork && emptyAllocated == 0 && HostCollection.SnapshotReads == 0
            && Universe.SpeedReads == 0 && Viewport.CameraReads == 0 && ViewportRegistry.GameViewReads == 0
            && kittens.All(k => k.PositionReads == 0 && k.VelocityReads == 0),
            "an empty cape registry returns allocation-free without scanning the kitten snapshot or reading camera, speed or motion");

        runtime.SetKittenSaberType(kittens[0], SaberType.Dark);
        runtime.SetKittenCapeEnabled(kittens[0], true);
        Check(runtime.CapeEnabledFor(kittens[0]) && !runtime.CapeEnabledFor(kittens[1])
            && runtime.SaberTypeFor(kittens[0]) == SaberType.Dark && CapeController.Constructions == 0,
            "the cape option and Light/Dark style are independent per-kitten session settings");
        runtime.UpdateCapes(1d / 60d);
        Check(CapeController.Constructions == 1 && runtime._kittenSabers[kittens[0]].Cape is not null,
            "enabling one kitten creates its controller only at a settled cape update");
        Check(runtime._capePendingCreationCount == 0,
            "successful setup removes the owner from the pending-creation count");

        for (int i = 1; i < 5; i++) runtime.SetKittenCapeEnabled(kittens[i], true);
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 5 && kittens.Skip(5).All(k => runtime._kittenSabers[k].Cape is null),
            "only opted-in kittens allocate cape controllers and the independent sixth kitten remains off");

        runtime.SetKittenCapeEnabled(kittens[5], true);
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 5 && runtime._kittenSabers[kittens[5]].Cape is null
            && runtime._capePendingCreationCount == 1,
            "a never-created cape stays lightweight while every visible game view is beyond render range");
        kittens[5].PositionEcl = new double3(60, 0, 0);
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 6 && runtime._kittenSabers[kittens[5]].Cape is not null,
            "a cape controller can be created at the exact render-distance boundary without a recent draw");
        Check(runtime._capePendingCreationCount == 0,
            "boundary creation drains the final pending-setup count");
        kittens[5].PositionEcl = new double3(80, 0, 0);

        // Only the first five are in render range. The sixth remains off-screen at 80 m.
        for (int i = 0; i < 6; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        Check(CapeController.DrawCalls == 5 && runtime._kittenSabers[kittens[5]].Cape!.LastMainDrawFrame == ulong.MaxValue,
            "rendering is limited to nearby game views and leaves distant cape bones untouched");
        ulong mainFrame = runtime._kittenSabers[kittens[0]].Cape!.LastMainDrawFrame;
        Program.FrameNumber = 101;
        var secondary = new Viewport(ViewportType.Secondary);
        runtime.DrawCape(kittens[0].Renderable, secondary, float4x4.Identity);
        Check(runtime._kittenSabers[kittens[0]].Cape!.LastMainDrawFrame == mainFrame,
            "secondary views draw capes without refreshing the main-view simulation visibility latch");

        // Four nearest visible capes enter the active set. Two per-frame budget
        // plus a rotating cursor gives each one a step every other 60 Hz frame.
        runtime.UpdateCapes(1d / 60d);
        Check(CapeController.StepCalls == 0, "cape simulation waits for one 30 Hz time quantum");
        for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        Program.FrameNumber = 102;
        runtime.UpdateCapes(1d / 60d);
        Check(CapeController.StepCalls == 2 && CapeController.StepOwners.SequenceEqual(["Cape 0", "Cape 1"])
            && runtime._kittenSabers[kittens[0]].Cape!.LastStepDt == CapeUpdatePolicy.SimulationInterval,
            "each frame runs at most two due cloth steps and drops excess time at a 30 Hz quantum");
        for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        Program.FrameNumber = 103;
        runtime.UpdateCapes(1d / 60d);
        Check(CapeController.StepCalls == 4 && CapeController.StepOwners.SequenceEqual(["Cape 0", "Cape 1", "Cape 2", "Cape 3"]),
            "round-robin scheduling shares the two-step budget across all four nearest capes");
        Check(!CapeController.StepOwners.Contains("Cape 4") && !CapeController.StepOwners.Contains("Cape 5"),
            "visible capes beyond the four-simulation budget remain in the static drape");

        // A zero simulation budget is immediate even while paused. It keeps all
        // renderers, but does not ask for positions, camera, speed or velocity.
        runtime._settings.CapeSimulationLimit = 0;
        HostCollection.SnapshotReads = Universe.SpeedReads = Viewport.CameraReads = ViewportRegistry.GameViewReads = 0;
        foreach (KittenEva kitten in kittens) kitten.PositionReads = kitten.VelocityReads = 0;
        runtime.UpdateCapes(.1);
        int suspendsAfterZero = CapeController.SuspendCalls;
        int releasesAfterZero = CapeController.SuspendReleases;
        Universe.Speed = 0;
        runtime.UpdateCapes(.1);
        Check(suspendsAfterZero == 6 && CapeController.SuspendCalls == 12 && releasesAfterZero == 6
            && CapeController.SuspendReleases == releasesAfterZero && runtime._capeControllers.Count == 6
            && HostCollection.SnapshotReads == 0 && Universe.SpeedReads == 0 && Viewport.CameraReads == 0
            && ViewportRegistry.GameViewReads == 0
            && kittens.All(k => k.PositionReads == 0 && k.VelocityReads == 0),
            "zero cape simulation releases each private solver once, stays idempotent while paused and preserves cosmetic renderers");
        runtime._settings.CapeSimulationLimit = SaberSettings.DefaultCapeSimulationLimit;
        Universe.Speed = 1;

        for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        Program.FrameNumber = 104;
        Universe.Speed = 0;
        runtime.UpdateCapes(.1);
        Check(CapeController.StepCalls == 4,
            "pausing simulation freezes a cape even when its timer is already due");

        for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        Program.FrameNumber = 105;
        Universe.Speed = 10;
        runtime.UpdateCapes(.1);
        int warpResets = CapeController.WarpResets;
        runtime.DrawCape(kittens[0].Renderable, main, float4x4.Identity);
        Program.FrameNumber = 106;
        runtime.UpdateCapes(.1);
        Check(warpResets == 6 && CapeController.WarpResets == warpResets && CapeController.StepCalls == 4,
            "unsupported time warp reseeds once per cape and cannot repeat reset work or step cloth");

        // Warm the actual enabled manager path, including candidate sorting,
        // scheduling and fake solver calls, before measuring allocations.
        Universe.Speed = 1;
        for (int frame = 0; frame < 96; frame++)
        {
            Program.FrameNumber++;
            for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
            runtime.UpdateCapes(.1);
        }
        CapeController.StepOwners.Clear();
        int activeStepsBefore = CapeController.StepCalls;
        long activeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 32; frame++)
        {
            Program.FrameNumber++;
            for (int i = 0; i < 5; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
            runtime.UpdateCapes(.1);
        }
        long activeAllocated = GC.GetAllocatedBytesForCurrentThread() - activeBefore;
        Check(activeAllocated == 0 && CapeController.StepCalls - activeStepsBefore == 2 * 32,
            "the warmed enabled manager loop allocates nothing and retains the fixed two-step frame ceiling");

        runtime.SetKittenCapeEnabled(kittens[0], false);
        Check(runtime._capeOwners.Count == 6 && runtime._kittenSabers[kittens[0]].Cape is not null,
            "a UI opt-out records deferred cleanup without disposing native cape resources during the UI handoff");
        CapeController capeBeforeQuickToggle = runtime._kittenSabers[kittens[0]].Cape!;
        runtime.SetKittenCapeEnabled(kittens[0], true);
        Check(runtime._capeOwners.Count == 6 && ReferenceEquals(capeBeforeQuickToggle, runtime._kittenSabers[kittens[0]].Cape),
            "an off-on quick toggle reuses the pending registry slot and controller without duplicates");
        Check(runtime._capePendingCreationCount == 0,
            "quick toggling an existing cape does not mark it for duplicate creation");
        runtime.SetKittenCapeEnabled(kittens[0], false);
        runtime.UpdateCapes(0);
        Check(runtime._kittenSabers[kittens[0]].Cape is null && CapeController.Disposals == 1
            && runtime._capeOwners.Count == 5 && runtime._capePendingCreationCount == 0,
            "turning a cape off disposes its renderers and private cloth at the next settled update");
        kittens[1].IsDisposed = true;
        system.All.Items = [kittens[0], kittens[2], kittens[3], kittens[4], kittens[5]];
        runtime.RefreshOverlayKittens();
        Check(!runtime._kittenSabers.ContainsKey(kittens[1]) && CapeController.Disposals == 2
            && runtime._capeOwners.Count == 4 && runtime._capePendingCreationCount == 0,
            "boarding or removing a kitten releases its owned cape resources");
        runtime.ResetKittenSabers();
        Check(CapeController.Disposals == CapeController.Constructions && runtime._capeControllers.Count == 0
            && runtime._capeOwners.Count == 0 && runtime._capePendingCreationCount == 0,
            "world reset disposes every remaining cape exactly once");

        CapeController.ResetMetrics();
        var failureRuntime = new SaberRuntime();
        var failureKitten = new KittenEva("Cape setup failure");
        var failureSystem = new CelestialSystem();
        failureSystem.All.Items = [failureKitten];
        Universe.CurrentSystem = failureSystem;
        failureRuntime.RefreshOverlayKittens();
        CapeController.SetupFailure = new InvalidOperationException("test setup fault");
        failureRuntime.SetKittenCapeEnabled(failureKitten, true);
        failureRuntime.UpdateCapes(.016);
        string? failure = failureRuntime.CapeStatusFor(failureKitten);
        failureRuntime.UpdateCapes(.016);
        Check(failureRuntime._kittenSabers[failureKitten].CapeFailureLatched
            && CapeController.SetupAttempts == 1 && CapeController.Constructions == 0
            && failureRuntime._capePendingCreationCount == 0
            && failure == failureRuntime.CapeStatusFor(failureKitten),
            "a per-kitten setup failure is shown and latched without retrying or setting the saber fault");
        failureRuntime.SetKittenCapeEnabled(failureKitten, false);
        failureRuntime.UpdateCapes(.016);
        Check(failureRuntime.CapeStatusFor(failureKitten) is null
            && !failureRuntime._kittenSabers[failureKitten].CapeFailureLatched
            && failureRuntime._capePendingCreationCount == 0,
            "turning the cape off clears its local failure status for a later opt-in");
        failureRuntime.ResetKittenSabers();

        CheckCapeLazySetup();
        CheckCapeSimulationBudgets();
        CheckCapePolicy();
        Universe.Speed = 1;
        Universe.SpeedFailure = null;
        Program.FrameNumber = 0;
    }

    private static void CheckCapeLazySetup()
    {
        CapeController.ResetMetrics();
        Universe.Speed = 1;
        Universe.SpeedFailure = null;
        Universe.SpeedReads = 0;
        Viewport.CameraReads = ViewportRegistry.GameViewReads = 0;
        Program.EditorFlag = false;
        Program.FrameNumber = 250;

        var main = new Viewport(ViewportType.Main) { Visible = false };
        var secondary = new Viewport(ViewportType.Secondary) { Visible = false };
        Program.MainViewport = main;
        ViewportRegistry.SetGameViews(main, secondary);
        var kitten = new KittenEva("Lazy cape") { PositionEcl = new double3(1, 0, 0) };
        var system = new CelestialSystem();
        system.All.Items = [kitten];
        Universe.CurrentSystem = system;
        var runtime = new SaberRuntime();
        runtime.RefreshOverlayKittens();
        runtime._settings.CapeSimulationLimit = 0;
        runtime.SetKittenCapeEnabled(kitten, true);
        runtime.SetKittenCapeEnabled(kitten, false);
        runtime.SetKittenCapeEnabled(kitten, true);
        Check(runtime._capeOwners.Count == 1 && runtime._capePendingCreationCount == 1,
            "a pending off-on toggle retains one owner and one setup request");

        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 0 && runtime._kittenSabers[kitten].Cape is null
            && runtime._capePendingCreationCount == 1 && Viewport.CameraReads == 0
            && Universe.SpeedReads == 0,
            "hidden main and secondary views do not trigger first-time cape setup or simulation work");

        runtime.SetKittenCapeEnabled(kitten, false);
        runtime.UpdateCapes(0);
        Check(runtime._capeOwners.Count == 0 && runtime._capePendingCreationCount == 0,
            "settled opt-out removes a never-created owner and drains its pending setup count");
        runtime.SetKittenCapeEnabled(kitten, true);
        Check(runtime._capeOwners.Count == 1 && runtime._capePendingCreationCount == 1,
            "a later opt-in registers one fresh pending setup request");

        secondary.Visible = true;
        secondary.Camera.PositionEcl = double3.Zero;
        Viewport.CameraReads = ViewportRegistry.GameViewReads = 0;
        kitten.PositionEcl = new double3(60, 0, 0);
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 1 && runtime._kittenSabers[kitten].Cape is not null
            && runtime._capePendingCreationCount == 0 && runtime._capeControllers.Count == 1
            && Viewport.CameraReads == 1 && ViewportRegistry.GameViewReads == 1
            && Universe.SpeedReads == 0 && kitten.VelocityReads == 0,
            "a visible secondary view creates at the exact 60 m boundary even with simulation disabled");

        main.Visible = true;
        main.CameraFailure = new InvalidOperationException("main camera unavailable");
        var secondKitten = new KittenEva("Secondary setup") { PositionEcl = new double3(20, 0, 0) };
        system.All.Items = [kitten, secondKitten];
        runtime.RefreshOverlayKittens();
        runtime.SetKittenCapeEnabled(secondKitten, true);
        Viewport.CameraReads = ViewportRegistry.GameViewReads = 0;
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 2 && runtime._kittenSabers[secondKitten].Cape is not null
            && runtime._capePendingCreationCount == 0 && Viewport.CameraReads == 2
            && ViewportRegistry.GameViewReads == 1 && Universe.SpeedReads == 0,
            "an unavailable main camera does not block a healthy visible secondary view from creating its cape");
        runtime.ResetKittenSabers();
        Check(runtime._capePendingCreationCount == 0 && runtime._capeOwners.Count == 0,
            "world reset clears pending lazy setup state");
    }

    private static void CheckCapeSimulationBudgets()
    {
        CapeController.ResetMetrics();
        Universe.Speed = 1;
        Universe.SpeedFailure = null;
        Program.EditorFlag = false;
        Program.FrameNumber = 300;
        var main = new Viewport(ViewportType.Main);
        Program.MainViewport = main;
        var runtime = new SaberRuntime();
        var kittens = new KittenEva[17];
        for (int i = 0; i < kittens.Length; i++)
            kittens[i] = new KittenEva($"Budget {i}") { PositionEcl = new double3(i + 1, 0, 0) };
        var system = new CelestialSystem();
        system.All.Items = kittens;
        Universe.CurrentSystem = system;
        runtime.RefreshOverlayKittens();
        Check(runtime._settings.CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit
            && SaberSettings.MinimumCapeSimulationLimit == 0
            && SaberSettings.MaximumCapeSimulationLimit == 16
            && CapeUpdatePolicy.MaximumSimulatedCapes == 16,
            "the configurable simulation budget defaults to four and has a fixed zero-to-sixteen range");

        foreach (KittenEva kitten in kittens) runtime.SetKittenCapeEnabled(kitten, true);
        runtime.UpdateCapes(0);
        Check(CapeController.Constructions == 17 && runtime._capeOwners.Count == 17,
            "all enabled capes create cosmetic controllers independently of the simulation candidate budget");

        runtime._settings.CapeSimulationLimit = 1;
        for (int i = 0; i < kittens.Length; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        runtime.UpdateCapes(.1);
        Check(CapeController.StepCalls == 1 && CapeController.StepOwners.SequenceEqual(["Budget 0"]),
            "a budget of one selects only the nearest recently visible cape");

        runtime._settings.CapeSimulationLimit = 16;
        var steppedAtSixteen = new HashSet<string>();
        for (int frame = 0; frame < 8; frame++)
        {
            Program.FrameNumber++;
            for (int i = 0; i < kittens.Length; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
            int before = CapeController.StepCalls;
            runtime.UpdateCapes(.1);
            int calls = CapeController.StepCalls - before;
            Check(calls <= CapeUpdatePolicy.MaxStepCallsPerFrame,
                "raising the candidate budget never raises the two-step frame cap");
            for (int i = before; i < CapeController.StepCalls; i++) steppedAtSixteen.Add(CapeController.StepOwners[i]);
        }
        Check(steppedAtSixteen.Count == 16 && !steppedAtSixteen.Contains("Budget 16"),
            "the sixteen-cape candidate span rotates fairly, excludes the seventeenth nearest-visible cape and never exceeds two steps per frame");

        runtime._settings.CapeSimulationLimit = 1;
        Program.FrameNumber++;
        for (int i = 0; i < kittens.Length; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
        int beforeLowering = CapeController.StepCalls;
        runtime.UpdateCapes(.1);
        Check(CapeController.StepCalls - beforeLowering == 1
            && CapeController.StepOwners[^1] == "Budget 0",
            "lowering to one immediately ignores the old candidate and due-array tails");

        runtime._settings.CapeSimulationLimit = 4;
        var steppedAtFour = new HashSet<string>();
        for (int frame = 0; frame < 2; frame++)
        {
            Program.FrameNumber++;
            for (int i = 0; i < kittens.Length; i++) runtime.DrawCape(kittens[i].Renderable, main, float4x4.Identity);
            int before = CapeController.StepCalls;
            runtime.UpdateCapes(.1);
            Check(CapeController.StepCalls - before <= CapeUpdatePolicy.MaxStepCallsPerFrame,
                "raising the candidate budget to four still keeps the shared two-step cap");
            for (int i = before; i < CapeController.StepCalls; i++) steppedAtFour.Add(CapeController.StepOwners[i]);
        }
        Check(steppedAtFour.SetEquals(["Budget 0", "Budget 1", "Budget 2", "Budget 3"]),
            "raising the budget to four immediately chooses the four nearest capes with a fair schedule");

        Universe.SpeedFailure = new InvalidOperationException("test manager fault");
        runtime.UpdateCapes(.1);
        int releasedAtFault = CapeController.Disposals;
        runtime.UpdateCapes(.1);
        Check(runtime._capeRuntimeFaulted && releasedAtFault == 17 && CapeController.Disposals == releasedAtFault
            && runtime._capeControllers.Count == 0,
            "a manager fault releases each renderer and cloth once, then takes the constant-time fault path");

        runtime.SetKittenCapeEnabled(kittens[0], false);
        Check(runtime.CapeStatusFor(kittens[0]) is null,
            "opting out clears cape failure status before the fault fast path runs");
        runtime.UpdateCapes(.1);
        Check(runtime._capeOwners.Count == 16 && CapeController.Disposals == releasedAtFault,
            "a faulted manager still drains pending opt-out cleanup without releasing twice");

        runtime.SetKittenCapeEnabled(kittens[1], false);
        runtime.SetKittenCapeEnabled(kittens[1], true);
        Universe.SpeedFailure = null;
        runtime.UpdateCapes(0);
        Check(!runtime._capeRuntimeFaulted && CapeController.Constructions == 18
            && runtime._kittenSabers[kittens[1]].Cape is not null
            && runtime._capeOwners.Count == 16,
            "a fresh opt-in clears the manager fault and retries only that kitten's cape controller");
        runtime.ResetKittenSabers();
        Check(CapeController.Disposals == CapeController.Constructions && runtime._capeOwners.Count == 0,
            "budget and fault transitions leave no stale owner or duplicate native disposal");
    }

    private static void CheckCapePolicy()
    {
        CapeUpdatePolicy.Candidate[] nearest = new CapeUpdatePolicy.Candidate[4];
        CapeUpdatePolicy.ClearCandidates(nearest);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 5, 100);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 4, 4);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 3, 9);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 2, 1);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 1, 16);
        Check(nearest[0].Index == 2 && nearest[1].Index == 4 && nearest[2].Index == 3 && nearest[3].Index == 1,
            "allocation-free candidate insertion retains the nearest four in stable order");
        Check(CapeUpdatePolicy.CanSimulateSquared(true, true, 625)
            && !CapeUpdatePolicy.CanSimulateSquared(true, false, 4)
            && !CapeUpdatePolicy.CanSimulateSquared(true, true, 625.5001)
            && CapeUpdatePolicy.CanRenderSquared(3600) && !CapeUpdatePolicy.CanRenderSquared(3601)
            && !CapeUpdatePolicy.CanRenderSquared(-1) && !CapeUpdatePolicy.CanRenderSquared(double.NaN)
            && !CapeUpdatePolicy.CanSimulateSquared(true, true, double.PositiveInfinity),
            "simulation requires recent main-view visibility within 25 m and render culling ends at 60 m");
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 99, double.NaN);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 99, double.PositiveInfinity);
        CapeUpdatePolicy.OfferCandidateSquared(nearest, 99, -1);
        Check(nearest[0].Index == 2 && nearest[3].Index == 1,
            "squared-distance candidate ordering rejects negative and non-finite distances");

        bool[] due = [true, true, true, true];
        CapeUpdatePolicy.Candidate[] owners = [new(0, 0), new(1, 1), new(2, 2), new(3, 3)];
        int[] selected = new int[2];
        int cursor = 0;
        int first = CapeUpdatePolicy.SelectDue(owners, due, selected, ref cursor);
        bool firstPair = first == 2 && selected[0] == 0 && selected[1] == 1;
        int second = CapeUpdatePolicy.SelectDue(owners, due, selected, ref cursor);
        Check(firstPair && second == 2 && selected[0] == 2 && selected[1] == 3,
            "the frame scheduler never exceeds two calls and rotates through every due cape");

        CapeUpdatePolicy.Candidate[] changingRanks = new CapeUpdatePolicy.Candidate[4];
        int[][] rankOrders = [[0, 1, 2, 3], [3, 2, 1, 0], [0, 1, 2, 3], [3, 2, 1, 0]];
        bool[] allDue = [true, true, true, true];
        var servicedOwners = new HashSet<int>();
        int rankCursor = 0;
        foreach (int[] order in rankOrders)
        {
            for (int i = 0; i < order.Length; i++) changingRanks[i] = new(order[i], i);
            int count = CapeUpdatePolicy.SelectDue(changingRanks, allDue, selected, ref rankCursor);
            for (int i = 0; i < count; i++) servicedOwners.Add(changingRanks[selected[i]].Index);
        }
        Check(servicedOwners.SetEquals([0, 1, 2, 3]),
            "round-robin fairness follows stable owner indices when distance ranking changes between frames");

        CapeUpdatePolicy.Candidate[] sparseRanks = [new(2, 0), new(0, 1), new(3, 2), new(1, 3)];
        bool[] oneDue = [false, true, false, false];
        int sparseCursor = 2;
        int sparseCount = CapeUpdatePolicy.SelectDue(sparseRanks, oneDue, selected, ref sparseCursor);
        Check(sparseCount == 1 && selected[0] == 1 && sparseRanks[selected[0]].Index == 0 && sparseCursor == 1,
            "a sparse due set emits one candidate once even when fairness wraps below the owner cursor");
        bool[] noneDue = [false, false, false, false];
        sparseCursor = 7;
        Check(CapeUpdatePolicy.SelectDue(sparseRanks, noneDue, selected, ref sparseCursor) == 0 && sparseCursor == 7,
            "a frame with no due capes leaves the owner fairness cursor unchanged");
        sparseCursor = 7;
        Check(CapeUpdatePolicy.SelectDue(ReadOnlySpan<CapeUpdatePolicy.Candidate>.Empty,
                ReadOnlySpan<bool>.Empty, selected, ref sparseCursor) == 0 && sparseCursor == 0,
            "an empty candidate span resets the owner fairness cursor");

        float elapsed = 0;
        CapeUpdatePolicy.Accumulate(ref elapsed, 1d / 60d, true);
        float partial = elapsed;
        CapeUpdatePolicy.Accumulate(ref elapsed, .2, false);
        bool paused = elapsed == partial && !CapeUpdatePolicy.IsDue(elapsed);
        CapeUpdatePolicy.Accumulate(ref elapsed, .2, true);
        float quantum = CapeUpdatePolicy.Consume(ref elapsed);
        Check(paused && quantum == CapeUpdatePolicy.SimulationInterval && elapsed == 0,
            "the per-cape clock freezes while paused and drops backlog after one consumed quantum");
    }

    private static void CheckToggleReversals(SaberRuntime runtime, KittenEva first, KittenEva second)
    {
        var saber = runtime._kittenSabers[first];
        var model = first.Renderable.Avatar.Core.CharacterModel;
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        runtime.UpdateOtherSabers(.05);
        float raisingBlend = saber.Pose.Blend;
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        Check(saber.Pose.Blend == raisingBlend && saber.Blade.Deployment == 0 && !saber.Blade.TargetOn,
            "cancelling during equip preserves the current pose without igniting");
        runtime.UpdateOtherSabers(.02);
        Check(saber.Pose.Blend < raisingBlend && saber.Pose.Blend > 0 && !saber.Blade.TargetOn,
            "cancelled equip reverses into arm lowering");
        float loweredBlend = saber.Pose.Blend;
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        Check(saber.Pose.Blend == loweredBlend && HasSaberPosePair(first) && runtime.SaberEnabledFor(first),
            "reenabling during unequip keeps the processor pair and starts from the displayed pose");
        runtime.SetControlledKitten(first);
        runtime.AdvanceKittenSaber(saber, .05);
        Check(saber.Pose.Blend > loweredBlend && !saber.Blade.TargetOn,
            "a control switch preserves the pending equip sequence and raises before ignition");
        runtime.AdvanceKittenSaber(saber, .1);
        runtime.AdvanceKittenSaber(saber, .05);
        runtime.AdvanceKittenSaber(saber, .05);
        Check(saber.Blade.Deployment > 0 && saber.Blade.Deployment < 1,
            "the controlled kitten also begins a gradual ignition after equipping");
        float extendingLength = saber.Blade.Deployment;
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        Check(saber.Blade.Deployment == extendingLength && saber.Pose.Blend == 1,
            "disabling during ignition preserves the current blade length");
        runtime.AdvanceKittenSaber(saber, .01);
        float retractingLength = saber.Blade.Deployment;
        Check(retractingLength > 0 && retractingLength < extendingLength && saber.Pose.Blend == 1,
            "partial ignition reverses into retraction while the arm stays raised");
        int stops = saber.Audio.Stops;
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        Check(saber.Blade.Deployment == retractingLength, "reenabling during retraction never resets blade length");
        runtime.AdvanceKittenSaber(saber, .01);
        Check(saber.Blade.Deployment > retractingLength && saber.Blade.TargetOn && saber.Audio.Stops == stops
            && HasSaberPosePair(first), "retraction reverses smoothly into ignition without tearing down resources");
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        float repeatedEnableLength = saber.Blade.Deployment;
        runtime.AdvanceKittenSaber(saber, 0);
        Check(saber.Blade.Deployment == repeatedEnableLength && saber.Blade.TargetOn,
            "repeated enable while paused does not restart ignition");
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        runtime.AdvanceKittenSaber(saber, .1);
        runtime.AdvanceKittenSaber(saber, .05);
        float repeatedDisableBlend = saber.Pose.Blend;
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        runtime.AdvanceKittenSaber(saber, .02);
        Check(saber.Pose.Blend < repeatedDisableBlend && saber.Blade.Deployment == 0,
            "repeated disable continues lowering instead of restarting equip");
        runtime.SetControlledKitten(second);
        runtime.UpdateOtherSabers(.1);
        runtime.UpdateOtherSabers(.1);
        Check(!saber.Blade.Equipped && model.AnimProcessors.Count == 0,
            "switching away during unequip lets the background kitten finish stowing");
    }

    private static void CheckControlledToggle(SaberRuntime runtime, KittenEva first, KittenEva second)
    {
        var saber = runtime._kittenSabers[first];
        // The host control can change after the last safe update. The circle must
        // target the currently controlled EVA, rather than the old runtime alias.
        Program.ControlledVehicle = first;
        runtime._igniteDragOwner = first;
        runtime.QueueControlledSaberToggle();
        Check(runtime.SaberEnabledFor(first) && runtime.SaberEnabledFor(second) && !saber.Blade.Equipped
            && runtime._kittenSaberRequests.Count == 1 && runtime._igniteDragOwner == null,
            "ignite button queues the current host kitten without changing the old controlled kitten or avatar");
        runtime.QueueControlledSaberToggle();
        Check(!runtime.SaberEnabledFor(first) && runtime._kittenSaberRequests.Count == 2,
            "a second quick button click reflects pending intent and queues retract");
        runtime.ApplyKittenSaberRequests(); runtime.UpdateOtherSabers(.1);
        Check(!saber.Blade.Equipped && !saber.Blade.TargetOn && saber.Pose.Kitten == null,
            "ignite followed by retract before the handoff cancels without a visible ignition");
        Program.ControlledVehicle = new object(); runtime.QueueControlledSaberToggle();
        Check(runtime._kittenSaberRequests.Count == 0, "ignite button ignores a controlled vehicle that is not an EVA kitten");
        Program.ControlledVehicle = first; Program.EditorFlag = true; runtime.QueueControlledSaberToggle();
        Program.EditorFlag = false;
        Check(runtime._kittenSaberRequests.Count == 0, "ignite button cannot enqueue in the editor");
        Program.ControlledVehicle = second;
    }

    private static float4x4 StockModelTransform(KittenEva kitten) => float4x4.CreateScale(kitten.Renderable.Avatar.Core.Scale)
        * float4x4.CreateRotationX(-MathF.PI / 2) * float4x4.CreateRotationZ(-MathF.PI / 2)
        * float4x4.CreateFromQuaternion(floatQuat.Pack(kitten.Body2Cce))
        * float4x4.CreateTranslation(float3.Pack(kitten.GetPositionEcl() - Program.RenderedViewport.GetCamera().PositionEcl));

    private static void CheckDropLifecycle()
    {
        var runtime = new SaberRuntime();
        var first = new KittenEva("Drop first"); var second = new KittenEva("Drop second");
        var system = new CelestialSystem(); system.All.Items = [first, second]; Universe.CurrentSystem = system;
        Program.ControlledVehicle = first; Program.RenderedViewport = Program.MainViewport;
        runtime.RefreshOverlayKittens(); runtime.SetControlledKitten(first);
        runtime.QueueKittenSaber(first, true); runtime.QueueKittenSaber(second, true); runtime.ApplyKittenSaberRequests();
        var a = runtime._kittenSabers[first]; var b = runtime._kittenSabers[second];
        void Pose(KittenEva owner, KittenSaber saber)
        {
            Program.FrameNumber++;
            var model = owner.Renderable.Avatar.Core.CharacterModel;
            foreach (var processor in model.AnimProcessors) processor.UpdateSkeleton(StockModelTransform(owner), model.Skeleton, 0);
        }
        for (int i = 0; i < 6; i++) { runtime.AdvanceKittenSaber(a,.1); runtime.AdvanceKittenSaber(b,.1); Pose(first,a); Pose(second,b); }
        runtime.QueueSaberDrop(first);
        Check(a.Drop.Held && a.DroppedBody is null, "Drop UI enqueues without creating native bodies while workers run");
        runtime.ApplySaberDropRequests();
        Check(!runtime.SaberHeldForActions && a.Drop.Phase == SaberDropSequence.Stage.Reaching && a.Blade.TargetOn,
            "drop disables hand attacks immediately while retaining ignition");
        for (int i = 0; i < 5; i++) { runtime.AdvanceKittenSaber(a,.1); Pose(first,a); }
        Check(a.Drop.Phase == SaberDropSequence.Stage.Dropped && a.DroppedBody is not null && a.Blade.Deployment == 1
            && b.Drop.Held && b.DroppedBody is null, "release creates one native owner for the requested kitten and preserves the lit blade");
        runtime.PublishDetachedSaber(first,a);
        var releasedFrame = runtime._renderer.Detached[first];
        int audioBefore = a.Audio.Updates;
        Check(runtime.UpdateDetachedSaberAudio(first,a) && a.Audio.Updates == audioBefore + 1,
            "a dropped saber keeps its own spatial audio channel alive");
        first.PositionEcl += new double3(20, 30, 40);
        runtime.SetControlledKitten(second); runtime.UpdateOtherSabers(.1);
        Check(runtime._renderer.Detached[first].Translation == releasedFrame.Translation && a.Drop.Detached,
            "switching kittens and moving the original owner cannot drag the dropped hilt");
        // Model a later native physics update, far beyond the original release location.
        var driftedFrame = releasedFrame;
        driftedFrame.Translation += new double3(1_000_000, -2_000_000, 3_000_000);
        a.DroppedBody!.MoveTo(driftedFrame);
        runtime.QueueSaberDrop(first); runtime.ApplySaberDropRequests();
        Check(a.Drop.Phase == SaberDropSequence.Stage.Returning && a.DroppedBody is null,
            "Undrop retires the collider at the settled boundary before animated return");
        Check(a.ReturnStart.Equals(driftedFrame) && (a.ReturnStart.Translation - first.PositionEcl).Length() > 1_000_000
            && runtime.TryDetachedSaberFrame(first,a,out var returnStart) && returnStart.Equals(driftedFrame),
            "Undrop begins at the dropped body's current distant world frame without a radius limit or initial snap");
        Program.FrameNumber += 3;
        for (int i=0;i<8;i++) runtime.AdvanceKittenSaber(a,.1);
        Check(a.Drop.Time == 0 && a.Drop.Phase == SaberDropSequence.Stage.Returning,
            "recall waits for an offscreen hand instead of skipping its return animation");
        Pose(first,a); runtime.AdvanceKittenSaber(a,.1);
        bool hasCurrentHand = a.Pose.TryGetBodyFrame(out var currentHand);
        bool hasApproachingFrame = runtime.TryDetachedSaberFrame(first,a,out var approaching);
        Check(hasCurrentHand && hasApproachingFrame,
            "a fresh hand starts the distant return animation");
        var handWorld = currentHand.Translation + first.GetPositionEcl();
        var expected = driftedFrame.Translation + (handWorld - driftedFrame.Translation) * a.Drop.ReturnBlend;
        Check((approaching.Translation - expected).Length() < 1e-7
            && (approaching.Translation - handWorld).Length() > 1000 && !a.Drop.Held,
            "a distant saber travels from its current position with the eased blend instead of snapping to the hand");
        first.PositionEcl += new double3(140,-20,80);
        var handModel = first.Renderable.Avatar.Core.CharacterModel;
        handModel.Skeleton.WorldTransforms[3].Translation += new float3(4,-2,7);
        Pose(first,a);
        hasCurrentHand = a.Pose.TryGetBodyFrame(out currentHand);
        bool hasFollowingFrame = runtime.TryDetachedSaberFrame(first,a,out var followingHand);
        Check(hasCurrentHand && hasFollowingFrame,
            "recall resamples the moving body's current hand pose");
        handWorld = currentHand.Translation + first.GetPositionEcl();
        expected = driftedFrame.Translation + (handWorld - driftedFrame.Translation) * a.Drop.ReturnBlend;
        Check((followingHand.Translation - expected).Length() < 1e-7
            && (followingHand.Translation - approaching.Translation).Length() > 1 && a.ReturnStart.Equals(driftedFrame),
            "the return follows the current hand while retaining its captured distant starting frame");
        for (int i = 0; i < 7; i++) { runtime.AdvanceKittenSaber(a,.1); Pose(first,a); }
        Check(a.Drop.Held && a.Blade.TargetOn && !runtime._renderer.Detached.ContainsKey(first),
            "animated return finishes on a fresh hand attachment without re-igniting");
        runtime.QueueSaberDrop(first); runtime.ApplySaberDropRequests();
        runtime.ResetAllDroppedSabers();
        Check(a.Drop.Held && a.DroppedBody is null && runtime._dropRequests.Count == 0,
            "save capture cancels a pending drop and releases transient ownership");
        runtime.QueueSaberDrop(first); runtime.ApplySaberDropRequests();
        for (int i = 0; i < 5; i++) { runtime.AdvanceKittenSaber(a,.1); Pose(first,a); }
        var ownedBody = a.DroppedBody;
        runtime.QueueKittenSaber(first,false); runtime.ApplyKittenSaberRequests();
        Check(a.Drop.Held && a.DroppedBody is null && ownedBody?.Disposed == true,
            "unequip destroys only that kitten's dropped native body");
        runtime.QueueKittenSaber(first,true); runtime.ApplyKittenSaberRequests();
        for (int i=0;i<6;i++) { runtime.AdvanceKittenSaber(a,.1); Pose(first,a); }
        runtime.QueueSaberDrop(first); runtime.QueueKittenSaber(first,false);
        runtime.ApplyKittenSaberRequests(); runtime.ApplySaberDropRequests();
        Check(a.Drop.Held && a.DroppedBody is null && !a.Blade.TargetOn && runtime._dropRequests.Count == 0,
            "a same-frame explicit stow cancels a pending drop before any collider can be released");
        runtime.ResetKittenSabers();
        Check(runtime._renderer.Detached.Count == 0 && runtime._kittenSabers.Count == 0,
            "world reset clears detached rendering and all per-kitten drop records");
    }

    private static void CheckDropSpinSelection()
    {
        var runtime = new SaberRuntime();
        KittenEva[] owners = [new("Spin zero"),new("Spin one"),new("Spin two"),new("Spin invalid")];
        var system = new CelestialSystem(); system.All.Items = owners; Universe.CurrentSystem = system;
        Program.ControlledVehicle = owners[0]; Program.RenderedViewport = Program.MainViewport;
        runtime.RefreshOverlayKittens(); runtime.SetControlledKitten(owners[0]);
        foreach (var owner in owners) runtime.QueueKittenSaber(owner,true);
        runtime.ApplyKittenSaberRequests();
        void PoseAll()
        {
            Program.FrameNumber++;
            foreach (var owner in owners)
            {
                var model = owner.Renderable.Avatar.Core.CharacterModel;
                foreach (var processor in model.AnimProcessors)
                    processor.UpdateSkeleton(StockModelTransform(owner),model.Skeleton,0);
            }
        }
        for (int i=0;i<6;i++)
        {
            foreach (var owner in owners) runtime.AdvanceKittenSaber(runtime._kittenSabers[owner],.1);
            PoseAll();
        }
        float selectedSpin = 1;
        runtime.QueueSaberDrop(owners[0]); runtime.QueueSaberDrop(owners[1],selectedSpin);
        runtime.QueueSaberDrop(owners[2],5); runtime.QueueSaberDrop(owners[3],float.NaN);
        selectedSpin = 0;
        Check(runtime._kittenSabers[owners[1]].Drop.Held && runtime._kittenSabers[owners[1]].DroppedBody is null,
            "choosing drop spin only queues the native action during UI input");
        Program.ControlledVehicle = owners[2]; runtime.SetControlledKitten(owners[2]);
        runtime.ApplySaberDropRequests();
        float[] expectedSpin = [0,1,2,0];
        Check(owners.Select((owner,index) => runtime._kittenSabers[owner].Drop.SpinMultiplier == expectedSpin[index]).All(value => value),
            "per-kitten queued drop spin is clamped and survives selection changes before the settled handoff");
        foreach (var owner in owners) runtime.AdvanceKittenSaber(runtime._kittenSabers[owner],0);
        Check(runtime._kittenSabers[owners[1]].Drop.SpinMultiplier == 1 && selectedSpin == 0,
            "paused reaching retains its captured spin independently of later UI selection");
        for (int i=0;i<5;i++)
        {
            foreach (var owner in owners) runtime.AdvanceKittenSaber(runtime._kittenSabers[owner],.1);
            PoseAll();
        }
        Check(owners.Select((owner,index) => runtime._kittenSabers[owner].DroppedBody is { } body
                && body.SpinMultiplier == expectedSpin[index]).All(value => value),
            "delayed native release receives each kitten's original zero, one, two or recovered spin setting");
        var recalled = runtime._kittenSabers[owners[1]];
        runtime.QueueSaberDrop(owners[1],float.PositiveInfinity); runtime.ApplySaberDropRequests();
        Check(recalled.Drop.Phase == SaberDropSequence.Stage.Returning && recalled.Drop.SpinMultiplier == 1
            && recalled.DroppedBody is null,
            "Undrop ignores spin input and recalls the existing saber without changing its captured release setting");
        runtime.ResetAllDroppedSabers();
        Check(owners.All(owner => runtime._kittenSabers[owner].Drop.SpinMultiplier == 0),
            "world and save cleanup clear every kitten's transient spin selection");
        runtime.ResetKittenSabers();
    }

    private static void CheckLocomotionTransitions()
    {
        var runtime = new SaberRuntime();
        var kitten = new KittenEva("Moon runner");
        var system = new CelestialSystem();
        system.All.Items = [kitten]; Universe.CurrentSystem = system;
        Program.ControlledVehicle = kitten;
        runtime.RefreshOverlayKittens(); runtime.SetControlledKitten(kitten);
        runtime.QueueKittenSaber(kitten, true); runtime.ApplyKittenSaberRequests();
        var saber = runtime._kittenSabers[kitten];
        for (int i = 0; i < 6; i++) runtime.AdvanceKittenSaber(saber, .1);
        var model = kitten.Renderable.Avatar.Core.CharacterModel;
        Program.RenderedViewport = Program.MainViewport;
        for (int hop = 0; hop < 3; hop++)
        foreach (var mode in new[] { LocomotionMode.Grounded, LocomotionMode.Airborne, LocomotionMode.Tumbling,
            LocomotionMode.Rightening, LocomotionMode.Grounded, LocomotionMode.Mmu })
        {
            kitten.LocomotionState.Mode = mode;
            runtime.AdvanceKittenSaber(saber, .1);
            Program.FrameNumber++;
            model.AnimProcessors[0].UpdateSkeleton(StockModelTransform(kitten), model.Skeleton, 0);
            Check(saber.Blade.Equipped && saber.Blade.TargetOn && saber.Blade.Deployment == 1
                && saber.Pose.Blend == 1 && saber.Pose.HasFreshBodyFrame && runtime.SaberEnabledFor(kitten),
                "running, hopping, stumbling and rightening retain a lit saber and fresh hand pose");
        }
        foreach (var mode in new[] { LocomotionMode.Ladder, LocomotionMode.Swimming })
        {
            kitten.LocomotionState.Mode = mode;
            runtime.AdvanceKittenSaber(saber, .1); runtime.AdvanceKittenSaber(saber, .1);
            Check(saber.Pose.Eligible && saber.Pose.Blend == 1 && saber.Blade.TargetOn
                && saber.Blade.Deployment == 1 && runtime.SaberEnabledFor(kitten),
                "ladder and swimming keep the same resolved hand pose and ignition intent");
            kitten.LocomotionState.Mode = LocomotionMode.Grounded;
            runtime.AdvanceKittenSaber(saber, .1); runtime.AdvanceKittenSaber(saber, .1);
            Check(saber.Pose.Blend == 1 && saber.Blade.TargetOn && saber.Blade.Deployment == 1,
                "returning to walking restores the still-lit saber without a new toggle");
        }
        kitten.LocomotionState.Mode = LocomotionMode.Ladder;
        runtime.QueueKittenSaber(kitten, false); runtime.ApplyKittenSaberRequests();
        for (int i = 0; i < 6; i++) runtime.AdvanceKittenSaber(saber, .1);
        kitten.LocomotionState.Mode = LocomotionMode.Grounded;
        runtime.AdvanceKittenSaber(saber, .1);
        Check(!saber.Blade.Equipped && !runtime.SaberEnabledFor(kitten),
            "an explicit disable during suspension remains off when ground movement resumes");
        runtime.QueueKittenSaber(kitten, true); runtime.ApplyKittenSaberRequests();
        runtime.AdvanceKittenSaber(saber, .05);
        kitten.LocomotionState.Mode = LocomotionMode.Ladder;
        for (int i = 0; i < 4; i++) runtime.AdvanceKittenSaber(saber, .1);
        Check(runtime.SaberEnabledFor(kitten) && saber.TogglePhase == SaberTogglePhase.None && saber.Blade.TargetOn,
            "equipping on a ladder completes with the same live hand solve");
        kitten.LocomotionState.Mode = LocomotionMode.Airborne;
        for (int i = 0; i < 6; i++) runtime.AdvanceKittenSaber(saber, .1);
        Check(saber.Blade.TargetOn && saber.Blade.Deployment == 1,
            "airborne movement keeps the completed equip and ignition");
        runtime.ResetKittenSabers();
        Program.FrameNumber = 0;
    }

    private static void CheckSwimmingLifecycle()
    {
        var runtime = new SaberRuntime();
        var kitten = new KittenEva("Swimmer"); var background = new KittenEva("Other swimmer");
        var system = new CelestialSystem(); system.All.Items = [kitten, background]; Universe.CurrentSystem = system;
        Program.ControlledVehicle = kitten;
        runtime.RefreshOverlayKittens(); runtime.SetControlledKitten(kitten);
        foreach (var owner in new[] { kitten, background }) runtime.QueueKittenSaber(owner, true);
        runtime.ApplyKittenSaberRequests();
        var saber = runtime._kittenSabers[kitten]; var other = runtime._kittenSabers[background];
        for (int i = 0; i < 6; i++) { runtime.AdvanceKittenSaber(saber, .1); runtime.AdvanceKittenSaber(other, .1); }
        Program.RenderedViewport = Program.MainViewport;
        foreach (var state in new[] {
            (LocomotionMode.Grounded, 0f, 0f, false, 0d, "entering the water"),
            (LocomotionMode.Swimming, 0f, 0f, false, -.2d, "stationary surface swimming"),
            (LocomotionMode.Swimming, 0f, 0f, true, -.2d, "parked analytic swimming"),
            (LocomotionMode.Swimming, 4f, MathF.PI / 2, false, -20d, "moving fully underwater"),
            (LocomotionMode.Mmu, 1f, .4f, false, -20d, "underwater MMU movement"),
            (LocomotionMode.Swimming, 0f, 0f, true, -.2d, "resurfacing"),
            (LocomotionMode.Grounded, 0f, 0f, false, 0d, "leaving the water") })
        {
            foreach (var owner in new[] { kitten, background })
            {
                owner.LocomotionState.Mode = state.Item1; owner.LocomotionState.GroundSpeed = state.Item2;
                owner.LocomotionState.SupportRatio = state.Item1 == LocomotionMode.Swimming ? 1 : 0;
                owner.LocomotionState.SwimParked = state.Item4;
                owner.HasPhysicsBubble = !state.Item4;
                owner.Body2Cce = doubleQuat.CreateFromAxisAngle(double3.UnitY, state.Item3);
                owner.PositionEcl = new(0, 0, state.Item5);
            }
            Program.FrameNumber++;
            foreach (var owner in new[] { kitten, background })
            {
                var record = runtime._kittenSabers[owner]; var model = owner.Renderable.Avatar.Core.CharacterModel;
                model.AnimProcessors[0].UpdateSkeleton(StockModelTransform(owner), model.Skeleton, 0);
                runtime.AdvanceKittenSaber(record, .1);
                runtime.SetSaberAutoSwing(owner, true);
                double phase = record.AutoSwing.Phase;
                runtime.AdvanceSaberAutoSwing(record, .1);
                Check(record.Blade.Equipped && record.Blade.TargetOn && record.Blade.Deployment == 1
                    && record.Pose.HasFreshBodyFrame && record.AutoSwing.Phase > phase,
                    state.Item6 + " keeps controlled and background BFL9Ks lit, fresh and automatically swinging");
            }
        }
        kitten.LocomotionState.Mode = LocomotionMode.Swimming;
        Program.FrameNumber += 2;
        double frozen = saber.AutoSwing.Phase;
        runtime.AdvanceSaberAutoSwing(saber, .1); runtime.AdvanceKittenSaber(saber, .1);
        Check(!saber.Pose.HasFreshBodyFrame && saber.AutoSwing.Phase == frozen
            && saber.Blade.Equipped && saber.Blade.TargetOn && saber.Blade.Deployment == 1,
            "a culled swimmer suspends stale automatic motion while preserving equipment and ignition intent");
        Program.FrameNumber++;
        var currentModel = kitten.Renderable.Avatar.Core.CharacterModel;
        currentModel.AnimProcessors[0].UpdateSkeleton(StockModelTransform(kitten), currentModel.Skeleton, 0);
        runtime.AdvanceSaberAutoSwing(saber, .1);
        Check(saber.Pose.HasFreshBodyFrame && saber.AutoSwing.Phase > frozen,
            "a new visible swimming pose resumes automatic motion without a new ignition");
        runtime.ResetKittenSabers(); Program.FrameNumber = 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Kitten saber check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
