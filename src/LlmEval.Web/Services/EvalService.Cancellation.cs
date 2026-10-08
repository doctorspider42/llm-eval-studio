using LlmEval.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

public partial class EvalService
{
    public Task CancelResultAsync(Guid id, CancellationToken ct = default) => CancelAsync(id, "result", false, ct);
    public Task CancelJudgeAsync(Guid id, CancellationToken ct = default) => CancelAsync(id, "judge", true, ct);
    public Task CancelIterationAsync(Guid id, bool judges = false, CancellationToken ct = default) => CancelAsync(id, "iteration", judges, ct);
    public Task CancelBatchAsync(Guid id, bool judges = false, CancellationToken ct = default) => CancelAsync(id, "series", judges, ct);

    private async Task CancelAsync(Guid id, string scope, bool judges, CancellationToken ct)
    {
        var changed = new HashSet<Guid>();
        await JudgeLock.WaitAsync(ct);
        try
        {
            await control.Gate.WaitAsync(ct);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var iterations = db.Iterations.AsQueryable();
                if (scope == "series")
                {
                    var batch = await db.Batches.FindAsync([id], ct) ?? throw EvalException.NotFound("series");
                    batch.AutoJudge = false;
                    iterations = iterations.Where(i => i.BatchId == id);
                }
                else if (scope == "iteration")
                {
                    var iteration = await db.Iterations.FindAsync([id], ct) ?? throw EvalException.NotFound("iteration");
                    iteration.AutoJudgeSuppressed = true;
                    iterations = iterations.Where(i => i.Id == id);
                }

                var now = DateTimeOffset.UtcNow;
                if (judges)
                {
                    var query = db.JudgeRuns.AsQueryable();
                    query = scope == "judge" ? query.Where(r => r.Id == id) : query.Where(r => iterations.Select(i => i.Id).Contains(r.IterationId));
                    if (scope == "judge" && !await query.AnyAsync(ct)) throw EvalException.NotFound("result");
                    foreach (var run in await query.Where(r => r.Status == ResultStatus.Pending || r.Status == ResultStatus.Running).ToListAsync(ct))
                    {
                        run.Status = ResultStatus.Cancelled;
                        run.CompletedAt = now;
                        changed.Add(run.IterationId);
                        control.Cancel(run.Id);
                    }
                }
                else
                {
                    var query = db.Results.AsQueryable();
                    query = scope == "result" ? query.Where(r => r.Id == id) : query.Where(r => iterations.Select(i => i.Id).Contains(r.IterationId));
                    if (scope == "result" && !await query.AnyAsync(ct)) throw EvalException.NotFound("result");
                    foreach (var result in await query.Where(r => r.Status == ResultStatus.Pending || r.Status == ResultStatus.Running).ToListAsync(ct))
                    {
                        result.Status = ResultStatus.Cancelled;
                        result.CompletedAt = now;
                        changed.Add(result.IterationId);
                        control.Cancel(result.Id);
                    }
                }
                // Once provider calls are cancelled, persist even if the requesting browser disconnects.
                await db.SaveChangesAsync(CancellationToken.None);
            }
            finally { control.Gate.Release(); }
        }
        finally { JudgeLock.Release(); }
        foreach (var iterationId in changed) events.RaiseIteration(iterationId);
        events.Raise();
    }
}
