using Brutal.Numerics;
using Purr2D2.Core;

namespace KSA
{
    internal interface IParentBody { }
    internal sealed class ParentBody : IParentBody { }

    internal sealed class KittenEva(string id)
    {
        internal string Id { get; } = id;
        internal bool IsDisposed;
        internal IParentBody Parent = new ParentBody();
        internal doubleQuat Body2Cce = doubleQuat.Identity;
        internal double3 PositionCce, PositionEcl;
        internal double3 GetPositionCce() => PositionCce;
        internal double3 GetPositionEcl() => PositionEcl;
    }

    internal static class BodyTransformExtensions
    {
        internal static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
}

namespace Purr2D2.Runtime
{
    internal sealed partial class SaberRuntime
    {
        private readonly Dictionary<KSA.KittenEva, KittenSaber> _kittenSabers = new();
        private readonly TestRenderer _renderer = new();
        private readonly TestSettings _settings = new();
        private double _playerTime;

        private sealed class KittenSaber(float bladeLength, bool equipped, float deployment,
            TestPose pose, TestAudio audio)
        {
            internal readonly TestDrop Drop = new();
            internal readonly TestBlade Blade = new(equipped, deployment);
            internal readonly float BladeLength = bladeLength;
            internal readonly TestPose Pose = pose;
            internal readonly TestAudio Audio = audio;
        }

        private sealed class TestDrop { internal bool Held = true; }

        internal void SetDroppedForTest(KSA.KittenEva owner, bool dropped) => _kittenSabers[owner].Drop.Held = !dropped;

        private sealed class TestBlade(bool equipped, float deployment)
        {
            internal bool Equipped = equipped;
            internal float Deployment = deployment;
        }

        private sealed class TestSettings { internal float Volume = 1; }

        internal sealed class TestPose
        {
            internal bool Eligible = true, HasFreshClashSnapshot = true, ClashHeld, AngryActive;
            internal double3 SegmentStart;
            internal double3 SegmentDirection = double3.UnitY;
            internal int SegmentSamples, BeginCount;
            internal float LastSampledLength, HeldLength;
            internal double LastUpdateTime, HoldUntil, AngryUntil, HoldStartedAt;

            internal void UpdateClash(double now)
            {
                LastUpdateTime = now;
                ClashHeld &= now < HoldUntil;
                AngryActive &= now < AngryUntil;
            }

            internal void ClearClash()
            {
                ClashHeld = AngryActive = false;
                HeldLength = 0;
            }

            internal bool TryGetBladeSegment(double length, out double3 baseBody, out double3 tipBody)
            {
                SegmentSamples++;
                LastSampledLength = (float)length;
                baseBody = SegmentStart;
                tipBody = SegmentStart + SegmentDirection * length;
                return Eligible && HasFreshClashSnapshot && double.IsFinite(length) && length > 0;
            }

            internal bool CanBeginClash(double now) => Eligible && HasFreshClashSnapshot
                && !ClashHeld && double.IsFinite(now);

            internal bool TryBeginClash(double now, float visibleBladeLength)
            {
                if (!CanBeginClash(now) || !float.IsFinite(visibleBladeLength) || visibleBladeLength <= 0) return false;
                BeginCount++;
                HoldStartedAt = now;
                HoldUntil = now + .4;
                AngryUntil = now + .5;
                HeldLength = visibleBladeLength;
                ClashHeld = AngryActive = true;
                return true;
            }
        }

        internal sealed class TestAudio
        {
            internal int ClashSounds;
            internal double3 LastPoint;
            internal void PlayClash(KSA.KittenEva owner, double3 pointBody, float volume)
            { ClashSounds++; LastPoint = pointBody; }
        }

        private sealed class TestRenderer
        {
            internal int ClashBursts;
            internal double3 LastPoint;
            internal double LastBorn;
            internal void AddClashSparks(KSA.KittenEva owner, double3 pointBody, double now)
            { ClashBursts++; LastPoint = pointBody; LastBorn = now; }
        }

        internal TestPose AddTestSaber(KSA.KittenEva owner, float configuredLength = 1,
            float deployment = 1, double3? segmentStart = null, double3? segmentDirection = null,
            bool equipped = true, bool eligible = true, bool fresh = true)
        {
            var pose = new TestPose
            {
                SegmentStart = segmentStart ?? double3.Zero,
                SegmentDirection = segmentDirection ?? double3.UnitY,
                Eligible = eligible,
                HasFreshClashSnapshot = fresh,
            };
            _kittenSabers.Add(owner, new KittenSaber(configuredLength, equipped, deployment, pose, new TestAudio()));
            return pose;
        }

        internal void TickClashesForTest(double supportedDt, double playerTime)
        { _playerTime = playerTime; UpdateSaberClashes(supportedDt); }

        private float VisibleBladeLength(KittenSaber saber) => saber.Pose.ClashHeld
            ? saber.Pose.HeldLength : saber.Blade.Deployment * saber.BladeLength;

        internal int PairCountForTest => _clashPairs.Count;
        internal int CandidateCountForTest => _clashCandidateCount;
        internal double ClashTimeForTest => _clashTime;
        internal int SparkCountForTest => _renderer.ClashBursts;
        internal double LastSparkBornForTest => _renderer.LastBorn;
        internal double3 LastSparkPointForTest => _renderer.LastPoint;
        internal bool HasCandidateForTest(KSA.KittenEva owner)
        {
            for (int i = 0; i < _clashCandidateCount; i++)
                if (ReferenceEquals(_clashCandidates[i].Owner, owner)) return true;
            return false;
        }
        internal TestPose PoseForTest(KSA.KittenEva owner) => _kittenSabers[owner].Pose;
        internal TestAudio AudioForTest(KSA.KittenEva owner) => _kittenSabers[owner].Audio;
        internal float VisibleLengthForTest(KSA.KittenEva owner)
        {
            return VisibleBladeLength(_kittenSabers[owner]);
        }
        internal void SetDeploymentForTest(KSA.KittenEva owner, float deployment) => _kittenSabers[owner].Blade.Deployment = deployment;
        internal void RemoveOwnerForTest(KSA.KittenEva owner) => RemoveClashOwner(owner);
        internal void ResetForTest() => ClearSaberClashes();
    }
}
