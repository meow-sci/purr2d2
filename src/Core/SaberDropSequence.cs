namespace Purr2D2.Core;

/// <summary>Presentation stages; native body creation/removal belongs to the settled host.</summary>
internal sealed class SaberDropSequence
{
    internal enum Stage { Held, Reaching, Dropped, Returning }
    internal const double ReachDuration = .36, DefaultReturnDuration = 1, MinimumReturnDuration = .2;
    internal Stage Phase { get; private set; }
    internal double Time { get; private set; }
    internal float SpinMultiplier { get; private set; }
    internal double ReturnDuration { get; private set; } = DefaultReturnDuration;
    internal bool Held => Phase == Stage.Held;
    internal bool Detached => Phase is Stage.Dropped or Stage.Returning;
    internal float ReturnBlend => EaseOut((float)(Time / ReturnDuration));
    internal bool BeginDrop(float spinMultiplier = 0)
    {
        if (!Held) return false;
        SpinMultiplier = SaberDropMath.ValidateSpinMultiplier(spinMultiplier);
        Phase = Stage.Reaching; Time = 0; return true;
    }
    internal bool BeginReturn(double durationSeconds = DefaultReturnDuration)
    {
        if (Phase != Stage.Dropped) return false;
        ReturnDuration = SaberDropMath.ValidateReturnDuration(durationSeconds);
        Phase = Stage.Returning; Time = 0; return true;
    }
    internal void Advance(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return;
        Time = Math.Min(1, Time + Math.Min(dt, .1));
    }
    internal bool ReadyToRelease => Phase == Stage.Reaching && Time >= ReachDuration;
    internal bool ReadyToAttach => Phase == Stage.Returning && Time >= ReturnDuration;
    internal void Released() { Phase = Stage.Dropped; Time = 0; }
    internal void Reset() { Phase = Stage.Held; Time = 0; SpinMultiplier = 0; ReturnDuration = DefaultReturnDuration; }
    private static float EaseOut(float x)
    {
        x = Math.Clamp(x, 0, 1);
        float remaining = 1 - x;
        return 1 - remaining * remaining * remaining;
    }
}
