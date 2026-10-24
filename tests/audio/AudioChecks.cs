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
        Console.WriteLine("PASS: audio layering, type switching, spatial follow, pause, mute, retraction and cleanup");
    }
}

namespace Brutal.FmodApi
{
    public sealed class TestChannel
    {
        public void TrySet3dDopplerLevel(float value) { }
        public void TrySet3dSpread(float value) { }
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
        public double3 GetPositionEgo(KittenEva owner) => double3.Zero;
        public double3 GetVelocityEgo(KittenEva owner) => double3.Zero;
    }
    public readonly record struct SpatialAudio(double3 Position, double3 Velocity, double Density);
    public sealed record SoundBehavior(string Id);
    public static class ModLibrary
    {
        public static bool TryGet<T>(string id, out T? sound) where T : class
        { sound = new SoundBehavior(id) as T; return sound is not null; }
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
        public bool Paused, Stopped;
        public SpatialAudio Spatial;
        public float VolumeMultiplier { get; set; }
        public float PitchMultiplier { get; set; }
        public bool IsPlaying() => !Stopped;
        public bool IsVirtual() => false;
        public void SetPaused(bool paused) => Paused = paused;
        public void SetSpatialAudio(SpatialAudio spatial) => Spatial = spatial;
        public void UpdateVolume() { }
        public void UpdatePitch() { }
        public void Stop(bool stopImmediate) => Stopped = true;
    }
    public static class GameAudio
    {
        public sealed class TestSystem { public bool IsNull() => false; }
        public static readonly TestSystem System = new();
        public static readonly List<ChannelWrapper> Channels = new();
        public static Camera GetAudioCamera() => new();
        public static void PlaySound(SoundBehavior sound, SpatialAudio spatial, out IChannel? channel,
            float volume, bool startPaused)
        {
            var created = new ChannelWrapper(sound.Id) { Spatial = spatial, VolumeMultiplier = volume, Paused = startPaused };
            Channels.Add(created);
            channel = created;
        }
    }
}
