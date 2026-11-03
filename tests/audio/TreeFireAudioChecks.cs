using System.Xml.Linq;
using Brutal.Numerics;
using KSA;
using Purr2D2.Rendering;

internal static class TreeFireAudioChecks
{
    internal static void Run()
    {
        Manifest();
        AdmissionAndSnapshot();
        SharedAnchorsAndMovingEmitter();
        ExpiryEvictionAndReset();
        PauseWarpAndFrozenClock();
        BoundedFailuresAndRecovery();
        Console.WriteLine("PASS: production fire-source/audio lifecycle, per-fragment deduplication, moving late-camera spatial audio, exact configured player-time duration, hidden/particles-off playback, capacity/reset/release, pause/warp and bounded failures");
    }

    private static void Manifest()
    {
        var assets = XDocument.Load("Assets/Sounds.xml");
        var sound = assets.Root!.Elements("Sound").Single(s => (string?)s.Attribute("Id") == "Purr2D2TreeFireLoop");
        var file = sound.Element("SoundFile")!;
        var loop = file.Element("Loop");
        var spatial = assets.Root.Elements("SpatialSoundData").Single(s => (string?)s.Attribute("Id") == "Purr2D2TreeFireSpatial");
        Check((string?)sound.Attribute("ChannelId") == "Sfx" && (string?)sound.Attribute("SimSpeed") == "Ignore"
            && (string?)file.Attribute("Path") == "Assets/Audio/fire-crackling.ogg"
            && (bool?)file.Attribute("TwoD") != true && (bool?)sound.Attribute("TwoD") != true
            && loop != null && (bool?)loop.Attribute("RandomStartTime") == true && !loop.Elements().Any()
            && (string?)sound.Element("SpatialSoundData")?.Attribute("Id") == "Purr2D2TreeFireSpatial"
            && (int?)spatial.Element("VolumeRange")?.Attribute("Min") == 1
            && (int?)spatial.Element("VolumeRange")?.Attribute("Max") == 35,
            "actual sound asset declares Sfx/Ignore, the supplied full-file loop, randomized fragment phases and 1–35m spatial falloff");
        Check(File.ReadAllBytes("Assets/Audio/fire-crackling.ogg").AsSpan(0, 4).SequenceEqual("OggS"u8), "the declared supplied Ogg is packaged");
    }

    private static void AdmissionAndSnapshot()
    {
        Reset(); using var renderer = new SaberRenderer(); var part = Part();
        foreach (double duration in new[] { 0, -1, double.NaN, double.PositiveInfinity }) Fire(renderer, part, 10, duration);
        renderer.AddTreeCutFire(part, new(double.NaN, 0, 0), double3.UnitY, 10, 2);
        Fire(renderer, new Part(), 10, 2);
        renderer.Tick(10); renderer.AudioTick();
        Check(renderer.Sources == 0 && GameAudio.Plays == 0, "zero/invalid durations, points and unavailable owners never start fire audio");
        double configured = 3;
        Fire(renderer, part, 10, configured); configured = 60;
        renderer.Tick(10); renderer.AudioTick(); var channel = Latest();
        renderer.Tick(12.999); renderer.AudioTick();
        Check(!channel.Stopped && renderer.Sources == 1, "the captured fire-duration setting remains active until its exact end");
        renderer.Tick(13);
        Check(channel.Stopped && channel.Immediate && renderer.Sources == 0, "source expiry stops audio immediately without another audio frame");
        Fire(renderer, part, 20, 100); renderer.Tick(20); renderer.AudioTick(); channel = Latest();
        renderer.Tick(80);
        Check(channel.Stopped && renderer.Sources == 0, "audio follows the production 60-second duration clamp");
    }

    private static void SharedAnchorsAndMovingEmitter()
    {
        Reset(); using var renderer = new SaberRenderer(); var part = Part();
        part.Local = double4x4.CreateTranslation(2, 0, 0);
        part.Tree!.OwningVehicle.World = double4x4.CreateTranslation(10, 20, 30);
        part.Tree.OwningVehicle.Velocity = new(-3, 2, 1);
        foreach (var point in new[] { new double3(0, 0, 0), new(2, 0, 0), new(0, 2, 0), new(2, 2, 0) })
            renderer.AddTreeCutFire(part, point, double3.UnitY, 0, 4);
        renderer.Tick(0);
        // Camera moves after the fire clock/source update, before the native audio hook.
        GameAudio.Camera.Position = new(1, 2, 3); GameAudio.Camera.Outside = true;
        GameSettings.Current.Graphics.Particles = false;
        renderer.SetVolume(.6f); renderer.Draw(); renderer.AudioTick();
        var channel = Latest();
        Check(renderer.Sources == 4 && GameAudio.Plays == 1 && renderer.RibbonDraws == 0
            && GameAudio.LastStartPaused && GameAudio.LastAlwaysPlay && !channel.Paused
            && channel.VolumeMultiplier == .6f && channel.PitchMultiplier == 1
            && channel.Spatial.Position == new double3(12, 19, 27)
            && channel.Spatial.Velocity == new double3(-3, 2, 1) && channel.Spatial.Pressure == 1
            && channel.Channel.Doppler == 0 && channel.Channel.Spread == 0,
            "four offscreen/particles-disabled visual anchors share one correctly positioned, configured moving fragment voice");
        part.Local = double4x4.CreateTranslation(5, 0, 0);
        part.Tree.OwningVehicle.World = double4x4.CreateRotationZ(Math.PI / 2) * double4x4.CreateTranslation(20, 30, 40);
        GameAudio.Camera.Position = new(2, 3, 4); renderer.Tick(.1); renderer.AudioTick();
        Near(channel.Spatial.Position, new(17, 33, 36));
        Check(GameAudio.Plays == 1 && !channel.Stopped, "a moving and rotating cut fragment updates its original loop rather than restarting it");
        renderer.AddTreeCutFire(part, new(6, 2, 0), double3.UnitY, 1, 8);
        renderer.Tick(4); renderer.AudioTick();
        Check(renderer.Sources == 1 && !channel.Stopped && GameAudio.Plays == 1, "one part voice remains until its last independently captured anchor expires");
        renderer.Tick(9); Check(channel.Stopped, "latest retained anchor expiration ends the grouped voice");
    }

    private static void ExpiryEvictionAndReset()
    {
        Reset(); using var renderer = new SaberRenderer(); var first = Part();
        Fire(renderer, first, 0, 20); renderer.Tick(0); renderer.AudioTick(); var evicted = Latest();
        for (int i = 0; i < 32; i++) Fire(renderer, Part(), 0, 20);
        Check(renderer.Sources == 32 && evicted.Stopped, "capacity eviction immediately stops a part whose final visual anchor was evicted");
        renderer.AudioTick();
        Check(GameAudio.Channels.Count(c => !c.Stopped) == 32, "capacity replacement creates at most one voice for each of the 32 retained sources");
        renderer.ClearTreeFire(); Check(GameAudio.Channels.All(c => c.Stopped), "explicit clear/world reset stops every voice");
        Fire(renderer, first, 1, 4); renderer.Tick(1); renderer.AudioTick(); var disposedOwner = Latest();
        first.Tree!.OwningVehicle.IsDisposed = true; renderer.AudioTick();
        Check(disposedOwner.Stopped, "disposed fragment cleanup does not wait for a future fire-clock update");
        first.Tree.OwningVehicle.IsDisposed = false;
        renderer.ClearTreeFire(); Fire(renderer, first, 2, 4); renderer.Tick(2); renderer.AudioTick(); var oldScene = Latest();
        Universe.CurrentSystem = new(); renderer.AudioTick();
        Check(oldScene.Stopped, "scene replacement releases old-system voices");
        renderer.ClearTreeFire(); Fire(renderer, Part(), 3, 4); renderer.Tick(3); renderer.AudioTick(); var release = Latest();
        renderer.ReleaseAudio();
        Check(release.Stopped && renderer.Sources == 0, "native mixer-release prefix clears loops before audio teardown");
        Fire(renderer, Part(), 4, 4); renderer.Tick(4); renderer.AudioTick(); var unload = Latest();
        renderer.Dispose(); renderer.AudioTick();
        Check(unload.Stopped, "renderer unload/disposal stops voices and permits no subsequent playback");
    }

    private static void PauseWarpAndFrozenClock()
    {
        Reset(); using var renderer = new SaberRenderer(); var part = Part();
        Universe.SimulationSpeed = 0; Fire(renderer, part, 5, 2); renderer.Tick(5); renderer.AudioTick(); var paused = Latest();
        Check(!paused.Paused, "pause follows the existing player-time fire policy instead of imposing a second timer/pause gate");
        for (int i = 0; i < 20; i++) renderer.AudioTick();
        Check(!paused.Stopped && GameAudio.Plays == 1, "a frozen authoritative fire clock has no wall-clock expiry or repeated playback");
        renderer.Tick(7); Check(paused.Stopped, "paused simulation still expires when the actual player-time fire source expires");
        Universe.SimulationSpeed = 1000; Fire(renderer, part, 8, 1); renderer.Tick(8); renderer.AudioTick(); var warped = Latest();
        Check(GameAudio.LastAlwaysPlay && !warped.Paused, "new warp-time fire playback bypasses the native 10x gate just like its visible fire");
        renderer.Tick(9); Check(warped.Stopped, "warp does not stretch the captured player-time duration");
    }

    private static void BoundedFailuresAndRecovery()
    {
        Reset(); using var renderer = new SaberRenderer(); var part = Part();
        GameAudio.System.Null = true; Fire(renderer, part, 0, 30); renderer.Tick(0); renderer.AudioTick();
        Check(GameAudio.Plays == 0 && ModLibrary.Gets == 0, "missing audio system does not create channels or consume asset retries");
        GameAudio.System.Null = false; ModLibrary.Missing = true;
        for (int i = 0; i < 20; i++) { renderer.Tick(i * .5); renderer.AudioTick(); }
        Check(ModLibrary.Gets == 4 && GameAudio.Plays == 0, "missing sound resolution is bounded and never escapes a frame");
        ModLibrary.Missing = false; renderer.ClearTreeFire(); Fire(renderer, part, 10, 20); renderer.Tick(10); renderer.AudioTick();
        Check(GameAudio.Plays == 1, "a fresh fire after reset can resolve a newly available sound");
        var channel = Latest(); channel.Finished = true; channel.Virtual = true;
        renderer.Tick(10.1); renderer.AudioTick();
        Check(GameAudio.Plays == 1 && !channel.Stopped, "virtualized channels remain owned and are not duplicated");
        channel.Virtual = false;
        renderer.Tick(10.2); renderer.AudioTick();
        Check(GameAudio.Plays == 1 && channel.Stopped, "a stolen/completed loop observes restart cooldown");
        renderer.Tick(10.3); renderer.AudioTick();
        Check(GameAudio.Plays == 2, "a lost native channel restarts while its fire remains alive");
        for (int i = 0; i < 6; i++) { Latest().Finished = true; renderer.Tick(10.6 + i * .3); renderer.AudioTick(); }
        Check(GameAudio.Plays == 8, "successful acquisitions reset consecutive-failure limits for later legitimate steals");
        renderer.SetVolume(0); renderer.AudioTick();
        Check(Latest().Stopped, "muting releases fire loops");
        renderer.SetVolume(.4f); renderer.AudioTick();
        Check(Latest().VolumeMultiplier == .4f && !Latest().Stopped, "unmuting resumes only the remaining visual fire interval");
        renderer.ClearTreeFire(); GameAudio.ReturnNull = true; Fire(renderer, part, 15, 10);
        int before = GameAudio.Plays;
        for (int i = 0; i < 20; i++) { renderer.Tick(15 + i * .2); renderer.AudioTick(); }
        Check(GameAudio.Plays - before == 4, "null native channels receive only four backoff attempts");
        renderer.ClearTreeFire(); GameAudio.ReturnNull = false; GameAudio.NextChannelInvalid = true; Fire(renderer, part, 20, 10);
        before = GameAudio.Plays;
        for (int i = 0; i < 20; i++) { renderer.Tick(20 + i * .2); renderer.AudioTick(); }
        Check(GameAudio.Plays - before == 4, "invalid/stolen native handles remain bounded and cannot throw out of audio frames");
        renderer.ClearTreeFire(); GameAudio.NextChannelInvalid = false; GameAudio.ThrowCamera = true; Fire(renderer, part, 25, 4);
        before = GameAudio.CameraReads;
        for (int i = 0; i < 20; i++) { renderer.Tick(25 + i * .1); renderer.AudioTick(); }
        Check(GameAudio.CameraReads - before <= 4, "camera failures receive the same bounded retry policy");
        renderer.ClearTreeFire(); GameAudio.ThrowCamera = false; Fire(renderer, part, 28, 3); renderer.Tick(28); renderer.AudioTick();
        channel = Latest(); channel.ThrowOnFollow = true; channel.ThrowOnStop = true;
        renderer.Tick(28.1); renderer.AudioTick(); renderer.ClearTreeFire();
        Check(channel.Stopped, "failed follow/stop calls are contained and relinquish their channel");
    }

    private static Part Part() => new() { Tree = new() { OwningVehicle = new() } };
    private static void Fire(SaberRenderer renderer, Part part, double now, double seconds) => renderer.AddTreeCutFire(part, double3.Zero, double3.UnitY, now, seconds);
    private static ChannelWrapper Latest() => GameAudio.Channels[^1];
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double3 actual, double3 expected) => Check((actual - expected).Length() < 1e-9, $"moving spatial point mismatch: {actual} != {expected}");
    private static void Reset()
    {
        Universe.CurrentSystem = new(); Universe.SimulationSpeed = 1;
        GameAudio.Channels.Clear(); GameAudio.Plays = GameAudio.CameraReads = 0; ModLibrary.Gets = 0; ModLibrary.Missing = false;
        GameAudio.System.Null = GameAudio.ReturnNull = GameAudio.ThrowCamera = GameAudio.ThrowPlay = GameAudio.NextChannelInvalid = false;
        GameAudio.Camera.Position = double3.Zero; GameAudio.Camera.Outside = false; GameSettings.Current.Graphics.Particles = true;
    }
}
