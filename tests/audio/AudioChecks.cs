using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;

// Exercise real SaberAudio state transitions with only the game/mixer boundary replaced.
internal static class AudioChecks
{
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void Main()
    {
        using var audio = new SaberAudio();
        var owner = new KittenEva();
        void Tick(SaberType type = SaberType.Light, bool paused = false, float deployment = 1,
            float volume = 1, bool on = true) => audio.Update(owner, new double3(2, 3, 4),
                deployment, on, paused, volume, contact: 1, type: type);
        ChannelWrapper Latest(string suffix) => GameAudio.Channels.Last(c => c.Id == "Purr2D2Saber" + suffix);

        Tick();
        Check(!Latest("LightIgnite").Stopped, "Light saber starts its own ignition recording");
        var light = Latest("LightStatic");
        var hum = Latest("Hum");
        var contact = Latest("Contact");
        Check(GameAudio.Channels.Count == 4 && !light.Stopped && !hum.Stopped && !contact.Stopped,
            "static layers alongside ignition, hum and contact");
        Check(light.VolumeMultiplier == .3f && light.Spatial.Position == new double3(2, 3, 4),
            "static uses the moving emitter and configured gain");
        Tick();
        Check(GameAudio.Channels.Count == 4, "repeated updates retain the loop");
        Tick(SaberType.Dark);
        var dark = Latest("DarkStatic");
        Check(light.Stopped && !dark.Stopped && !hum.Stopped && !contact.Stopped
            && GameAudio.Channels.Count == 5, "style change replaces only static without reigniting");
        Tick(SaberType.Dark, paused: true);
        Check(dark.Paused && hum.Paused && GameAudio.Channels.Count == 5, "pause retains channels");
        Tick(paused: true);
        Check(dark.Stopped && GameAudio.Channels.Count == 5, "paused type change defers new playback");
        Tick(deployment: .5f, volume: .5f);
        light = Latest("LightStatic");
        Check(light.VolumeMultiplier == .075f && !light.Paused, "gain follows deployment and volume");
        audio.Suspend();
        Check(light.Paused, "stale pose suspends static");
        Tick();
        Check(!light.Paused, "fresh pose resumes static");
        Tick(volume: 0);
        Check(light.Stopped && hum.Stopped, "mute stops loops");
        Tick(SaberType.Dark);
        dark = Latest("DarkStatic");
        Tick(SaberType.Dark, deployment: 0, on: false);
        Check(dark.Stopped && !Latest("Retract").Stopped, "retraction ends static and keeps retract effect");
        Tick(SaberType.Dark);
        audio.Reset();
        Check(GameAudio.Channels.All(c => c.Stopped), "world reset stops every owned channel");
        int beforePausedIgnition = GameAudio.Channels.Count;
        Tick(SaberType.Dark, paused: true);
        Check(GameAudio.Channels.Count == beforePausedIgnition, "paused ignition starts no sounds");
        Tick(SaberType.Dark);
        Check(!Latest("DarkIgnite").Stopped, "Dark saber starts its own ignition recording on resume");
        Tick(on: false, deployment: 0);
        Check(Latest("DarkIgnite").Stopped && !Latest("Retract").Stopped,
            "reversal stops Dark ignition and plays the common close sound");
        Tick();
        Check(!Latest("LightIgnite").Stopped && Latest("Retract").Stopped,
            "reignition selects the current Light style and cuts the previous transition");
        audio.Shutdown();
        int count = GameAudio.Channels.Count;
        Tick();
        Check(GameAudio.Channels.All(c => c.Stopped) && GameAudio.Channels.Count == count,
            "shutdown stops playback permanently");

        ForceBreakupOneShots();
        ForceBreakupAudioGracefulFailures();
        Console.WriteLine("PASS: audio layering, type switching, spatial follow, pause, mute, retraction and cleanup");
    }

    private static void ForceBreakupOneShots()
    {
        int firstChannel = GameAudio.Channels.Count;
        var owner = new KittenEva();
        using var audio = new ForceBreakupAudio();
        GameAudio.CameraPosition = new double3(10, 20, 30);
        GameAudio.CameraVelocity = new double3(100, 200, 300);

        audio.Initialize();
        audio.PlayGroan(owner, new double3(2, 3, 4), .8f);
        Check(GameAudio.Channels.Count == firstChannel + 1, "groan starts exactly one existing ignition one-shot");
        Check(audio.HasActiveChannels, "active groan is exposed to the runtime poller");
        var groan = GameAudio.Channels[^1];
        Check(groan.Id == "Purr2D2SaberLightIgnite" && !groan.Stopped && !groan.Paused,
            "groan maps to the existing lightsaber ignition sound");
        Check(groan.VolumeMultiplier == .8f * .7f && groan.Spatial.Position == new double3(12, 23, 34)
            && groan.Spatial.Velocity == GameAudio.CameraVelocity && groan.Channel.DopplerLevel == 0,
            "groan uses the body-frame emitter, global volume and zero Doppler");

        audio.Update(owner, new double3(3, 4, 5), .8f);
        audio.Update(owner, new double3(4, 5, 6), .8f);
        Check(GameAudio.Channels.Count == firstChannel + 1 && groan.Spatial.Position == new double3(14, 25, 36),
            "repeated updates only move the one-shot and never replay it");

        audio.PlayBreak(owner, new double3(5, 6, 7), .6f);
        Check(GameAudio.Channels.Count == firstChannel + 2, "break starts a separate one-shot channel");
        var breakup = GameAudio.Channels[^1];
        Check(breakup.Id == "Purr2D2SaberRetract" && !breakup.Stopped && !groan.Stopped,
            "break maps to the existing retract sound and overlaps the groan");
        Check(audio.HasActiveChannels, "overlapping one-shots keep the runtime poller active");

        audio.Update(owner, new double3(7, 8, 9), .4f, paused: true);
        Check(groan.Paused && breakup.Paused
            && groan.Spatial.Position == new double3(17, 28, 39)
            && breakup.Spatial.Position == new double3(17, 28, 39),
            "pause retains and spatially follows both active channels");
        audio.Update(owner, new double3(8, 9, 10), 0, paused: false);
        Check(!groan.Paused && !breakup.Paused && groan.VolumeMultiplier == 0 && breakup.VolumeMultiplier == 0,
            "resume follows the saved global volume, including mute");
        int beforeMutedPlay = GameAudio.Channels.Count;
        audio.PlayGroan(owner, new double3(0, 0, 0), 0);
        audio.PlayBreak(owner, new double3(0, 0, 0), 0);
        Check(GameAudio.Channels.Count == beforeMutedPlay, "muted events do not create new channels");
        breakup.Finished = true;
        audio.Update(owner, new double3(8, 9, 10), 0);
        Check(breakup.Stopped && !groan.Stopped && audio.HasActiveChannels,
            "a finished one-shot releases without cutting its overlapping channel");
        groan.Finished = true;
        audio.Update(owner, new double3(8, 9, 10), 0);
        Check(groan.Stopped && !audio.HasActiveChannels,
            "the runtime poller can stop after every one-shot has finished");

        audio.Reset();
        Check(groan.Stopped && breakup.Stopped && !audio.HasActiveChannels, "reset stops every owned one-shot");
        audio.PlayGroan(owner, new double3(1, 2, 3), 1);
        var staleOwnerChannel = GameAudio.Channels[^1];
        owner.IsDisposed = true;
        audio.Update(owner, new double3(1, 2, 3), 1);
        Check(staleOwnerChannel.Stopped, "disposed owner releases its one-shot");
        owner.IsDisposed = false;

        audio.PlayBreak(owner, new double3(1, 2, 3), 1);
        var shutdownChannel = GameAudio.Channels[^1];
        audio.Shutdown();
        int afterShutdown = GameAudio.Channels.Count;
        audio.PlayGroan(owner, new double3(1, 2, 3), 1);
        audio.Update(owner, new double3(1, 2, 3), 1);
        Check(shutdownChannel.Stopped && !audio.HasActiveChannels && GameAudio.Channels.Count == afterShutdown,
            "shutdown stops channels and prevents later playback");
    }

    private static void ForceBreakupAudioGracefulFailures()
    {
        int firstChannel = GameAudio.Channels.Count;
        var owner = new KittenEva();
        GameAudio.System.Null = true;
        using (var unavailable = new ForceBreakupAudio())
        {
            unavailable.Initialize();
            unavailable.PlayGroan(owner, double3.Zero, 1);
            unavailable.Update(owner, double3.Zero, 1);
            unavailable.Shutdown();
        }
        GameAudio.System.Null = false;
        Check(GameAudio.Channels.Count == firstChannel, "missing audio system fails closed without throwing");

        ModLibrary.MissingIds.Add("Purr2D2SaberLightIgnite");
        using (var missingAsset = new ForceBreakupAudio())
        {
            missingAsset.Initialize();
            missingAsset.PlayGroan(owner, double3.Zero, 1);
            missingAsset.Update(owner, double3.Zero, 1);
            missingAsset.Shutdown();
        }
        ModLibrary.MissingIds.Clear();
        Check(GameAudio.Channels.Count == firstChannel, "missing sound asset fails closed without throwing");

        GameAudio.ReturnNullChannel = true;
        using (var missingChannel = new ForceBreakupAudio())
        {
            missingChannel.Initialize();
            missingChannel.PlayGroan(owner, double3.Zero, 1);
            missingChannel.Update(owner, double3.Zero, 1);
            missingChannel.Shutdown();
        }
        GameAudio.ReturnNullChannel = false;
        Check(GameAudio.Channels.Count == firstChannel, "unavailable mixer channel fails closed without throwing");
    }
}

namespace Brutal.FmodApi
{
    public sealed class TestChannel
    {
        public float DopplerLevel;
        public float Spread;
        public void TrySet3dDopplerLevel(float value) => DopplerLevel = value;
        public void TrySet3dSpread(float value) => Spread = value;
    }
}

namespace KSA
{
    public static class Double3Ex
    {
        public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
    public sealed class KittenEva
    {
        public bool IsDisposed { get; set; }
        public doubleQuat Body2Cce => doubleQuat.Identity;
    }
    public sealed class Camera
    {
        public double3 GetPositionEgo(KittenEva owner) => GameAudio.CameraPosition;
        public double3 GetVelocityEgo(KittenEva owner) => GameAudio.CameraVelocity;
    }
    public readonly record struct SpatialAudio(double3 Position, double3 Velocity, double Density);
    public sealed record SoundBehavior(string Id);
    public static class ModLibrary
    {
        public static readonly HashSet<string> MissingIds = new();
        public static bool TryGet<T>(string id, out T? sound) where T : class
        { sound = MissingIds.Contains(id) ? null : new SoundBehavior(id) as T; return sound is not null; }
    }
    public interface IChannel
    {
        float VolumeMultiplier { get; set; }
        float PitchMultiplier { get; set; }
        bool IsPlaying();
        bool IsVirtual();
        void SetPaused(bool paused);
        void SetSpatialAudio(SpatialAudio spatial);
        void UpdateVolume();
        void UpdatePitch();
        void Stop(bool stopImmediate);
    }
    public sealed class ChannelWrapper(string id) : IChannel
    {
        public readonly string Id = id;
        public readonly Brutal.FmodApi.TestChannel Channel = new();
        public bool Paused, Stopped, Finished;
        public SpatialAudio Spatial;
        public float VolumeMultiplier { get; set; }
        public float PitchMultiplier { get; set; }
        public bool IsPlaying() => !Stopped && !Finished;
        public bool IsVirtual() => false;
        public void SetPaused(bool paused) => Paused = paused;
        public void SetSpatialAudio(SpatialAudio spatial) => Spatial = spatial;
        public void UpdateVolume() { }
        public void UpdatePitch() { }
        public void Stop(bool stopImmediate) => Stopped = true;
    }
    public static class GameAudio
    {
        public sealed class TestSystem { public bool Null; public bool IsNull() => Null; }
        public static readonly TestSystem System = new();
        public static readonly List<ChannelWrapper> Channels = new();
        public static bool ReturnNullChannel;
        public static double3 CameraPosition;
        public static double3 CameraVelocity;
        public static Camera GetAudioCamera() => new();
        public static void PlaySound(SoundBehavior sound, SpatialAudio spatial, out IChannel? channel,
            float volume, bool startPaused)
        {
            if (ReturnNullChannel) { channel = null; return; }
            var created = new ChannelWrapper(sound.Id) { Spatial = spatial, VolumeMultiplier = volume, Paused = startPaused };
            Channels.Add(created);
            channel = created;
        }
    }
}
