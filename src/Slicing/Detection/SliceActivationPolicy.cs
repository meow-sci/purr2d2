namespace Purr2D2.Slicing.Detection;

internal enum SliceContactAuthorization { None, ManualContact, PassiveContact }

// Called at the joined handoff; changing authorization starts a new sample epoch.
// It authorizes detection only, never cancellation of already accepted work.
internal sealed class SliceActivationPolicy
{
    internal SliceContactAuthorization Kind { get; private set; }
    internal ulong Epoch { get; private set; }
    private ulong _gesture;
    private double _lastPassiveMotion = double.NaN;

    internal bool Update(bool manualHeld, ulong gesture, bool passiveEnabled, bool eligible)
    {
        var next = !eligible ? SliceContactAuthorization.None
            : manualHeld && gesture != 0 ? SliceContactAuthorization.ManualContact
            : passiveEnabled ? SliceContactAuthorization.PassiveContact : SliceContactAuthorization.None;
        ulong nextGesture = next == SliceContactAuthorization.ManualContact ? gesture : 0;
        if (next == Kind && nextGesture == _gesture) return false;
        Kind = next; _gesture = nextGesture;
        _lastPassiveMotion = double.NaN;
        if (Epoch == ulong.MaxValue) throw new InvalidOperationException("Slice contact epoch exhausted.");
        Epoch++;
        return true;
    }

    // A passive stroke continues through ordinary sample gaps, but a new movement
    // after 250 ms of quiet starts a new epoch and forces both detectors to prime.
    internal bool ObservePassiveMotion(double now, bool moving)
    {
        if (Kind != SliceContactAuthorization.PassiveContact || !double.IsFinite(now)) return false;
        if (!moving) return false;
        bool restart = double.IsFinite(_lastPassiveMotion) && (now < _lastPassiveMotion || now - _lastPassiveMotion >= .25);
        _lastPassiveMotion = now;
        if (!restart) return false;
        if (Epoch == ulong.MaxValue) throw new InvalidOperationException("Slice contact epoch exhausted.");
        Epoch++;
        return true;
    }
}
