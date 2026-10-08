using System.Diagnostics;
using System.Threading.Channels;
using LlmEval.Web.Data;
using LlmEval.Web.Llm;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

/// <summary>In-process pub/sub so Blazor circuits can live-refresh when results land.</summary>
public class EvalEvents
{
    /// <summary>Raised with the iteration id whenever a result or rating inside it changes.</summary>
    public event Action<Guid>? IterationChanged;
    /// <summary>Raised when anything changes (lists, dashboard).</summary>
    public event Action? Changed;

    public void RaiseIteration(Guid iterationId)
    {
        IterationChanged?.Invoke(iterationId);
        Changed?.Invoke();
    }

    public void Raise() => Changed?.Invoke();
}

public class ResultQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public void Enqueue(Guid resultId) => _channel.Writer.TryWrite(resultId);
}

public class IterationRunner(
    ResultQueue queue,
    IDbContextFactory<AppDbContext> dbFactory,
    LlmClientFactory clients,
    EvalEvents events,
    EvalService evalService,
    IConfiguration config,
    ILogger<IterationRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        var parallelism = config.GetValue("Runner:MaxParallelism", 6);
        await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stoppingToken },
            async (id, ct) =>
            {
                try { await RunAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { log.LogError(ex, "Runner crashed on result {ResultId}", id); }
            });
    }

    /// <summary>Anything left Pending/Running from a previous process gets another shot.</summary>
    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = await db.Results
                .Where(r => r.Status == ResultStatus.Pending || r.Status == ResultStatus.Running)
                .Select(r => r.Id).ToListAsync(ct);
            foreach (var id in ids) queue.Enqueue(id);
            if (ids.Count > 0) log.LogInformation("Requeued {Count} unfinished results", ids.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not requeue unfinished results");
        }
    }

    private async Task RunAsync(Guid resultId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.Results
            .Include(r => r.Iteration)
            .Include(r => r.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(r => r.Id == resultId, ct);
        if (result is null || result.Status is ResultStatus.Completed or ResultStatus.Failed) return;

        result.Status = ResultStatus.Running;
        result.StartedAt = DateTimeOffset.UtcNow;
        result.Error = null;
        await db.SaveChangesAsync(ct);
        events.RaiseIteration(result.IterationId);

        var sw = Stopwatch.StartNew();
        try
        {
            var request = new LlmRequest(result.Model.Provider, result.Model,
                result.Iteration.SystemPromptSnapshot, result.Iteration.UserMessageSnapshot);
            var response = await clients.For(result.Model.Provider.Type).CompleteAsync(request, ct);

            result.Output = response.Output;
            result.InputTokens = response.InputTokens;
            result.OutputTokens = response.OutputTokens;
            result.Status = ResultStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutting down – stays Running and will be requeued on next start
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LLM call failed for result {ResultId} ({Model})", resultId, result.Model.DisplayName);
            result.Status = ResultStatus.Failed;
            result.Error = ex is LlmException or HttpRequestException or TaskCanceledException
                ? ex.Message
                : ex.ToString();
        }

        result.LatencyMs = sw.ElapsedMilliseconds;
        result.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        events.RaiseIteration(result.IterationId);

        if (result.Iteration.BatchId is not null)
        {
            try { await evalService.AutoJudgeIfReadyAsync(result.IterationId, CancellationToken.None); }
            catch (Exception ex) { log.LogWarning(ex, "Auto-judge trigger failed for iteration {IterationId}", result.IterationId); }
        }
    }
}
