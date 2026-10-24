using Purr2D2.Core;

namespace KSA
{
    internal static class Program
    {
        internal static bool EditorFlag = false;
        internal static KittenEva? ControlledVehicle;
        internal static double PlayerTime;
        internal static double GetPlayerTime() => PlayerTime;
    }
    internal enum LocomotionMode { Grounded, Airborne, Mmu, Ladder, Swimming, Tumbling }
    internal enum Situation { Freefall, Rolling, Landed }
    internal static class SituationEx
    {
        internal static bool HasTerrainContact(this Situation value) => value != Situation.Freefall;
    }
    internal sealed class LocomotionState { internal LocomotionMode Mode = LocomotionMode.Grounded; }
    internal sealed class KittenEva
    {
        internal bool IsDisposed = false;
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
        internal double InputTime => KSA.Program.PlayerTime;
        internal int Launches;
        internal SomersaultDirection Direction;
        internal float RotationRadians => 0;
        internal float Tuck => 0;
        internal bool CanStart(KSA.KittenEva? owner, out string reason, double? pressTime = null)
        { reason = "Unavailable."; return Available && (pressTime ?? KSA.Program.GetPlayerTime()) <= AllowedUntil
            && owner is { IsDisposed: false } && (Active ? FollowupAvailable
            : owner.LocomotionState.Mode == KSA.LocomotionMode.Grounded && KSA.SituationEx.HasTerrainContact(owner.Situation)); }
        internal bool Request(KSA.KittenEva owner, SomersaultDirection direction, double? pressTime = null)
        { if (!CanStart(owner, out _, pressTime)) return false; LastPressTime = pressTime ?? KSA.Program.GetPlayerTime();
            Direction = direction; Active = true; Launches++; return true; }
        internal void Update(KSA.KittenEva? owner) { }
        internal void Reset() => Active = false;
    }
}
