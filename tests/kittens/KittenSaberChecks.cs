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
        runtime.SetKittenSaberColor(first, new(1, 0, .2f));
        Check(runtime.SaberColorFor(first) == new float3(1, 0, .2f) && runtime.SaberColorFor(second) == secondPalette
            && !runtime.SaberEnabledFor(first) && runtime._kittenSaberRequests.Count == 0
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "changing one row color neither changes another kitten nor equips, ignites or binds its saber");
        Program.ControlledVehicle = second;
        runtime.SetAppearanceColor(new(.4f, 1, .3f));
        Check(runtime.AppearanceColor == new float3(.4f, 1, .3f) && runtime.SaberColorFor(second) == runtime.DefaultSaberColor
            && runtime.SaberColorFor(first) == new float3(1, 0, .2f),
            "the panel changes its controlled kitten and saved color preference without recoloring existing peers");
        KittenEva third = new("Third"), fourth = new("Fourth"), fifth = new("Fifth");
        system.All.Items = [first, second, third, fourth, fifth]; runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(third) == new float3(49 / 255f, 49 / 255f, 5 / 255f)
            && runtime.SaberColorFor(fourth) == new float3(98 / 255f, 9 / 255f, 96 / 255f)
            && runtime.SaberColorFor(fifth) == firstPalette,
            "new EVA colors cycle yellow, purple and red independently of the saved preference");
        runtime.RefreshOverlayKittens();
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
        Check(firstSaber.Blade.Equipped && firstSaber.Blade.TargetOn && firstSaber.Pose.Eligible
            && firstSaber.Pose.Blend == 0 && firstSaber.Blade.Deployment == 0,
            "enabling a fresh kitten while paused retains ignition intent before its pose blends");
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        Check(ReferenceEquals(firstSaber, runtime._kittenSabers[first])
            && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 1,
            "repeated enable reuses the record and actual Bind adds only one pose processor");

        runtime.QueueKittenSaber(second, true); runtime.ApplyKittenSaberRequests();
        var secondSaber = runtime._kittenSabers[second];
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
        runtime.QueueKittenSaber(first, false); runtime.ApplyKittenSaberRequests();
        Check(!firstSaber.Blade.Equipped && firstModel.AnimProcessors.Count == 0
            && first.Renderable.Avatar.Attachments.Mmu.Enabled && firstSaber.Audio.Stops > 0,
            "unchecking stows the saber, removes its processor, restores MMU and stops channels");
        runtime.QueueKittenSaber(first, true); runtime.ApplyKittenSaberRequests();
        firstSaber.Pose.Update(.1, true);
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
        Check(runtime._kittenSaberRequests.Count == 0 && !runtime._kittenSabers.ContainsKey(first),
            "stale removed kittens cannot enqueue a saber or recreate a color record");
        runtime.QueueKittenSaber(second, false); runtime.ResetKittenSabers();
        Check(runtime._kittenSabers.Count == 0 && runtime._kittenSaberRequests.Count == 0
            && runtime.OverlayKittens.Count == 0 && runtime._activeSaber == null
            && ReferenceEquals(runtime._pose, runtime._idleSaber.Pose)
            && second.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && secondSaber.Audio.Shutdowns == 1 && secondSaber.Audio.Disposals == 1,
            "world reset disposes every remaining kitten, clears queued UI work and restores idle aliases");
        KittenEva afterReset = new("After reset");
        system.All.Items = [afterReset]; runtime.RefreshOverlayKittens();
        Check(runtime.SaberColorFor(afterReset) == firstPalette, "world reset restarts palette assignment at red");
        runtime.ResetKittenSabers();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Kitten saber check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
