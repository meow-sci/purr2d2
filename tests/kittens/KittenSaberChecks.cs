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
    private float3 Color => new(.1f, .8f, 1);
    private KittenEva? _resetGestureOwner;

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
        internal void RemoveEmitter(KittenEva owner) => Emitters.Remove(owner);
        internal void SetEmitter(KittenEva owner, double4x4 frame, float length, float3 color) => Emitters.Add(owner);
    }

    public static void Main()
    {
        var runtime = new SaberRuntime();
        KittenEva first = new("First"), second = new("Second");
        var system = new CelestialSystem();
        system.All.Items = [first, second]; Universe.CurrentSystem = system;
        runtime.RefreshOverlayKittens();
        Check(runtime.OverlayKittens.Count == 2 && runtime._kittenSabers.Count == 0,
            "settled fleet snapshot lists all EVA kittens without attaching poses");

        runtime.QueueKittenSaber(first, true);
        Check(runtime._kittenSabers.Count == 0 && first.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
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
        runtime.SetControlledKitten(first); runtime.SetControlledKitten(second);
        Check(ReferenceEquals(runtime._blade, secondSaber.Blade) && ReferenceEquals(runtime._pose, secondSaber.Pose)
            && ReferenceEquals(runtime._audio, secondSaber.Audio) && ReferenceEquals(runtime._owner, second)
            && firstSaber.Blade.TargetOn && secondSaber.Blade.TargetOn
            && !runtime._freshPoseThisTick && !runtime._hasPreviousTip && runtime._swingSpeed == 0
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

        runtime._renderer.Emitters.Add(first); first.IsDisposed = true;
        system.All.Items = [second]; runtime.RefreshOverlayKittens();
        Check(!runtime._kittenSabers.ContainsKey(first) && firstModel.AnimProcessors.Count == 0
            && first.Renderable.Avatar.Attachments.Mmu.Enabled && firstSaber.Audio.Shutdowns == 1
            && firstSaber.Audio.Disposals == 1 && !runtime._renderer.Emitters.Contains(first)
            && secondSaber.Blade.TargetOn, "boarding or disposing an uncontrolled EVA releases only that kitten's owned resources");
        runtime.RefreshOverlayKittens();
        Check(firstSaber.Audio.Disposals == 1, "repeated fleet refresh cannot dispose a removed kitten twice");

        runtime.QueueKittenSaber(first, true);
        Check(runtime._kittenSaberRequests.Count == 0, "stale removed kittens cannot enqueue a new saber");
        runtime.QueueKittenSaber(second, false); runtime.ResetKittenSabers();
        Check(runtime._kittenSabers.Count == 0 && runtime._kittenSaberRequests.Count == 0
            && runtime.OverlayKittens.Count == 0 && runtime._activeSaber == null
            && ReferenceEquals(runtime._pose, runtime._idleSaber.Pose)
            && second.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0
            && secondSaber.Audio.Shutdowns == 1 && secondSaber.Audio.Disposals == 1,
            "world reset disposes every remaining kitten, clears queued UI work and restores idle aliases");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Kitten saber check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
