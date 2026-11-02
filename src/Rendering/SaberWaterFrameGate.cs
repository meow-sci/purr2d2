namespace Purr2D2.Rendering;

/// <summary>Only an actual current native copy may defer one main-view blade submission.</summary>
internal sealed class SaberWaterFrameGate
{
    private object? _viewport, _target;
    private ulong _frame;
    private nint _command, _color, _depth, _opaque;
    private bool _captured, _deferred, _consumed;

    internal void Reset() { _captured = _deferred = _consumed = false; _viewport = _target = null; }
    internal void Capture(ulong frame, object viewport, object target, nint command, nint color, nint depth, nint opaque)
    {
        Reset();
        if (command == 0 || color == 0 || depth == 0 || opaque == 0) return;
        _frame=frame; _viewport=viewport; _target=target; _command=command; _color=color; _depth=depth; _opaque=opaque;
        _captured=true;
    }
    internal bool Matches(ulong frame, object viewport, object target, nint command, nint color, nint depth, nint opaque) =>
        _captured && !_consumed && _frame == frame && ReferenceEquals(viewport,_viewport) && ReferenceEquals(target,_target)
        && command == _command && color == _color && depth == _depth && opaque == _opaque;
    internal bool TryDefer(ulong frame, object viewport, object target, nint command, nint color, nint depth, nint opaque)
    {
        if (!Matches(frame,viewport,target,command,color,depth,opaque)) return false;
        _deferred=true; return true;
    }
    internal bool TryConsume(ulong frame, object viewport, object target, nint command, nint color, nint depth, nint opaque)
    {
        if (!_deferred || !Matches(frame,viewport,target,command,color,depth,opaque)) return false;
        _consumed=true; return true;
    }
    internal bool HasPending(ulong frame, object viewport, nint command) => _deferred && !_consumed
        && _frame == frame && ReferenceEquals(viewport,_viewport) && _command == command;
    internal bool ConsumePending(ulong frame, object viewport, nint command)
    {
        if (!HasPending(frame,viewport,command)) return false;
        _consumed=true; return true;
    }
}
