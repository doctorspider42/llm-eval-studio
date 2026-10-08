using LlmEval.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

public record ReportRating(string User, bool IsAi, int Stars, string? Comment);

public record ReportAnswer(string Alias, string? ModelName, int Repetition, ResultStatus Status, double? AvgStars, string? Output,
    string? Error, List<ReportRating> Ratings);

public record ReportCase(Guid TestCaseId, string Title, string? SystemPrompt, string UserMessage, string? ExpectedAnswer,
    List<ReportAnswer> Answers);

public record BatchReport(BatchDto Batch, List<ReportCase> Cases, List<string> JudgeNames, int HumanRatings, int AiRatings,
    DateTimeOffset GeneratedAt);

public record ReportOptions(string Lang, bool Reveal = true, bool Comments = true, bool Answers = false, bool AutoPrint = false);

/// <summary>Collects everything a series report needs in one pass (the per-model numbers come from <see cref="EvalService.GetBatchAsync"/>).</summary>
public class ReportService(IDbContextFactory<AppDbContext> dbFactory, EvalService svc)
{
    public async Task<BatchReport> BuildBatchReportAsync(Guid batchId, bool reveal, CancellationToken ct = default)
    {
        var batch = await svc.GetBatchAsync(batchId, reveal, null, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var modelIds = await db.Batches.Where(b => b.Id == batchId).Select(b => b.ModelIds).FirstAsync(ct);
        var iterations = await db.Iterations.AsNoTracking().AsSplitQuery()
            .Where(i => i.BatchId == batchId)
            .Include(i => i.TestCase)
            .Include(i => i.Results).ThenInclude(r => r.Model)
            .Include(i => i.Results).ThenInclude(r => r.Ratings).ThenInclude(r => r.User)
            .Include(i => i.JudgeRuns).ThenInclude(j => j.Model)
            .ToListAsync(ct);

        // Same alias assignment as the series page: batch order first, then any stragglers.
        var order = modelIds.Concat(iterations.SelectMany(i => i.Results).Select(r => r.ModelId)).Distinct().ToList();
        var alias = order.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => EvalService.Alias(x.i));

        var cases = iterations
            .GroupBy(i => i.TestCaseId)
            .Select(g =>
            {
                var first = g.OrderBy(i => i.Repetition).First();
                var answers = g.SelectMany(i => i.Results.Select(r => (Rep: i.Repetition ?? 1, R: r)))
                    .OrderBy(x => order.IndexOf(x.R.ModelId)).ThenBy(x => x.Rep)
                    .Select(x => new ReportAnswer(
                        alias[x.R.ModelId],
                        reveal ? x.R.Model.DisplayName : null,
                        x.Rep,
                        x.R.Status,
                        x.R.Ratings.Count > 0 ? x.R.Ratings.Average(r => r.Stars) : null,
                        x.R.Output,
                        x.R.Error,
                        x.R.Ratings.OrderBy(r => r.User.JudgeModelId is null ? 0 : 1).ThenBy(r => r.CreatedAt)
                            .Select(r => new ReportRating(r.User.Name, r.User.JudgeModelId is not null, r.Stars, r.Comment)).ToList()))
                    .ToList();
                return new ReportCase(g.Key, first.TestCase.Title, first.SystemPromptSnapshot, first.UserMessageSnapshot,
                    first.ExpectedAnswerSnapshot, answers);
            })
            .OrderBy(c => c.Title)
            .ToList();

        var ratings = iterations.SelectMany(i => i.Results).SelectMany(r => r.Ratings).ToList();
        var judges = iterations.SelectMany(i => i.JudgeRuns).Where(j => j.Status == ResultStatus.Completed)
            .Select(j => j.Model.DisplayName).Distinct().Order().ToList();

        return new BatchReport(batch, cases, judges,
            ratings.Count(r => r.User.JudgeModelId is null),
            ratings.Count(r => r.User.JudgeModelId is not null),
            DateTimeOffset.UtcNow);
    }
}
