namespace Purr2D2.Core;

/// <summary>Smooths the shared Force spread in log space without touching game state.</summary>
public sealed class ForceSpreadSmoother
{
    private const double SettledPositionEpsilon = 1e-5;
    private const double SettledVelocityEpsilon = 1e-4;
    private static readonly double MinimumLogSpread = Math.Log(ForceSpreadLimits.MinimumGroupSpread);
    private static readonly double MaximumLogSpread = Math.Log(ForceSpreadLimits.MaximumGroupSpread);

    private double _logPosition;
    private double _logVelocity;
    private double _logTarget;
    private double _spread = ForceSpreadLimits.DefaultGroupSpread;
    private double _targetSpread = ForceSpreadLimits.DefaultGroupSpread;

    /// <summary>The displayed multiplicative spread factor.</summary>
    public double Spread => _spread;

    /// <summary>The current target multiplicative spread factor.</summary>
    public double Target => _targetSpread;

    /// <summary>The current velocity in log-spread units per second.</summary>
    public double Velocity => _logVelocity;

    /// <summary>Retargets without discarding the current velocity.</summary>
    public void SetTarget(double spread)
    {
        if (!double.IsFinite(spread) || spread <= 0) return;
        spread = ClampSpread(spread);
        if (spread == _targetSpread) return;
        _targetSpread = spread;
        _logTarget = Math.Log(spread);
    }

    /// <summary>Seeds both position and target from a known displayed spread.</summary>
    public void Reset(double spread = ForceSpreadLimits.DefaultGroupSpread)
    {
        _spread = double.IsFinite(spread) && spread > 0
            ? ClampSpread(spread) : ForceSpreadLimits.DefaultGroupSpread;
        _logPosition = _logTarget = Math.Log(_spread);
        _targetSpread = _spread;
        _logVelocity = 0;
    }

    /// <summary>Advances an analytic critically damped spring by elapsed player time.</summary>
    public double Advance(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime <= 0) return _spread;

        double error = _logTarget - _logPosition;
        double distance = Math.Abs(error);
        double frequency = ForceOffsetSmoother.NaturalFrequency;
        if (distance <= SettledPositionEpsilon && Math.Abs(_logVelocity) <= SettledVelocityEpsilon)
        {
            SnapToTarget();
            return _spread;
        }

        // Limit only motion that could carry the spread through a stationary target.
        // Velocity is retained across retargets, so reversals may briefly carry onward.
        if (distance > SettledPositionEpsilon)
        {
            double toward = Math.Sign(error);
            double towardSpeed = _logVelocity * toward;
            double maximumTowardSpeed = frequency * distance;
            if (towardSpeed > maximumTowardSpeed)
                _logVelocity = toward * maximumTowardSpeed;
        }

        double scaledTime = frequency * deltaTime;
        if (!double.IsFinite(scaledTime) || scaledTime > 50)
        {
            SnapToTarget();
            return _spread;
        }

        double decay = Math.Exp(-scaledTime);
        double displacement = _logPosition - _logTarget;
        double accelerationTerm = _logVelocity + displacement * frequency;
        double nextPosition = _logTarget + (displacement + accelerationTerm * deltaTime) * decay;
        double nextVelocity = (_logVelocity - accelerationTerm * scaledTime) * decay;
        if (!double.IsFinite(nextPosition) || !double.IsFinite(nextVelocity))
        {
            SnapToTarget();
            return _spread;
        }

        if (nextPosition < MinimumLogSpread || nextPosition > MaximumLogSpread)
        {
            _logPosition = Math.Clamp(nextPosition, MinimumLogSpread, MaximumLogSpread);
            _logVelocity = 0;
            _spread = _logPosition == MinimumLogSpread
                ? ForceSpreadLimits.MinimumGroupSpread
                : ForceSpreadLimits.MaximumGroupSpread;
        }
        else
        {
            _logPosition = nextPosition;
            _logVelocity = nextVelocity;
            _spread = Math.Clamp(Math.Exp(_logPosition), ForceSpreadLimits.MinimumGroupSpread,
                ForceSpreadLimits.MaximumGroupSpread);
        }

        if (Math.Abs(_logTarget - _logPosition) <= SettledPositionEpsilon
            && Math.Abs(_logVelocity) <= SettledVelocityEpsilon)
            SnapToTarget();
        return _spread;
    }

    private void SnapToTarget()
    {
        _spread = _targetSpread;
        _logPosition = _logTarget;
        _logVelocity = 0;
    }

    private static double ClampSpread(double spread) =>
        Math.Clamp(spread, ForceSpreadLimits.MinimumGroupSpread, ForceSpreadLimits.MaximumGroupSpread);
}
