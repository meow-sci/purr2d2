using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private void AdvanceSaberAutoSwing(KittenSaber saber, double playerDt)
    {
        var swing = saber.AutoSwing;
        if (!swing.Enabled || !saber.Drop.Held) return;
        double dt = double.IsFinite(playerDt) && playerDt > 0
            && saber.Blade.Equipped && VisibleBladeLength(saber) > .001f
            && saber.Pose.Eligible && saber.Pose.HasFreshBodyFrame && !saber.Pose.ClashHeld
            ? Math.Min(playerDt, .1) : 0;
        swing.Advance(dt);
        swing.GetOffset(out float x, out float y);

        // Rebuild from the global calibration every frame. Automatic motion never
        // accumulates on top of a prior pose or changes the saved hand settings.
        float3 grip = new(_settings.GripX, _settings.GripY, _settings.GripZ);
        grip = new(Math.Clamp(grip.X - x * 9, -40, 40),
            Math.Clamp(grip.Y - y * 10, -40, 40), grip.Z);
        float3 aim = new(_settings.AimX, _settings.AimY, _settings.AimZ);
        aim = float3.Transform(aim, floatQuat.CreateFromAxisAngle(float3.UnitY, -x * .9f));
        aim = float3.Transform(aim, floatQuat.CreateFromAxisAngle(float3.UnitX, y * .75f));
        saber.Pose.GripOffsetFromChestCm = grip;
        saber.Pose.BladeDirectionModel = aim;
    }

    private bool SetSaberAutoSwing(KittenEva owner, bool enabled)
    {
        if (_fault != null || owner.IsDisposed || !_currentKittens.Contains(owner)
            || !_kittenSabers.TryGetValue(owner, out var saber)) return false;
        if (!enabled)
        {
            DisableSaberAutoSwing(saber);
            return true;
        }
        if (!saber.Drop.Held) return false;
        if (saber.AutoSwing.Enabled) return true;

        // Retire manual/assisted input through its existing safe request path before
        // automatic motion takes over this kitten's pose.
        if (ReferenceEquals(owner, _owner))
        {
            if (_overlayDrag.Active || _overlayDrag.Held) ResetOverlaySwing();
            if (SliceWorkActive || _selectingSlice) Queue(Command.CancelSlice);
        }
        saber.AutoSwing.SetEnabled(true);
        if (!SaberEnabledFor(owner)) QueueKittenSaber(owner, true);
        return true;
    }

    private void DisableSaberAutoSwing(KittenSaber? saber)
    {
        if (saber is null) return;
        saber.AutoSwing.SetEnabled(false);
    }

    private bool SetSaberAutoSwingDuration(KittenEva owner, float duration)
    {
        if (_fault != null || owner.IsDisposed || !_currentKittens.Contains(owner)
            || !_kittenSabers.TryGetValue(owner, out var saber)) return false;
        float previous = saber.AutoSwing.DurationSeconds;
        if (!saber.AutoSwing.SetDuration(duration)) return false;
        if (saber.AutoSwing.DurationSeconds != previous)
            saber.AutoSwingDurationText = saber.AutoSwing.DurationSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";
        return true;
    }

    private void ToggleSaberAutoSwing(KittenEva owner)
    {
        if (!_kittenSabers.TryGetValue(owner, out var saber)) return;
        SetSaberAutoSwing(owner, !saber.AutoSwing.Enabled);
    }
}
