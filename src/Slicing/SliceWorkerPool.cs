using System.Collections.Concurrent;

namespace Purr2D2.Slicing;

/// <summary>Fixed background workers. Closing the pool never joins them on the game thread.</summary>
internal sealed class SliceWorkerPool : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private int _closed;
    private int _active;
    internal int WorkerCount { get; }
    internal int ActiveCount => Volatile.Read(ref _active);
    internal int PendingCount => _tasks.Count;

    internal SliceWorkerPool(int count)
    {
        WorkerCount = Math.Clamp(count, 1, 4);
        for (int i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(Run) { IsBackground = true, Name = $"Purr2D2 slice {i + 1}" };
            thread.Start();
        }
    }

    private void Run()
    {
        foreach (var task in _tasks.GetConsumingEnumerable())
        {
            Interlocked.Increment(ref _active);
            try { TryExecuteTask(task); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    protected override void QueueTask(Task task) => _tasks.Add(task);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
    public override int MaximumConcurrencyLevel => WorkerCount;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _tasks.CompleteAdding();
    }
}
