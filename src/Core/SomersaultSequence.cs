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
    private readonly double[] _extraTurnStarts = new double[SomersaultCombo.MaximumJumps - 1];
    private int _extraTurns;
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

    internal bool AddTurn(double startElapsed)
    {
        if (Phase != SomersaultPhase.Airborne || _extraTurns >= _extraTurnStarts.Length
            || !double.IsFinite(startElapsed) || startElapsed < Elapsed) return false;
        _extraTurnStarts[_extraTurns++] = startElapsed;
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
        double lastStart = _extraTurns == 0 ? 0 : _extraTurnStarts[_extraTurns - 1];
        if (interrupted || landed || elapsed >= lastStart + Math.Max(5, _profile.FlightSeconds * 3))
        {
            _recoveryStart = elapsed;
            _recoveryRotation = RotationRadians;
            // An early collision gets the shortest return to upright. It never
            // forces a complete flourish after native collision handling intervenes.
            _recoveryTarget = MathF.Round(RotationRadians / MathF.Tau) * MathF.Tau;
            _recoveryTuck = Tuck;
            Phase = SomersaultPhase.Recovering;
            return;
        }
        double phase = elapsed / _profile.FlightSeconds;
        RotationRadians = MathF.Tau * Smooth((phase - .035) / .80);
        Tuck = Smooth((phase - .035) / .16) * (1 - Smooth((phase - .58) / .24));
        for (int i = 0; i < _extraTurns; i++)
        {
            double extraPhase = (elapsed - _extraTurnStarts[i]) / _profile.FlightSeconds;
            RotationRadians += MathF.Tau * Smooth((extraPhase - .035) / .80);
            Tuck = Math.Max(Tuck, Smooth((extraPhase - .035) / .16) * (1 - Smooth((extraPhase - .58) / .24)));
        }
        // Hold all requested completed turns when the ground falls away; only
        // an acknowledged boost adds another turn, never an estimated landing.
    }

    internal void Reset()
    {
        Phase = SomersaultPhase.Idle;
        HasLeftGround = false;
        RotationRadians = Tuck = 0;
        Elapsed = 0;
        _profile = default;
        _extraTurns = 0;
        _recoveryStart = 0;
        _recoveryRotation = _recoveryTarget = _recoveryTuck = 0;
    }

    private static float Smooth(double value)
    {
        float t = (float)Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
