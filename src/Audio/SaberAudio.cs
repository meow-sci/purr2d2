using System;
using Brutal.FmodApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Audio;

/// <summary>
/// Owns one saber's moving SFX channels. Call on the main thread, after the camera and
/// hand pose have updated, once per frame even when the HUD is hidden.
/// </summary>
public sealed class SaberAudio : IDisposable
{
    private const int MaxLoopStartsPerTransition = 4;
    private const float AudibleDeployment = 0.001f;
    private const float TransitionGain = 0.7f;
    private const float LightIgniteGain = 0.75f;
    private const float DarkAmbientGain = 0.7f;
    private const float ClashGain = 0.8f;
    private const float SwooshGain = TransitionGain * LightIgniteGain;
    private const float SliceGain = 1f;
    private SoundBehavior? _lightIgniteSound;
    private SoundBehavior? _darkIgniteSound;
    private SoundBehavior? _retractSound;
    private SoundBehavior? _humSound;
    private SoundBehavior? _lightStaticSound;
    private SoundBehavior? _darkStaticSound;
    private SoundBehavior? _contactSound;
    private SoundBehavior? _sliceSound;
    private IChannel? _transition;
    private IChannel? _hum;
    private IChannel? _static;
    private SaberType _type;
    private IChannel? _contact;
    private OneShot _clash, _swoosh, _slice;
    private struct OneShot
    {
        internal IChannel? Channel;
        internal double3 PointBody;
    }
    private float _transitionGain = TransitionGain;
    private KittenEva? _owner;
    private bool _initializationAttempted;
    private bool _lastTargetOn;
    private bool _transitionPending;
    private bool _shutdown;
    private bool _disposed;
    private bool _warnedPlayback;
    private int _humStarts;
    private int _staticStarts;
    private int _contactStarts;
    private long _nextHumStart;
    private long _nextStaticStart;
    private long _nextContactStart;

    public bool Ready => _humSound is not null && !_shutdown && !_disposed;
    public string? LastError { get; private set; }

    /// <summary>Resolve XML assets after StarMapAllModsLoaded; safe to call repeatedly.</summary>
    public void Initialize()
    {
        if (_initializationAttempted || _shutdown || _disposed || GameAudio.System.IsNull()) return;
        _initializationAttempted = true;
        _lightIgniteSound = Resolve("Purr2D2SaberLightIgnite");
        _darkIgniteSound = Resolve("Purr2D2SaberDarkIgnite");
        _retractSound = Resolve("Purr2D2SaberRetract");
        _humSound = Resolve("Purr2D2SaberHum");
        _lightStaticSound = Resolve("Purr2D2SaberLightStatic");
        _darkStaticSound = Resolve("Purr2D2SaberDarkStatic");
        _contactSound = Resolve("Purr2D2SaberContact");
        _sliceSound = Resolve("Purr2D2ForceBreak");
    }

    /// <param name="emitterBody">Hilt/emitter position relative to the kitten's body origin, in body coordinates.</param>
    /// <param name="paused">Pass true for pause and unsupported time warp. No new channels are started.</param>
    /// <param name="swingSpeed">Tip speed relative to the kitten, in m/s; never orbital speed.</param>
    /// <param name="contact">Contact intensity from 0 to 1; zero stops the optional sizzle loop.</param>
    /// <param name="type">Selects ignition and the additional ambient loop independently of blade color.</param>
    public void Update(KittenEva owner, double3 emitterBody, float deployment, bool targetOn,
        bool paused, float volume, float swingSpeed = 0, float contact = 0, SaberType type = SaberType.Light)
    {
        if (_shutdown || _disposed) return;
        if (owner.IsDisposed || !Finite(emitterBody)) { Stop(); return; }
        if (!ReferenceEquals(_owner, owner)) { Reset(); _owner = owner; }
        Initialize();
        deployment = Clamp(deployment, 0, 1);
        volume = Clamp(volume, 0, 1);
        contact = Clamp(contact, 0, 1);
        swingSpeed = Clamp(swingSpeed, 0, 30);

        if (type != _type)
        {
            Cut(ref _static);
            _type = type;
            _staticStarts = 0;
            _nextStaticStart = 0;
        }

        if (targetOn != _lastTargetOn)
        {
            _lastTargetOn = targetOn;
            _transitionPending = true;
            _humStarts = _staticStarts = _contactStarts = 0;
            _nextHumStart = _nextStaticStart = _nextContactStart = 0;
        }

        if (!Ready && _clash.Channel is null && _swoosh.Channel is null && _slice.Channel is null) return;
        try
        {
            Camera camera = GameAudio.GetAudioCamera();
            FollowOneShot(ref _clash, owner, camera, volume * ClashGain, paused);
            FollowOneShot(ref _swoosh, owner, camera, volume * SwooshGain, paused);
            FollowOneShot(ref _slice, owner, camera, volume * SliceGain, paused);
            if (!Ready) return;
            double3 position = camera.GetPositionEgo(owner) + emitterBody.Transform(owner.Body2Cce);
            double3 velocity = camera.GetVelocityEgo(owner);
            // Fictional suit/saber audio remains audible in vacuum. Coordinates still spatialize normally.
            var spatial = new SpatialAudio(position, velocity, 1.0);

            if (_transitionPending && !paused)
            {
                Cut(ref _transition);
                _transitionGain = TransitionGain * (targetOn && _type == SaberType.Light ? LightIgniteGain : 1f);
                if (volume > 0)
                {
                    SoundBehavior? ignite = _type == SaberType.Dark ? _darkIgniteSound : _lightIgniteSound;
                    _transition = Play(targetOn ? ignite : _retractSound, spatial, volume * _transitionGain);
                }
                _transitionPending = false;
            }

            float swing = Math.Clamp(swingSpeed / 6f, 0, 1);
            float ambientTypeGain = _type == SaberType.Dark ? DarkAmbientGain : 1f;
            float humGain = volume * deployment * 0.42f * ambientTypeGain * (1 + swing * 0.18f);
            float staticGain = volume * deployment * 0.3f * ambientTypeGain;
            if (deployment <= AudibleDeployment || volume <= 0)
            {
                Cut(ref _hum);
                Cut(ref _static);
                Cut(ref _contact);
                _humStarts = _staticStarts = _contactStarts = 0;
                _nextHumStart = _nextStaticStart = _nextContactStart = 0;
            }
            else if (!paused)
            {
                EnsureLoop(ref _hum, _humSound, spatial, humGain, ref _humStarts, ref _nextHumStart);
                EnsureLoop(ref _static, _type == SaberType.Dark ? _darkStaticSound : _lightStaticSound,
                    spatial, staticGain, ref _staticStarts, ref _nextStaticStart);
                if (contact > 0.01f)
                    EnsureLoop(ref _contact, _contactSound, spatial, volume * deployment * contact * 0.35f,
                        ref _contactStarts, ref _nextContactStart);
                else
                {
                    Cut(ref _contact);
                    _contactStarts = 0;
                    _nextContactStart = 0;
                }
            }

            Follow(ref _transition, spatial, volume * _transitionGain, 1, paused);
            Follow(ref _hum, spatial, humGain, 1 + swing * 0.15f, paused);
            Follow(ref _static, spatial, staticGain, 1, paused);
            Follow(ref _contact, spatial, volume * deployment * contact * 0.35f, 1, paused);
        }
        catch (Exception ex)
        {
            WarnPlayback(ex.Message);
            // A stale camera, channel or scene must never interrupt the game's frame.
            Cut(ref _transition);
            Cut(ref _hum);
            Cut(ref _static);
            Cut(ref _contact);
            Cut(ref _clash.Channel);
            Cut(ref _swoosh.Channel);
            Cut(ref _slice.Channel);
        }
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

    private IChannel? Play(SoundBehavior? sound, SpatialAudio spatial, float volume)
    {
        if (sound is null) return null;
        // Configure while paused so no sound spends one frame at the listener origin.
        GameAudio.PlaySound(sound, spatial, out IChannel? channel, volume, startPaused: true);
        if (channel is ChannelWrapper wrapper)
        {
            wrapper.Channel.TrySet3dDopplerLevel(0);
            wrapper.Channel.TrySet3dSpread(0);
        }
        channel?.SetPaused(false);
        return channel;
    }

    private void EnsureLoop(ref IChannel? channel, SoundBehavior? sound, SpatialAudio spatial,
        float volume, ref int starts, ref long nextStart)
    {
        if (Alive(channel)) return;
        Cut(ref channel);
        long now = Environment.TickCount64;
        if (starts >= MaxLoopStartsPerTransition || now < nextStart || sound is null) return;
        starts++;
        nextStart = now + 250L * starts;
        channel = Play(sound, spatial, volume);
        if (channel is null && starts == MaxLoopStartsPerTransition)
            WarnPlayback("Saber audio could not acquire a loop channel; toggle the blade to retry.");
    }

    /// <summary>Play one clash hit at the captured body-local contact point, independent of ignition transitions.</summary>
    public void PlayClash(KittenEva owner, double3 pointBody, float volume)
    {
        Initialize();
        PlayOneShot(ref _clash, _darkIgniteSound, owner, pointBody, volume, ClashGain);
    }

    /// <summary>Use Light ignition as the somersault swoosh for either saber type.</summary>
    public void PlaySwoosh(KittenEva owner, double3 pointBody, float volume)
    {
        Initialize();
        PlayOneShot(ref _swoosh, _lightIgniteSound, owner, pointBody, volume, SwooshGain);
    }

    /// <summary>Use the Force breakup bang for an accepted manual contact slice.</summary>
    public void PlaySlice(KittenEva owner, double3 pointBody, float volume)
    {
        Initialize();
        PlayOneShot(ref _slice, _sliceSound, owner, pointBody, volume, SliceGain);
    }

    private void PlayOneShot(ref OneShot shot, SoundBehavior? sound, KittenEva owner,
        double3 pointBody, float volume, float gain)
    {
        if (_shutdown || _disposed || owner.IsDisposed || !Finite(pointBody)) return;
        volume = Clamp(volume, 0, 1);
        if (volume <= 0) return;
        if (!ReferenceEquals(_owner, owner)) { Reset(); _owner = owner; }
        // Runtime gesture/command gates debounce events. Each accepted event
        // replaces its own channel without cutting ignition or another action.
        Cut(ref shot.Channel);
        try
        {
            Camera camera = GameAudio.GetAudioCamera();
            double3 position = camera.GetPositionEgo(owner) + pointBody.Transform(owner.Body2Cce);
            var spatial = new SpatialAudio(position, camera.GetVelocityEgo(owner), 1.0);
            shot.PointBody = pointBody;
            shot.Channel = Play(sound, spatial, volume * gain);
        }
        catch (Exception ex)
        {
            Cut(ref shot.Channel);
            WarnPlayback(ex.Message);
        }
    }

    private static void FollowOneShot(ref OneShot shot, KittenEva owner, Camera camera, float volume, bool paused)
    {
        if (!Alive(shot.Channel)) { Cut(ref shot.Channel); return; }
        double3 position = camera.GetPositionEgo(owner) + shot.PointBody.Transform(owner.Body2Cce);
        var spatial = new SpatialAudio(position, camera.GetVelocityEgo(owner), 1.0);
        Follow(ref shot.Channel, spatial, volume, 1, paused);
    }

    private static void Follow(ref IChannel? channel, SpatialAudio spatial, float volume, float pitch, bool paused)
    {
        if (!Alive(channel)) { Cut(ref channel); return; }
        channel!.SetSpatialAudio(spatial);
        channel.VolumeMultiplier = volume;
        channel.PitchMultiplier = pitch;
        channel.UpdateVolume();
        channel.UpdatePitch();
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

    /// <summary>
    /// Pause a temporarily hidden/stale emitter without forgetting its ignition state.
    /// A subsequent Update resumes and relocates its channels without replaying activation.
    /// </summary>
    public void Suspend()
    {
        if (_shutdown || _disposed) return;
        PauseOwned(ref _transition);
        PauseOwned(ref _hum);
        PauseOwned(ref _static);
        PauseOwned(ref _contact);
        PauseOwned(ref _clash.Channel);
        PauseOwned(ref _swoosh.Channel);
        PauseOwned(ref _slice.Channel);
    }

    private static void PauseOwned(ref IChannel? channel)
    {
        try { channel?.SetPaused(true); }
        catch { channel = null; /* A stolen/invalid handle can be reacquired by Update. */ }
    }

    /// <summary>Immediately end all channels without playing a retraction sound.</summary>
    public void Stop()
    {
        if (!_shutdown)
        {
            Cut(ref _transition);
            Cut(ref _hum);
            Cut(ref _static);
            Cut(ref _contact);
            Cut(ref _clash.Channel);
            Cut(ref _swoosh.Channel);
            Cut(ref _slice.Channel);
        }
        else _transition = _hum = _static = _contact = null;
        _clash = _swoosh = _slice = default;
        _owner = null;
        _lastTargetOn = _transitionPending = false;
        _humStarts = _staticStarts = _contactStarts = 0;
        _nextHumStart = _nextStaticStart = _nextContactStart = 0;
    }

    /// <summary>Forget the previous EVA/world on save load, boarding, scene exit or equip change.</summary>
    public void Reset() => Stop();

    /// <summary>Call before GameAudio.Release (for example from a Program.Dispose prefix).</summary>
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
        Console.WriteLine($"purr2d2: saber audio: {message}");
    }

    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
