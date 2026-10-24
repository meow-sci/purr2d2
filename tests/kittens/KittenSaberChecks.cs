using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    // A narrow fixture for the other runtime partials. Ownership/queue/pruning and
    // all avatar binding/publication code under test come from production sources.
    private BladeState _blade;
    private SaberPoseController _pose;
    private SaberAudio _audio;
    private KittenEva? _owner;
    private readonly FakeRenderer _renderer = new();
    private readonly Resettable _targeting = new(), _contactProbe = new();
    private readonly AssistedSliceSequence _sliceSequence = new();
    private readonly SaberSettings _settings = new();
    private bool _ready = true, _disposed, _selectingSlice, _freshPoseThisTick, _hasPreviousTip;
    private string? _fault;
    private float _swingSpeed;
    private ulong _consumedPoseGeneration;
    private KittenEva? _resetGestureOwner;
    private KittenEva? _kittenColorPickerOwner;
    private void DismissKittenColorPicker() => _kittenColorPickerOwner = null;

    private SaberRuntime()
    {
        _blade = _idleSaber.Blade; _pose = _idleSaber.Pose; _audio = _idleSaber.Audio;
        _disposed = false; _fault = null; _selectingSlice = false;
    }
    private void CancelAssistedSlice(string reason) { _sliceSequence.Cancel(); _pose.ClearSliceSwing(); }
    private void ResetSomersault() { }
    private void ResetOverlaySwing() => _resetGestureOwner = _pose.Kitten;
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
        CheckLocomotionTransitions();
        var runtime = new SaberRuntime();
        KittenEva first = new("First"), second = new("Second");
        var system = new CelestialSystem();
        system.All.Items = [first, second]; Universe.CurrentSystem = system;
        runtime.RefreshOverlayKittens();
        Check(runtime.OverlayKittens.Count == 2 && runtime._kittenSabers.Count == 2
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && second.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "settled fleet snapshot seeds EVA color records without attaching poses");
        var firstPalette = new float3(143 / 255f, 16 / 255f, 16 / 255f);
        var secondPalette = new float3(13 / 255f, 81 / 255f, 1 / 255f);
        Check(runtime.SaberColorFor(first) == firstPalette && runtime.SaberColorFor(second) == secondPalette,
            "first and second EVA kittens receive exact normalized red and green palette entries");
        Check(runtime.SaberTypeFor(first) == SaberType.Light && runtime.SaberTypeFor(second) == SaberType.Light,
            "every newly observed kitten starts with the Light saber style");
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
            && runtime._settings.LightEffects.ArcCount == 1,
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
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 1,
            "repeated enable reuses the record and actual Bind adds only one pose processor");

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
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 1
            && secondSaber.Type == SaberType.Light,
            "changing a lit saber's style preserves its deployment, ignition, pose, color and other owners");
        runtime.SetKittenSaberType(first, SaberType.Dark);
        runtime._kittenColorPickerOwner = first;
        runtime.SetControlledKitten(first); runtime.SetControlledKitten(second);
        Check(ReferenceEquals(runtime._blade, secondSaber.Blade) && ReferenceEquals(runtime._pose, secondSaber.Pose)
            && ReferenceEquals(runtime._audio, secondSaber.Audio) && ReferenceEquals(runtime._owner, second)
            && firstSaber.Blade.TargetOn && secondSaber.Blade.TargetOn
            && !runtime._freshPoseThisTick && !runtime._hasPreviousTip && runtime._swingSpeed == 0
            && runtime.Color == secondSaber.Color && runtime.Color != firstSaber.Color
            && runtime._kittenColorPickerOwner == null
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 1,
            "switching control changes active aliases and preserves both equipped sabers");
        Check(ReferenceEquals(runtime._resetGestureOwner, first),
            "control switch resets the old kitten's gesture before changing pose aliases");

        Program.ControlledVehicle = second;
        Program.FrameNumber = 7; Program.RenderedViewport = Program.MainViewport;
        firstSaber.Pose.Update(.1, true);
        var firstModel = first.Renderable.Avatar.Core.CharacterModel;
        firstModel.AnimProcessors[0].UpdateSkeleton(float4x4.Identity, firstModel.Skeleton, 0);
        Check(firstSaber.Pose.HasFreshBodyFrame && firstSaber.Pose.MainPoseGeneration == 1,
            "actual pose publication accepts a visible EVA kitten that is not controlled");
        runtime.UpdateOtherSabers(0);
        Check(runtime._renderer.Colors.TryGetValue(first, out var renderedColor) && renderedColor == firstSaber.Color
            && renderedColor != secondSaber.Color, "an uncontrolled kitten's point light receives its own color");
        Program.RenderedViewport = new Viewport(ViewportType.Secondary);
        Program.FrameNumber = 8;
        firstModel.AnimProcessors[0].UpdateSkeleton(float4x4.Identity, firstModel.Skeleton, 0);
        Check(firstSaber.Pose.MainPoseGeneration == 1, "secondary view cannot refresh destructive-contact pose generation");
        Program.FrameNumber = 10;
        Check(!firstSaber.Pose.HasFreshBodyFrame, "noncontrolled poses retain the same stale-frame rejection");

        first.Renderable.Avatar.Attachments.Mmu.Enabled = false;
        int stopsBeforeDisable = firstSaber.Audio.Stops;
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        Check(firstSaber.Blade.Equipped && !firstSaber.Blade.TargetOn && firstSaber.Blade.Deployment == 1
            && firstSaber.Pose.Blend == 1 && firstModel.AnimProcessors.Count == 1
            && firstSaber.Audio.Stops == stopsBeforeDisable && !runtime.SaberEnabledFor(first),
            "unchecking requests retraction without removing the hilt, pose or sound");
        runtime.UpdateOtherSabers(0);
        Check(firstSaber.Blade.Deployment == 1 && firstSaber.Pose.Blend == 1,
            "disabling while paused preserves the visible blade and arm until time resumes");
        Program.RenderedViewport = Program.MainViewport; Program.FrameNumber = 11;
        firstModel.AnimProcessors[0].UpdateSkeleton(float4x4.Identity, firstModel.Skeleton, 0);
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
            && firstModel.AnimProcessors.Count == 1 && !first.Renderable.Avatar.Attachments.Mmu.Enabled
            && firstSaber.Audio.Stops == stopsBeforeDisable,
            "unequipping lowers the arm while retaining its hilt, MMU override and audio channels");
        float loweringBlend = firstSaber.Pose.Blend;
        runtime.UpdateOtherSabers(0);
        Check(firstSaber.Pose.Blend == loweringBlend && firstSaber.Blade.Equipped,
            "pausing during unequip freezes the lowering pose without hiding the hilt");
        Program.FrameNumber = 12;
        firstModel.AnimProcessors[0].UpdateSkeleton(float4x4.Identity, firstModel.Skeleton, 0);
        Check(firstSaber.Pose.HasFreshBodyFrame, "the lowering hilt still publishes a fresh hand transform");
        runtime.UpdateOtherSabers(.1); runtime.UpdateOtherSabers(.05);
        Check(!firstSaber.Blade.Equipped && firstModel.AnimProcessors.Count == 0
            && first.Renderable.Avatar.Attachments.Mmu.Enabled && firstSaber.Audio.Stops > stopsBeforeDisable,
            "only a fully lowered arm releases the hilt, processor, MMU override and audio");

        CheckToggleReversals(runtime, first, second);
        CheckControlledToggle(runtime, first, second);
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        runtime.UpdateOtherSabers(.1);
        Check(firstModel.AnimProcessors.Count == 1 && !first.Renderable.Avatar.Attachments.Mmu.Enabled,
            "reenabling binds one fresh processor and hides the MMU again");

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
            && secondSaber.Audio.Shutdowns == 1 && secondSaber.Audio.Disposals == 1,
            "world reset disposes every remaining kitten, clears queued UI work and restores idle aliases");
        KittenEva afterReset = new("After reset");
        system.All.Items = [afterReset]; runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(afterReset) == firstPalette && runtime.SaberTypeFor(afterReset) == SaberType.Light,
            "world reset starts a new Light saber session and restarts palette assignment at red");
        runtime.ResetKittenSabers();
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
        Check(saber.Pose.Blend == loweredBlend && model.AnimProcessors.Count == 1 && runtime.SaberEnabledFor(first),
            "reenabling during unequip keeps one processor and starts from the displayed pose");
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
            && model.AnimProcessors.Count == 1, "retraction reverses smoothly into ignition without tearing down resources");
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
        runtime.QueueControlledSaberToggle();
        Check(runtime.SaberEnabledFor(first) && runtime.SaberEnabledFor(second) && !saber.Blade.Equipped
            && runtime._kittenSaberRequests.Count == 1,
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
            model.AnimProcessors[0].UpdateSkeleton(float4x4.Identity, model.Skeleton, 0);
            Check(saber.Blade.Equipped && saber.Blade.TargetOn && saber.Blade.Deployment == 1
                && saber.Pose.Blend == 1 && saber.Pose.HasFreshBodyFrame && runtime.SaberEnabledFor(kitten),
                "running, hopping, stumbling and rightening retain a lit saber and fresh hand pose");
        }
        foreach (var mode in new[] { LocomotionMode.Ladder, LocomotionMode.Swimming })
        {
            kitten.LocomotionState.Mode = mode;
            runtime.AdvanceKittenSaber(saber, .1); runtime.AdvanceKittenSaber(saber, .1);
            Check(!saber.Pose.Eligible && !saber.Pose.HasFreshBodyFrame && saber.Blade.TargetOn
                && saber.Blade.Deployment == 1 && runtime.SaberEnabledFor(kitten),
                "unsupported hand poses suspend presentation without clearing ignition intent");
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
        Check(runtime.SaberEnabledFor(kitten) && saber.TogglePhase == SaberTogglePhase.Equipping && !saber.Blade.TargetOn,
            "a temporary pose loss during equip retains the queued ignition");
        kitten.LocomotionState.Mode = LocomotionMode.Airborne;
        for (int i = 0; i < 6; i++) runtime.AdvanceKittenSaber(saber, .1);
        Check(saber.Blade.TargetOn && saber.Blade.Deployment == 1,
            "interrupted equip completes automatically once a supported pose returns");
        runtime.ResetKittenSabers();
        Program.FrameNumber = 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Kitten saber check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
