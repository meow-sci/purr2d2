using Purr2D2.Core;

namespace Brutal.GlfwApi
{
    internal enum GlfwKey { Space, Up, Down, Left, Right }
    internal enum GlfwKeyAction { Release, Press, Repeat }
    internal enum GlfwModifier { None }
    internal sealed class GlfwWindow
    {
        private readonly Dictionary<GlfwKey, GlfwKeyAction> _keys = new();
        internal GlfwKeyAction GetKey(GlfwKey key) => _keys.GetValueOrDefault(key, GlfwKeyAction.Release);
        internal void SetKey(GlfwKey key, GlfwKeyAction action) => _keys[key] = action;
    }
}

namespace RenderCore.Input
{
    using Brutal.GlfwApi;
    internal readonly struct GlfwKeyEvent(GlfwWindow window, GlfwKeyAction action, GlfwKey key)
    {
        internal GlfwWindow Window { get; } = window;
        internal GlfwKeyAction Action { get; } = action;
        internal GlfwKey Key { get; } = key;
        internal void Deconstruct(out GlfwWindow window, out GlfwKeyAction action, out GlfwKey key, out GlfwModifier modifiers)
        { window = Window; action = Action; key = Key; modifiers = GlfwModifier.None; }
    }
}

namespace KSA
{
    internal static class Program
    {
        internal static bool EditorFlag = false;
        internal static KittenEva? ControlledVehicle;
        internal static double PlayerTime;
        internal static Brutal.GlfwApi.GlfwWindow MainWindow = new();
        internal static Brutal.GlfwApi.GlfwWindow GetWindow() => MainWindow;
        internal static double GetPlayerTime() => PlayerTime;
    }
    internal static class Universe
    {
        internal static object? CurrentSystem = new();
        internal static double Speed = 1;
        internal static double GetSimulationSpeed() => Speed;
    }
    internal enum LocomotionMode { Grounded, Airborne, Mmu, Ladder, Swimming, Tumbling }
    internal enum Situation { Freefall, Rolling, Landed }
    internal enum FlightComputerRCSMode { Enabled, Disabled }
    internal sealed class FlightComputer { internal FlightComputerRCSMode RCSMode = FlightComputerRCSMode.Disabled; }
    internal static class SituationEx
    {
        internal static bool HasTerrainContact(this Situation value) => value != Situation.Freefall;
    }
    internal sealed class LocomotionState { internal LocomotionMode Mode = LocomotionMode.Grounded; }
    internal sealed class KittenEva
    {
        internal bool IsDisposed = false;
        internal bool IsEditedVehicle = false, IsControllable = true;
        internal object? System = Universe.CurrentSystem;
        internal readonly FlightComputer FlightComputer = new();
        internal readonly LocomotionState LocomotionState = new();
        internal Situation Situation = Situation.Landed;
    }
}

namespace Purr2D2.Pose
{
    internal sealed class SaberPoseController { internal bool Swinging; }
    internal sealed class SomersaultPoseController
    {
        internal KSA.KittenEva? Owner;
        internal bool Eligible;
        internal string Status => "Unsupported rig.";
        internal bool Bind(KSA.KittenEva owner) { Owner = owner; return Eligible = true; }
        internal void Unbind() { Owner = null; Eligible = false; }
        internal void SetMotion(SomersaultDirection direction, float angle, float tuck, bool active) { }
    }
}

namespace Purr2D2.Runtime
{
    internal sealed class SomersaultMotionController
    {
        internal bool Active, Available = true;
        internal bool FollowupAvailable;
        internal double AllowedUntil = double.PositiveInfinity, LastPressTime;
        internal double ComboIntervalSeconds = .75;
        internal double InputTime => KSA.Program.PlayerTime;
        internal int Launches;
        internal SomersaultDirection Direction;
        internal KSA.KittenEva? LastOwner;
        internal float RotationRadians => 0;
        internal float Tuck => 0;
        internal void SetComboIntervalSeconds(double seconds) => ComboIntervalSeconds = double.IsFinite(seconds)
            ? Math.Clamp(seconds, .1, 1.5) : .75;
        internal bool CanStart(KSA.KittenEva? owner, out string reason, double? pressTime = null)
        { reason = "Unavailable."; return Available && (pressTime ?? KSA.Program.GetPlayerTime()) <= AllowedUntil
            && owner is { IsDisposed: false } && (Active ? FollowupAvailable
            : owner.LocomotionState.Mode == KSA.LocomotionMode.Grounded && KSA.SituationEx.HasTerrainContact(owner.Situation)); }
        internal bool Request(KSA.KittenEva owner, SomersaultDirection direction, double? pressTime = null)
        { if (!CanStart(owner, out _, pressTime)) return false; LastPressTime = pressTime ?? KSA.Program.GetPlayerTime();
            Direction = direction; LastOwner = owner; Active = true; Launches++; return true; }
        internal void Update(KSA.KittenEva? owner) { }
        internal void Reset() => Active = false;
    }
}
