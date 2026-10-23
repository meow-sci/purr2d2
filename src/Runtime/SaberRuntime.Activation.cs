namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    // Session-only: every launch requires opting in from the game's menu.
    private bool _enabled, _deactivationPending;

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        SetSaberOverlayVisible(enabled);
        if (enabled) _fault = null;
        if (!enabled)
        {
            _windowVisible = false;
            _settingsWindowBoundsAvailable = false;
            _deactivationPending = true;
        }
    }

    // UI callbacks must never release native physics/poses while workers run.
    // Retain this reset even if off/on are clicked before the next settled frame.
    private bool ApplyActivation()
    {
        if (_deactivationPending)
        {
            ResetWorld();
            _deactivationPending = false;
        }
        return _enabled;
    }
}
