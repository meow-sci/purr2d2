namespace Purr2D2.Core;

internal enum SliceCue { None, BeginSwing, Commit }

/// <summary>One button press, one visible stroke, at most one structural commit.</summary>
internal sealed class AssistedSliceSequence
{
    internal const double SwingSeconds = .72;
    internal bool Active { get; private set; }
    internal bool Swinging { get; private set; }
    internal double Progress { get; private set; }
    internal void Start() { Active = true; Swinging = false; Progress = 0; }
    internal void Cancel() { Active = Swinging = false; Progress = 0; }
    internal SliceCue Advance(double playerDt, bool ready, bool freshPose)
    {
        if (!Active || !ready || !freshPose || !double.IsFinite(playerDt) || playerDt <= 0) return SliceCue.None;
        if (!Swinging) { Swinging = true; return SliceCue.BeginSwing; }
        Progress = Math.Min(1, Progress + Math.Min(playerDt, .1) / SwingSeconds);
        if (Progress < 1) return SliceCue.None;
        // Consume before invoking any external/native operation, including one that fails.
        Active = Swinging = false;
        return SliceCue.Commit;
    }
}
