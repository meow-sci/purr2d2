namespace Purr2D2.Core;

internal enum SliceCue { None, BeginSwing, Commit }

/// <summary>The final pose must be submitted by the main view before a ready result can commit.</summary>
internal sealed class AssistedSliceSequence
{
    internal const double SwingSeconds = SaberSwing.Duration;
    internal bool Active { get; private set; }
    internal bool Swinging { get; private set; }
    internal double Progress { get; private set; }
    internal bool EndPresented { get; private set; }
    internal void Start() { Active = true; Swinging = EndPresented = false; Progress = 0; }
    internal void Cancel() { Active = Swinging = EndPresented = false; Progress = 0; }
    internal void NotifyRendered() { if (Active && Progress >= 1) EndPresented = true; }
    internal SliceCue Advance(double playerDt, bool ready, bool freshPose, bool resultReady)
    {
        if (!Active || !ready || !freshPose || !double.IsFinite(playerDt) || playerDt <= 0) return SliceCue.None;
        if (Progress >= 1)
        {
            if (!EndPresented || !resultReady) return SliceCue.None;
            Active = false; // Consume before the native transaction, including one that fails.
            return SliceCue.Commit;
        }
        if (!Swinging) { Swinging = true; return SliceCue.BeginSwing; }
        Progress = Math.Min(1, Progress + Math.Min(playerDt, .1) / SwingSeconds);
        if (Progress >= 1) Swinging = false;
        return SliceCue.None;
    }
}
