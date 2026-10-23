namespace Purr2D2.Core;

/// <summary>One cancellable worker. Polling and cancellation never join it on the game thread.</summary>
internal sealed class BackgroundSliceWork<TPlan, TResult> where TPlan : class where TResult : class
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task<TResult> _task;
    private TPlan? _plan;
    private bool _cancelled, _released;
    internal TPlan? Plan => Volatile.Read(ref _plan);
    internal bool Finished => _task.IsCompleted;
    internal BackgroundSliceWork(Func<CancellationToken, Action<TPlan>, TResult> calculate)
    {
        _task = Task.Factory.StartNew(() =>
        {
            var previous = Thread.CurrentThread.Priority;
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (PlatformNotSupportedException) { }
                _cancel.Token.ThrowIfCancellationRequested();
                return calculate(_cancel.Token, plan => Volatile.Write(ref _plan, plan));
            }
            finally
            {
                try { Thread.CurrentThread.Priority = previous; } catch (PlatformNotSupportedException) { }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    internal bool TryTake(out TResult? result, out string? error)
    {
        result = null; error = null;
        if (!_task.IsCompleted) return false;
        // Reading Result before IsCompleted would stall the rendering thread.
        if (_task.IsCompletedSuccessfully && !_cancelled) result = _task.GetAwaiter().GetResult();
        else error = _task.Exception?.GetBaseException().Message ?? "Slice cancelled.";
        if (!_released) { _cancel.Dispose(); _released = true; }
        return true;
    }
    internal void Cancel() { _cancelled = true; if (!_released) _cancel.Cancel(); }
}
