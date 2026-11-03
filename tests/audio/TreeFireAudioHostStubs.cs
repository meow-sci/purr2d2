using Brutal.Numerics;

namespace Brutal.FmodApi
{
    public sealed class FireTestChannel
    {
        public float Doppler = -1, Spread = -1;
        public void TrySet3dDopplerLevel(float value) => Doppler = value;
        public void TrySet3dSpread(float value) => Spread = value;
    }
}
namespace Brutal.VulkanApi { public sealed class CommandBuffer; }
namespace Brutal.VulkanApi.Abstractions { internal static class FireRenderingBoundary; }

namespace KSA
{
    public static class Double3Ex
    {
        public static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
        public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
        public static double3 NormalizeOrZero(this double3 value) => double3.NormalizeOrZero(value);
        public static doubleQuat Inverse(this doubleQuat value) => doubleQuat.Inverse(value);
    }
    public static class Universe { public static object CurrentSystem = new(); public static double SimulationSpeed = 1; }
    public static class GameSettings
    {
        public static readonly Options Current = new();
        public sealed class Options { public readonly GraphicsOptions Graphics = new(); }
        public sealed class GraphicsOptions { public bool Particles = true; }
    }
    public sealed class Part
    {
        public PartTree? Tree;
        public double4x4 Local = double4x4.Identity;
        public double4x4 MatrixAsmb2Ego(in double4x4 vehicleMatrix) => Local * vehicleMatrix;
    }
    public sealed class PartTree { public required Vehicle OwningVehicle; }
    public sealed class Vehicle
    {
        public bool IsDisposed;
        public object System = Universe.CurrentSystem;
        public double4x4 World = double4x4.Identity;
        public double3 Velocity;
        public double4x4 GetMatrixAsmb2Ego(Camera camera)
        { var result = World; result.Translation -= camera.Position; return result; }
        public double3 GetPositionCci() => double3.UnitY;
        public doubleQuat GetBody2Cci() => doubleQuat.Identity;
        public doubleQuat Body2Ego => doubleQuat.Identity;
    }
    public sealed class Camera
    {
        public double3 Position;
        public bool Outside;
        public double3 GetPositionEgo(Vehicle vehicle) => vehicle.World.Translation - Position;
        public double3 GetVelocityEgo(Vehicle vehicle) => vehicle.Velocity;
        public bool IsSphereOutsideFrustum(double3 point, double radius) => Outside;
    }
    public readonly record struct SpatialAudio(double3 Position, double3 Velocity, double Pressure);
    public sealed record SoundBehavior(string Id);
    public static class ModLibrary
    {
        public static int Gets;
        public static bool Missing;
        public static T Get<T>(string id) where T : class
        { Gets++; return Missing ? throw new KeyNotFoundException(id) : (new SoundBehavior(id) as T)!; }
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
        void Stop(bool stopImmediate = false);
    }
    public sealed class ChannelWrapper(string id) : IChannel
    {
        public readonly string Id = id;
        public readonly Brutal.FmodApi.FireTestChannel Channel = new();
        public bool Stopped, Paused, Finished, Virtual, ThrowOnRead, ThrowOnFollow, ThrowOnStop, Immediate;
        public int Stops;
        public SpatialAudio Spatial;
        public float VolumeMultiplier { get; set; }
        public float PitchMultiplier { get; set; }
        public bool IsPlaying() => ThrowOnRead ? throw new InvalidOperationException("stolen channel") : !Stopped && !Finished;
        public bool IsVirtual() => Virtual && !Stopped;
        public void SetPaused(bool paused) => Paused = paused;
        public void SetSpatialAudio(SpatialAudio spatial)
        { if (ThrowOnFollow) throw new InvalidOperationException("stale spatial channel"); Spatial = spatial; }
        public void UpdateVolume() { }
        public void UpdatePitch() { }
        public void Stop(bool stopImmediate = false)
        { Stopped = true; Immediate = stopImmediate; Stops++; if (ThrowOnStop) throw new InvalidOperationException("mixer closed"); }
    }
    public static class GameAudio
    {
        public sealed class TestSystem { public bool Null; public bool IsNull() => Null; }
        public static readonly TestSystem System = new();
        public static readonly Camera Camera = new();
        public static readonly List<ChannelWrapper> Channels = new();
        public static int Plays, CameraReads;
        public static bool ReturnNull, ThrowCamera, ThrowPlay, NextChannelInvalid, LastStartPaused, LastAlwaysPlay;
        public static Camera GetAudioCamera()
        { CameraReads++; return ThrowCamera ? throw new InvalidOperationException("camera unavailable") : Camera; }
        public static void PlaySound(SoundBehavior sound, SpatialAudio spatial, out IChannel? channel,
            float volume = 1, bool startPaused = false, bool alwaysPlay = false)
        {
            Plays++; LastStartPaused = startPaused; LastAlwaysPlay = alwaysPlay;
            if (ThrowPlay) throw new InvalidOperationException("mixer unavailable");
            if (ReturnNull || Universe.SimulationSpeed > 10 && !alwaysPlay) { channel = null; return; }
            var created = new ChannelWrapper(sound.Id)
            { Spatial = spatial, VolumeMultiplier = volume, Paused = startPaused, ThrowOnRead = NextChannelInvalid };
            Channels.Add(created); channel = created;
        }
    }
}

namespace Purr2D2.Rendering
{
    // Only the surrounding renderer/native drawing boundary is replaced. Source
    // admission, capacity, pruning, Clear and late-audio hooks are production code.
    public sealed partial class SaberRenderer : IDisposable
    {
        private static SaberRenderer? _instance;
        private bool _disposed;
        private double _now;
        internal int RibbonDraws;
        public SaberRenderer() => _instance = this;
        internal void Tick(double now) { UpdateTreeFire(now); _now = now; }
        internal void AudioTick() => BeforeTreeFireAudioUpdate();
        internal void ReleaseAudio() => BeforeTreeFireAudioRelease();
        internal void SetVolume(float value) => _treeFireVolume = value;
        internal int Sources => _treeFire.Count;
        internal void Draw() => DrawTreeFire(new Brutal.VulkanApi.CommandBuffer(), float4x4.Identity, KSA.GameAudio.Camera);
        public void Dispose() { DisposeTreeFireAudio(); _disposed = true; if (ReferenceEquals(_instance, this)) _instance = null; }
        private static bool Finite(double3 point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
        private void DrawRibbon(Brutal.VulkanApi.CommandBuffer commands, float4x4 projection, double3 tail, double3 tip,
            double3 normal, float radius, float core, float4 color, bool isChar, bool billboard) => RibbonDraws++;
    }
}
