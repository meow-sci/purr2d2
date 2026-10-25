using Brutal.Numerics;
using RenderCore;
using RenderCore.Animation;

// Only host/render/audio boundaries are stubbed. The harness compiles the actual
// per-kitten runtime partial and actual pose controller, including Bind/Unbind,
// MMU ownership and main-view emitter publication.
namespace KSA
{
    public static class Program
    {
        public static bool EditorFlag;
        public static object? ControlledVehicle;
        public static ulong FrameNumber;
        private static IViewport _mainViewport = new Viewport(ViewportType.Main);
        public static IViewport MainViewport
        {
            get => _mainViewport;
            set
            {
                _mainViewport = value;
                if (value is IGameViewport gameViewport) ViewportRegistry.SetGameViews(gameViewport);
            }
        }
        public static IViewport RenderedViewport = MainViewport;
    }

    public static class Universe
    {
        public static CelestialSystem? CurrentSystem;
        public static KittenRosterData KittenRoster = new();
        public static double Speed = 1;
        public static int SpeedReads;
        public static Exception? SpeedFailure;
        public static double GetSimulationSpeed()
        {
            SpeedReads++;
            if (SpeedFailure is { } failure) throw failure;
            return Speed;
        }
    }

    public sealed class CelestialSystem { public readonly HostCollection All = new(); }
    public sealed class HostCollection
    {
        public static int SnapshotReads;
        public object[] Items = [];
        public ReadOnlySpan<object> AsSpan() { SnapshotReads++; return Items; }
        public object? Get(KeyHash hash) => Items.OfType<Vehicle>().FirstOrDefault(vehicle => vehicle.Hash == hash);
    }
    public readonly record struct KeyHash(uint Code)
    {
        public static readonly KeyHash Zero = new(0);
        public static KeyHash Make(ReadOnlySpan<char> value)
        {
            uint hash = 2166136261;
            foreach (char c in value) hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
            return value.IsEmpty ? Zero : new(hash);
        }
    }
    public sealed class KittenRosterData { public readonly List<KittenRosterEntryData> Kittens = []; }
    public sealed class KittenRosterEntryData(string name, string character)
    {
        public const string EvaAssignmentId = "EVA";
        public string Name = name, Character = character, AssignedVehicleId = EvaAssignmentId;
        public KeyHash NameHash = KeyHash.Make(name);
        public bool Kia;
    }
    public class Vehicle(string id)
    {
        public string Id = id;
        public KeyHash Hash = KeyHash.Make(id);
        public bool IsDisposed;
        public IVASeat[] Seats = [];
        public ReadOnlySpan<IVASeat> Crew => Seats;
    }
    public sealed class IVASeat { public KeyHash AssignedKittenHash; }
    public sealed class CharacterReference(string id) { public string Id = id; }
    public enum LocomotionMode { Grounded, Airborne, Mmu, Ladder, Tumbling, Rightening, Swimming }
    public sealed class LocomotionState { public LocomotionMode Mode = LocomotionMode.Grounded; }
    public sealed class KittenEva(string id) : Vehicle(id)
    {
        public CharacterReference Character = new("Stock");
        public readonly KittenRenderable Renderable = new();
        public readonly LocomotionState LocomotionState = new();
        public readonly ParentBody Parent = new();
        public double3 PositionEcl;
        public double3 SurfaceVelocityCci;
        public int PositionReads, VelocityReads;
        public float3 CenterOfMassAsmbF => float3.Zero;
        public doubleQuat Body2Cce => doubleQuat.Identity;
        public double3 GetPositionEcl() { PositionReads++; return PositionEcl; }
        public double3 GetSurfaceVelocityCci() { VelocityReads++; return SurfaceVelocityCci; }
    }
    public sealed class ParentBody { public doubleQuat GetCci2Cce() => doubleQuat.Identity; }
    public sealed class KittenRenderable
    {
        private readonly CharacterAvatar _characterAvatar = new();
        public CharacterAvatar Avatar => _characterAvatar;
    }
    public sealed class CharacterAvatar
    {
        public readonly CharacterCore Core = new();
        public readonly CharacterAttachments Attachments = new();
        public sealed class CharacterCore
        {
            public readonly CharacterModel CharacterModel = new();
            public float Scale = .01f;
        }
        public sealed class CharacterAttachments { public readonly MmuAttachment Mmu = new(); }
        public sealed class MmuAttachment { public bool Enabled = true; }
    }
    public sealed class CharacterModel
    {
        public readonly Skeleton Skeleton = new();
        public readonly List<IAnimProcessor> AnimProcessors = [];
    }
    public interface IAnimProcessor
    {
        float Priority { get; }
        bool ShouldUpdate { get; set; }
        bool CanCacheAnimation { get; }
        void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt);
        void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt);
    }
    public enum ViewportType { Main, Secondary, CharacterPortrait }
    public interface IViewport { ViewportType Type { get; } bool Visible { get; } Camera GetCamera(); }
    public interface IGameViewport : IViewport { }
    public static class ViewportRegistry
    {
        public const int MAX_VIEWPORTS = 8;
        private static IGameViewport[] _gameViews = [];
        public static int GameViewReads;
        public static ReadOnlySpan<IGameViewport> GameViews { get { GameViewReads++; return _gameViews; } }
        public static void SetGameViews(params IGameViewport[] views) => _gameViews = views;
    }
    public sealed class Viewport(ViewportType type) : IGameViewport
    {
        public static int CameraReads;
        public ViewportType Type => type;
        public bool Visible { get; set; } = true;
        public Camera Camera { get; } = new();
        public Exception? CameraFailure;
        public Camera GetCamera()
        {
            CameraReads++;
            if (CameraFailure is { } failure) throw failure;
            return Camera;
        }
    }
    public sealed class Camera { public double3 PositionEcl { get; set; } }
    public static class Double3Ex
    {
        public static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
        public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
}

namespace Purr2D2.Capes
{
    // Native model/solver creation is a boundary in this lifecycle harness. The
    // production manager and update policy still run, while this counter fake
    // makes their ownership, visibility and scheduling decisions observable.
    internal sealed class CapeController : IDisposable
    {
        internal static string? HookFailure;
        internal static int SetupAttempts, Constructions, Disposals, DrawCalls, StepCalls, IdleResets, WarpResets;
        internal static int SuspendCalls, SuspendReleases;
        internal static readonly List<string> StepOwners = [];
        internal static Exception? SetupFailure;

        private float _elapsed;
        private float _idle;
        private bool _suspended, _unsupportedSpeed;
        internal readonly KSA.KittenEva Owner;
        internal Purr2D2.Core.SaberType Type;
        internal string? Failure { get; set; }
        internal ulong LastMainDrawFrame = ulong.MaxValue;
        internal bool IsSimulationCandidate;
        internal bool IsStepDue => CapeUpdatePolicy.IsDue(_elapsed);
        internal float LastStepDt;
        internal Brutal.Numerics.float3 LastVelocity;

        internal CapeController(KSA.KittenEva owner, Purr2D2.Core.SaberType type)
        {
            SetupAttempts++;
            if (SetupFailure is { } failure) throw failure;
            Owner = owner; Type = type; Constructions++;
        }
        internal void SetType(Purr2D2.Core.SaberType type) => Type = type;
        internal bool Draw(KSA.KittenRenderable renderable, KSA.IViewport viewport, Brutal.Numerics.float4x4 bodyToCamera, ulong frame)
        {
            DrawCalls++;
            if (ReferenceEquals(viewport, KSA.Program.MainViewport)) LastMainDrawFrame = frame;
            return true;
        }
        internal void MarkActive(bool supportedSpeed)
        {
            _idle = 0;
            if (supportedSpeed) _suspended = _unsupportedSpeed = false;
        }
        internal void Accumulate(double dt) => CapeUpdatePolicy.Accumulate(ref _elapsed, dt, true);
        internal float ConsumeStepTime() => CapeUpdatePolicy.Consume(ref _elapsed);
        internal void ClearAccumulatedTime() => _elapsed = 0;
        internal void Step(float dt, Brutal.Numerics.float3 velocity)
        {
            StepCalls++; StepOwners.Add(Owner.Id); LastStepDt = dt; LastVelocity = velocity;
        }
        internal void SuspendForUnsupportedSpeed()
        {
            if (_unsupportedSpeed) return;
            _unsupportedSpeed = _suspended = true; _elapsed = 0; WarpResets++;
        }
        internal void SuspendSimulation()
        {
            SuspendCalls++;
            if (_suspended) return;
            _suspended = true;
            _elapsed = 0;
            _idle = 0;
            SuspendReleases++;
        }
        internal void AdvanceIdle(double dt)
        {
            _idle += (float)Math.Max(0, dt);
            if (_idle < CapeUpdatePolicy.ReleaseAfterIdle) return;
            _idle = 0; _elapsed = 0; IdleResets++;
        }
        public void Dispose() { Disposals++; }
        internal static void ResetMetrics()
        {
            HookFailure = null; SetupAttempts = Constructions = Disposals = DrawCalls = StepCalls = IdleResets = WarpResets = 0;
            SuspendCalls = SuspendReleases = 0;
            StepOwners.Clear(); SetupFailure = null;
        }
    }
}

namespace RenderCore
{
    public struct TransformNodeInfo { public int ParentIndex; }
    // Local IK is covered by PoseChecks against real Numerics. These boundaries
    // deliberately reject local-pose evaluation in this lifecycle-only harness.
    public struct TransformTRS
    {
        public floatQuat Rotation;
        public float4x4 CreateMatrix() => throw new NotSupportedException();
        public static TransformTRS FromMatrix(float4x4 value) => throw new NotSupportedException();
        public static TransformTRS Lerp(TransformTRS a, TransformTRS b, float t) => throw new NotSupportedException();
    }
}

namespace RenderCore.Animation
{
    public sealed class Skeleton
    {
        public readonly List<string> BoneNames = ["Chest", "Shoulder_R", "Elbow_R", "Wrist_R"];
        private readonly TransformNodeInfo[] _nodes = [new() { ParentIndex = -1 }, new(), new() { ParentIndex = 1 }, new() { ParentIndex = 2 }];
        private readonly float4x4[] _world = [float4x4.Identity, float4x4.Identity, float4x4.Identity, float4x4.Identity];
        public int BoneCount => BoneNames.Count;
        public Span<TransformNodeInfo> NodeInfo => _nodes;
        public Span<float4x4> WorldTransforms => _world;
    }
}

namespace Purr2D2.Audio
{
    public sealed class SaberAudio : IDisposable
    {
        public int Shutdowns, Disposals, Stops, Suspends;
        public int Updates;
        public bool LastTargetOn;
        public float LastDeployment;
        public Purr2D2.Core.SaberType LastType;
        public void Shutdown() { Shutdowns++; Stop(); }
        public void Dispose() => Disposals++;
        public void Stop() => Stops++;
        public void Reset() => Stop();
        public void Suspend() => Suspends++;
        public void Update(KSA.KittenEva owner, double3 position, float deployment, bool targetOn,
            bool paused, float volume, float swingSpeed = 0, float contact = 0,
            Purr2D2.Core.SaberType type = Purr2D2.Core.SaberType.Light)
        {
            Updates++; LastTargetOn = targetOn; LastDeployment = deployment; LastType = type;
        }
    }
}
