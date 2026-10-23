namespace Purr2D2.Core;

internal enum SomersaultPhase { Idle, Queued, AwaitingAcknowledgement, Airborne, Recovering }

/// <summary>Pose timing consumes applied simulation time, never UI/player time.</summary>
internal sealed class SomersaultSequence
{
    internal const double RecoverySeconds = .14;
    internal SomersaultPhase Phase { get; private set; }
    internal bool Active => Phase != SomersaultPhase.Idle;
    internal bool HasLeftGround { get; private set; }
    internal float RotationRadians { get; private set; }
    internal float Tuck { get; private set; }
    internal double Elapsed { get; private set; }
    private SomersaultProfile _profile;
    private double _recoveryStart;
    private float _recoveryRotation, _recoveryTarget, _recoveryTuck;

    internal bool Queue()
    {
        if (Active) return false;
        Reset();
        Phase = SomersaultPhase.Queued;
        return true;
    }

    internal bool Dispatched(SomersaultProfile profile)
    {
        if (Phase != SomersaultPhase.Queued || !double.IsFinite(profile.FlightSeconds) || profile.FlightSeconds <= 0) return false;
        _profile = profile;
        Phase = SomersaultPhase.AwaitingAcknowledgement;
        return true;
    }

    internal bool Acknowledge()
    {
        if (Phase != SomersaultPhase.AwaitingAcknowledgement) return false;
        Phase = SomersaultPhase.Airborne;
        return true;
    }

    internal void AdvanceTo(double elapsed, bool airborne, bool terrainContact, bool contactCleared, bool interrupted,
        double liftoffGraceSeconds)
    {
        if (Phase is not (SomersaultPhase.Airborne or SomersaultPhase.Recovering)
            || !double.IsFinite(elapsed) || elapsed <= Elapsed) return;
        Elapsed = elapsed;
        if (Phase == SomersaultPhase.Recovering)
        {
            float recovery = Smooth((elapsed - _recoveryStart) / RecoverySeconds);
            RotationRadians = _recoveryRotation + (_recoveryTarget - _recoveryRotation) * recovery;
            Tuck = _recoveryTuck * (1 - recovery);
            if (recovery >= 1) Reset();
            return;
        }
        HasLeftGround |= contactCleared || airborne && !terrainContact;
        bool landed = !airborne && terrainContact && (HasLeftGround || elapsed >= Math.Max(.05, liftoffGraceSeconds));
        if (interrupted || landed || elapsed >= Math.Max(5, _profile.FlightSeconds * 3))
        {
            _recoveryStart = elapsed;
            _recoveryRotation = RotationRadians;
            // An early collision gets the shortest return to upright. It never
            // forces a complete flourish after native collision handling intervenes.
            _recoveryTarget = RotationRadians > MathF.PI ? MathF.Tau : 0;
            _recoveryTuck = Tuck;
            Phase = SomersaultPhase.Recovering;
            return;
        }
        double phase = elapsed / _profile.FlightSeconds;
        RotationRadians = MathF.Tau * Smooth((phase - .035) / .80);
        Tuck = Smooth((phase - .035) / .16) * (1 - Smooth((phase - .58) / .24));
        // When the ground falls away, hold one completed upright turn and wait
        // for real contact or timeout. No second flip or estimated-ground snap.
    }

    internal void Reset()
    {
        Phase = SomersaultPhase.Idle;
        HasLeftGround = false;
        RotationRadians = Tuck = 0;
        Elapsed = 0;
        _profile = default;
        _recoveryStart = 0;
        _recoveryRotation = _recoveryTarget = _recoveryTuck = 0;
    }

    private static float Smooth(double value)
    {
        float t = (float)Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
