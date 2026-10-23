using System.Diagnostics;

namespace Purr2D2.Core;

/// <summary>A single best-effort compilation attempt. Polling, cancellation and unload never join it.</summary>
internal sealed class BackgroundJitWarmup : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, string> _prepare;
    private readonly Action<string> _report;
    private CancellationTokenSource? _cancel = new();
    private bool _started;
    private int _completed;

    internal BackgroundJitWarmup(Func<CancellationToken, string> prepare, Action<string> report)
    { _prepare = prepare; _report = report; }

    // Completion means an attempt finished, including failure/cancellation. Compilation is
    // an optimization, so a refused target must never permanently disable cutting.
    internal bool Completed => Volatile.Read(ref _completed) != 0;

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _cancel == null) return;
            _started = true;
            try
            {
                _ = Task.Factory.StartNew(Run, CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
            catch (Exception error)
            {
                Report("could not start; normal JIT fallback: " + error.Message);
                _cancel.Dispose(); _cancel = null;
                Volatile.Write(ref _completed, 1);
            }
        }
    }

    private void Run()
    {
        long started = Stopwatch.GetTimestamp();
        ThreadPriority? previous = null;
        CancellationToken cancel;
        lock (_gate) cancel = _cancel!.Token;
        try
        {
            try { previous = Thread.CurrentThread.Priority; Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            cancel.ThrowIfCancellationRequested();
            string summary = _prepare(cancel);
            cancel.ThrowIfCancellationRequested();
            Report(summary + $"; {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms total");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Report("cancelled on unload; normal JIT fallback"); }
        catch (Exception error) { Report("failed; normal JIT fallback: " + error.Message); }
        finally
        {
            if (previous is { } priority) try { Thread.CurrentThread.Priority = priority; } catch (Exception) { }
            lock (_gate) { _cancel?.Dispose(); _cancel = null; }
            Volatile.Write(ref _completed, 1);
        }
    }

    private void Report(string summary)
    {
        try { _report(summary); }
        catch (Exception) { /* A failed log sink cannot disable the fallback or fault an unobserved task. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_cancel == null) return;
            _cancel.Cancel();
            if (!_started)
            {
                _cancel.Dispose(); _cancel = null;
                Volatile.Write(ref _completed, 1);
            }
        }
    }
}
