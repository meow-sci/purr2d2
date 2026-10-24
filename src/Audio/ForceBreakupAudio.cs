using System;
using Brutal.FmodApi;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Audio;

/// <summary>Owns the short placeholder sounds played by the Force breakup gesture.</summary>
internal sealed class ForceBreakupAudio : IDisposable
{
    private const string GroanSoundId = "Purr2D2SaberLightIgnite";
    private const string BreakSoundId = "Purr2D2SaberRetract";
    private const float PlaybackGain = .7f;

    private SoundBehavior? _groanSound;
    private SoundBehavior? _breakSound;
    private IChannel? _groan;
    private IChannel? _break;
    private KittenEva? _owner;
    private bool _initializationAttempted;
    private bool _paused;
    private bool _shutdown;
    private bool _disposed;
    private bool _warnedPlayback;

    internal string? LastError { get; private set; }
    internal bool HasActiveChannels => _groan is not null || _break is not null;

    /// <summary>Resolve the existing one-shot assets after StarMapAllModsLoaded.</summary>
    public void Initialize()
    {
        if (_initializationAttempted || _shutdown || _disposed) return;
        try
        {
            if (GameAudio.System.IsNull()) return;
            _initializationAttempted = true;
            _groanSound = Resolve(GroanSoundId);
            _breakSound = Resolve(BreakSoundId);
        }
        catch (Exception ex)
        {
            WarnPlayback(ex.Message);
        }
    }

    public void PlayGroan(KittenEva owner, double3 emitterBody, float volume) =>
        Play(owner, emitterBody, volume, true, ref _groan);

    public void PlayBreak(KittenEva owner, double3 emitterBody, float volume) =>
        Play(owner, emitterBody, volume, false, ref _break);

    /// <summary>Move active one-shots with their emitter until playback completes.</summary>
    public void Update(KittenEva owner, double3 emitterBody, float volume, bool paused = false)
    {
        if (_shutdown || _disposed) return;
        if (owner is null || owner.IsDisposed || !Finite(emitterBody)) { Stop(); return; }
        if (!ReferenceEquals(_owner, owner)) { Reset(); _owner = owner; }
        _paused = paused;
        volume = Clamp(volume, 0, 1);
        Initialize();
        if (_groan is null && _break is null) return;

        try
        {
            SpatialAudio spatial = GetSpatial(owner, emitterBody);
            Follow(ref _groan, spatial, volume * PlaybackGain, paused);
            Follow(ref _break, spatial, volume * PlaybackGain, paused);
        }
        catch (Exception ex)
        {
            WarnPlayback(ex.Message);
            Cut(ref _groan);
            Cut(ref _break);
        }
    }

    private void Play(KittenEva owner, double3 emitterBody, float volume, bool groan, ref IChannel? channel)
    {
        if (_shutdown || _disposed) return;
        if (owner is null || owner.IsDisposed || !Finite(emitterBody)) { Stop(); return; }
        if (!ReferenceEquals(_owner, owner)) { Reset(); _owner = owner; }
        volume = Clamp(volume, 0, 1);
        Initialize();
        SoundBehavior? sound = groan ? _groanSound : _breakSound;
        if (sound is null || volume <= 0) return;

        try
        {
            // A repeated event cannot restart the same sound while its one-shot is still active.
            if (Alive(channel)) return;
            Cut(ref channel);
            channel = Start(sound, GetSpatial(owner, emitterBody), volume * PlaybackGain);
        }
        catch (Exception ex)
        {
            WarnPlayback(ex.Message);
            Cut(ref channel);
        }
    }

    private static SpatialAudio GetSpatial(KittenEva owner, double3 emitterBody)
    {
        Camera camera = GameAudio.GetAudioCamera();
        double3 position = camera.GetPositionEgo(owner) + emitterBody.Transform(owner.Body2Cce);
        // Keep coordinates current for spatialization while disabling orbital Doppler in the channel.
        return new SpatialAudio(position, camera.GetVelocityEgo(owner), 1.0);
    }

    private SoundBehavior? Resolve(string id)
    {
        try
        {
            if (ModLibrary.TryGet<SoundBehavior>(id, out var sound) && sound is not null) return sound;
            LastError = $"Audio asset '{id}' is missing; check mod.toml and Assets/Sounds.xml.";
        }
        catch (Exception ex) { LastError = $"Audio asset '{id}' could not resolve: {ex.Message}"; }
        Console.WriteLine($"purr2d2: {LastError}");
        return null;
    }

    private IChannel? Start(SoundBehavior sound, SpatialAudio spatial, float volume)
    {
        // Configure while paused so a one-shot never spends a frame at the listener origin.
        GameAudio.PlaySound(sound, spatial, out IChannel? channel, volume, startPaused: true);
        if (channel is ChannelWrapper wrapper)
        {
            wrapper.Channel.TrySet3dDopplerLevel(0);
            wrapper.Channel.TrySet3dSpread(0);
        }
        channel?.SetPaused(_paused);
        return channel;
    }

    private static void Follow(ref IChannel? channel, SpatialAudio spatial, float volume, bool paused)
    {
        if (!Alive(channel)) { Cut(ref channel); return; }
        channel!.SetSpatialAudio(spatial);
        channel.VolumeMultiplier = volume;
        channel.UpdateVolume();
        channel.SetPaused(paused);
    }

    private static bool Alive(IChannel? channel) => channel is not null && (channel.IsPlaying() || channel.IsVirtual());

    private static void Cut(ref IChannel? channel)
    {
        IChannel? old = channel;
        channel = null;
        try { old?.Stop(stopImmediate: true); }
        catch { /* Invalid/stolen handles and a closing mixer are already stopped. */ }
    }

    /// <summary>Immediately end all owned one-shots without starting another sound.</summary>
    public void Stop()
    {
        Cut(ref _groan);
        Cut(ref _break);
        _owner = null;
        _paused = false;
    }

    /// <summary>Forget the current owner after a world, scene or EVA transition.</summary>
    public void Reset() => Stop();

    /// <summary>Call before GameAudio.Release.</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        Stop();
        _shutdown = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private void WarnPlayback(string message)
    {
        LastError = message;
        if (_warnedPlayback) return;
        _warnedPlayback = true;
        Console.WriteLine($"purr2d2: Force breakup audio: {message}");
    }

    private static float Clamp(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
