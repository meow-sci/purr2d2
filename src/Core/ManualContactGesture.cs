namespace Purr2D2.Core;

/// <summary>A real SWING press owns one contact attempt; release/return never rearms it.</summary>
internal sealed class ManualContactGesture
{
    internal ulong Id { get; private set; }
    internal bool Held { get; private set; }
    internal bool Spent { get; private set; }
    internal bool InFlight { get; private set; }
    internal bool CanDetect => Held && !Spent && !InFlight;
    internal void Begin() { if (Id == ulong.MaxValue) throw new InvalidOperationException("Swing gesture counter exhausted."); Id++; Held = true; Spent = false; }
    internal void Release() => Held = false;
    internal bool Consume(ulong id)
    {
        if (!CanDetect || id != Id) return false;
        Spent = InFlight = true;
        return true;
    }
    internal void Finish() => InFlight = false;
    internal void Cancel() { Held = false; Spent = true; InFlight = false; }
}
