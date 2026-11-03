using Brutal.FmodApi;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Audio;

/// <summary>One spatial loop per burning part, retained solely by the renderer's live fire anchors.</summary>
internal sealed class TreeFireAudio(int maximumSources) : IDisposable
{
    private const string SoundId = "Purr2D2TreeFireLoop";
    private const int MaximumAttempts = 4;
    private readonly Dictionary<Part, Voice> _voices = new(ReferenceEqualityComparer.Instance);
    private readonly List<Part> _removed = new(maximumSources);
    private SoundBehavior? _sound;
    private int _resolveAttempts;
    private int _retainedParts;
    private double _nextResolve;
    private bool _disposed, _warned;

    private sealed class Voice
    {
        internal IChannel? Channel;
        internal double3 PointSum, Point;
        internal int Anchors, Attempts;
        internal double NextAttempt;
    }

    // These calls synchronize ownership without another duration or expiry clock.
    // Removing the last visual anchor immediately releases its one voice.
    internal void BeginSources()
    {
        _retainedParts = 0;
        foreach (var voice in _voices.Values) { voice.Anchors = 0; voice.PointSum = double3.Zero; }
    }

    internal void KeepSource(Part part, double3 point)
    {
        if (_disposed || !Finite(point)) return;
        if (!_voices.TryGetValue(part, out var voice))
        {
            if (_retainedParts >= maximumSources) return;
            _voices.Add(part, voice = new());
        }
        if (voice.Anchors == 0)
        {
            if (_retainedParts >= maximumSources) return;
            _retainedParts++;
        }
        voice.PointSum += point;
        voice.Anchors++;
    }

    internal void EndSources()
    {
        _removed.Clear();
        foreach (var (part, voice) in _voices)
        {
            if (voice.Anchors != 0) voice.Point = voice.PointSum / voice.Anchors;
            else { Cut(ref voice.Channel); _removed.Add(part); }
        }
        foreach (var part in _removed) _voices.Remove(part);
        _removed.Clear();
    }

    /// <summary>Run before native audio updates, after this frame's audio camera has moved.</summary>
    internal void Update(double now, float volume)
    {
        if (_disposed || _voices.Count == 0) return;
        if (!double.IsFinite(now)) { Clear(); return; }
        volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0;
        try
        {
            if (GameAudio.System.IsNull()) { StopChannels(); return; }
            if (volume > 0) Resolve(now);
            Camera? camera = null;
            _removed.Clear();
            foreach (var (part, voice) in _voices)
            {
                if (part.Tree?.OwningVehicle is not { IsDisposed: false } vehicle
                    || !ReferenceEquals(vehicle.System, Universe.CurrentSystem))
                { Cut(ref voice.Channel); _removed.Add(part); continue; }
                if (volume <= 0)
                {
                    Cut(ref voice.Channel);
                    voice.Attempts = 0; voice.NextAttempt = now;
                    continue;
                }
                if (_sound == null) continue;
                bool starting = false;
                try
                {
                    if (!Alive(voice.Channel))
                    {
                        Cut(ref voice.Channel);
                        if (voice.Attempts >= MaximumAttempts || now < voice.NextAttempt) continue;
                        voice.Attempts++;
                        voice.NextAttempt = now + .25 * voice.Attempts;
                        starting = true;
                    }
                    camera ??= GameAudio.GetAudioCamera();
                    var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
                    var matrix = part.MatrixAsmb2Ego(in vehicleMatrix);
                    var position = voice.Point.Transform(matrix);
                    var velocity = camera.GetVelocityEgo(vehicle);
                    if (!Finite(position) || !Finite(velocity)) throw new InvalidOperationException("Tree fire audio position is unavailable.");
                    var spatial = new SpatialAudio(position, velocity, 1.0);
                    if (voice.Channel == null)
                    {
                        // Native XML randomizes the loop offset. Configure its
                        // moving emitter before it can play at the listener origin.
                        GameAudio.PlaySound(_sound, spatial, out var channel, volume, startPaused: true, alwaysPlay: true);
                        voice.Channel = channel;
                        if (channel is ChannelWrapper wrapper)
                        {
                            wrapper.Channel.TrySet3dDopplerLevel(0);
                            wrapper.Channel.TrySet3dSpread(0);
                        }
                    }
                    if (voice.Channel is { } playing)
                    {
                        playing.SetSpatialAudio(spatial);
                        playing.VolumeMultiplier = volume;
                        playing.PitchMultiplier = 1;
                        playing.UpdateVolume(); playing.UpdatePitch();
                        // Fire already advances in player time through pause/warp.
                        playing.SetPaused(false);
                        if (starting && Alive(playing))
                        {
                            voice.Attempts = 0;
                            voice.NextAttempt = now + .25;
                        }
                    }
                }
                catch (Exception error)
                {
                    Cut(ref voice.Channel);
                    if (!starting) { voice.Attempts++; voice.NextAttempt = now + .25 * voice.Attempts; }
                    Warn(error.Message);
                }
            }
            foreach (var part in _removed) _voices.Remove(part);
            _removed.Clear();
        }
        catch (Exception error) { StopChannels(); Warn(error.Message); }
    }

    private void Resolve(double now)
    {
        if (_sound != null || _resolveAttempts >= MaximumAttempts || now < _nextResolve) return;
        _resolveAttempts++;
        _nextResolve = now + .5 * _resolveAttempts;
        try
        {
            // Keep registry lookup within this bounded, guarded retry path.
            _sound = ModLibrary.Get<SoundBehavior>(SoundId);
        }
        catch (Exception error) { Warn("Could not resolve the tree fire loop: " + error.Message); }
    }

    private static bool Alive(IChannel? channel) => channel != null && (channel.IsPlaying() || channel.IsVirtual());
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static void Cut(ref IChannel? channel)
    {
        var old = channel; channel = null;
        try { old?.Stop(stopImmediate: true); }
        catch { /* A stolen handle or a closing mixer has already released playback. */ }
    }

    private void StopChannels()
    { foreach (var voice in _voices.Values) Cut(ref voice.Channel); }

    internal void Clear()
    {
        StopChannels(); _voices.Clear(); _removed.Clear();
        _retainedParts = 0;
        _sound = null; _resolveAttempts = 0; _nextResolve = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
    }

    private void Warn(string message)
    {
        if (_warned) return;
        _warned = true;
        Console.WriteLine("[purr2d2] Tree fire audio: " + message);
    }
}
