namespace LlmEval.Web.Services;

/// <summary>Serializes state transitions with cancellation, while provider calls run concurrently.</summary>
public sealed class RunControl
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly Dictionary<Guid, (CancellationTokenSource Source, TaskCompletionSource Done)> _active = [];

    public async Task RunAsync(Guid id, CancellationToken hostToken, Func<CancellationToken, Task> action)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Gate.WaitAsync(hostToken);
        try
        {
            if (!_active.TryAdd(id, (source, done))) return;
        }
        finally { Gate.Release(); }

        try { await action(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            await Gate.WaitAsync(CancellationToken.None);
            try { _active.Remove(id); done.TrySetResult(); }
            finally { Gate.Release(); }
        }
    }

    // Caller holds Gate; cancellation and CTS disposal cannot race.
    public void Cancel(Guid id)
    {
        if (_active.TryGetValue(id, out var run)) run.Source.Cancel();
    }

    public async Task WaitForIdleAsync(Guid id, CancellationToken ct)
    {
        Task? pending;
        await Gate.WaitAsync(ct);
        try { pending = _active.TryGetValue(id, out var run) ? run.Done.Task : null; }
        finally { Gate.Release(); }
        if (pending is not null) await pending.WaitAsync(ct);
    }
}
