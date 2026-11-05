namespace Purr2D2.Core;

/// <summary>A held SWING drag can start sequential contact attempts, with a player-time pause between them.</summary>
internal sealed class ManualContactGesture
{
    internal const double CooldownSeconds = .25;

    internal ulong Id { get; private set; }
    internal bool Held { get; private set; }
    internal bool Spent { get; private set; }
    internal bool InFlight { get; private set; }
    internal double CooldownUntil { get; private set; }
    internal bool CanDetect => Held && !Spent && !InFlight;
    internal bool CanDetectAt(double playerTime) => CanDetect && double.IsFinite(playerTime) && playerTime >= CooldownUntil;
    internal void Begin() { if (Id == ulong.MaxValue) throw new InvalidOperationException("Swing gesture counter exhausted."); Id++; Held = true; Spent = false; }
    internal void Release() => Held = false;
    internal bool Consume(ulong id, double playerTime)
    {
        if (!CanDetectAt(playerTime) || id != Id) return false;
        Spent = InFlight = true;
        return true;
    }
    // The runtime may submit only frozen held intervals or the last unconsumed held pose.
    // Releasing the button changes ownership of that event, not its gesture id.
    internal bool ConsumeReleased(ulong id, double playerTime)
    {
        if (Held || Spent || InFlight || id == 0 || id != Id || !double.IsFinite(playerTime) || playerTime < CooldownUntil) return false;
        Spent = InFlight = true;
        return true;
    }
    internal void Finish() => InFlight = false;
    internal void FinishAttempt(double playerTime)
    {
        InFlight = false;
        Spent = false;
        if (double.IsFinite(playerTime)) CooldownUntil = Math.Max(CooldownUntil, playerTime + CooldownSeconds);
    }
    internal void Cancel() { Held = false; Spent = true; InFlight = false; }
}
