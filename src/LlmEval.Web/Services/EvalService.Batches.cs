using LlmEval.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

/// <summary>Series ("serie"): one model set × many test cases × N repetitions, judged and summarised as a whole.</summary>
public partial class EvalService
{
    private const int MaxBatchCalls = 3000;

    /// <summary>Blind alias within a series: "A", "B"… (UI renders it as "Candidate A").</summary>
    public static string Alias(int index) => $"{(char)('A' + index % 26)}{(index >= 26 ? (index / 26).ToString() : "")}";

    public async Task<BatchDto> CreateBatchAsync(CreateBatchRequest req, CancellationToken ct = default)
    {
        var testCaseIds = (req.TestCaseIds ?? []).Distinct().ToList();
        var modelIds = (req.ModelIds ?? []).Distinct().ToList();
        if (testCaseIds.Count == 0) throw EvalException.Invalid("errors.selectTestCase");
        if (modelIds.Count == 0) throw EvalException.Invalid("errors.selectModel");
        if (req.Repetitions is < 1 or > 20) throw EvalException.Invalid("errors.repetitionsRange");
        var calls = testCaseIds.Count * modelIds.Count * req.Repetitions;
        if (calls > MaxBatchCalls) throw EvalException.Invalid("errors.tooManyCalls", calls, MaxBatchCalls);

        Guid batchId;
        var queueOrder = new List<(int Rep, Guid ResultId)>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cases = await db.TestCases.Where(t => testCaseIds.Contains(t.Id)).OrderBy(t => t.Title).ToListAsync(ct);
            if (cases.Count != testCaseIds.Count) throw EvalException.Invalid("errors.someTestCasesMissing");

            var models = await db.Models.Include(m => m.Provider).Where(m => modelIds.Contains(m.Id)).ToListAsync(ct);
            if (models.Count != modelIds.Count) throw EvalException.Invalid("errors.someModelsMissing");
            var disabled = models.Where(m => !m.Enabled || !m.Provider.Enabled).Select(m => m.DisplayName).ToList();
            if (disabled.Count > 0) throw EvalException.Invalid("errors.disabledModels", string.Join(", ", disabled));

            var judgeIds = (req.JudgeModelIds ?? []).Distinct().ToList();
            if (req.AutoJudge) await ResolveJudgesAsync(db, judgeIds, ct); // fail fast if there's nobody to judge

            var batch = new Batch
            {
                Name = Blank(req.Name) ?? $"Series {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
                Note = Blank(req.Note),
                Repetitions = req.Repetitions,
                ModelIds = models.OrderBy(_ => Random.Shared.Next()).Select(m => m.Id).ToList(),
                AutoJudge = req.AutoJudge,
                JudgeModelIds = judgeIds,
                CreatedById = await ExistingUserId(db, req.UserId, ct),
            };

            var lastNumbers = await db.Iterations.Where(i => testCaseIds.Contains(i.TestCaseId))
                .GroupBy(i => i.TestCaseId).Select(g => new { g.Key, Max = g.Max(i => i.Number) })
                .ToDictionaryAsync(x => x.Key, x => x.Max, ct);

            foreach (var t in cases)
            {
                var number = lastNumbers.GetValueOrDefault(t.Id);
                for (var rep = 1; rep <= req.Repetitions; rep++)
                {
                    var iteration = new Iteration
                    {
                        TestCaseId = t.Id,
                        Number = ++number,
                        Repetition = rep,
                        SystemPromptSnapshot = t.SystemPrompt,
                        UserMessageSnapshot = PromptComposer.Compose(t.Prompt, t.Data),
                        ExpectedAnswerSnapshot = t.ExpectedAnswer,
                        CreatedById = batch.CreatedById,
                    };
                    var shuffled = models.OrderBy(_ => Random.Shared.Next()).ToList();
                    for (var i = 0; i < shuffled.Count; i++)
                        iteration.Results.Add(new IterationResult { ModelId = shuffled[i].Id, Slot = i + 1, Status = ResultStatus.Pending });
                    batch.Iterations.Add(iteration);
                }
            }

            db.Batches.Add(batch);
            await db.SaveChangesAsync(ct);
            batchId = batch.Id;
            queueOrder.AddRange(batch.Iterations.SelectMany(i => i.Results.Select(r => (i.Repetition ?? 1, r.Id))));
        }

        // Repetition 1 of every case first, so a usable picture shows up early.
        foreach (var (_, id) in queueOrder.OrderBy(x => x.Rep)) queue.Enqueue(id);
        events.Raise();
        return await GetBatchAsync(batchId, reveal: false, ct: ct);
    }

    public async Task<List<BatchSummaryDto>> GetBatchesAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await BatchSummaries(db.Batches.OrderByDescending(b => b.CreatedAt).Take(Math.Clamp(take, 1, 500))).ToListAsync(ct);
    }

    private static IQueryable<BatchSummaryDto> BatchSummaries(IQueryable<Batch> q) =>
        q.Select(b => new BatchSummaryDto(
            b.Id, b.Name, b.Note,
            b.CreatedBy != null ? b.CreatedBy.Name : null,
            b.CreatedAt,
            b.Iterations.Select(i => i.TestCaseId).Distinct().Count(),
            b.ModelIds.Count,
            b.Repetitions,
            b.Iterations.Count,
            b.Iterations.SelectMany(i => i.Results).Count(),
            b.Iterations.SelectMany(i => i.Results).Count(r => r.Status == ResultStatus.Completed || r.Status == ResultStatus.Failed),
            b.Iterations.SelectMany(i => i.Results).Count(r => r.Status == ResultStatus.Failed),
            b.Iterations.SelectMany(i => i.JudgeRuns).Count(),
            b.Iterations.SelectMany(i => i.JudgeRuns).Count(j => j.Status == ResultStatus.Completed || j.Status == ResultStatus.Failed),
            b.AutoJudge,
            b.Iterations.SelectMany(i => i.Results).SelectMany(r => r.Ratings).Average(x => (double?)x.Stars)));

    public async Task<BatchDto> GetBatchAsync(Guid id, bool reveal, Guid? userId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var summary = await BatchSummaries(db.Batches.Where(b => b.Id == id)).FirstOrDefaultAsync(ct)
                      ?? throw EvalException.NotFound("series");
        var batch = await db.Batches.AsNoTracking().FirstAsync(b => b.Id == id, ct);

        // Project only what the overview needs – no answer texts.
        var its = await db.Iterations.AsNoTracking().AsSplitQuery()
            .Where(i => i.BatchId == id)
            .Select(i => new
            {
                i.Id, i.TestCaseId, Title = i.TestCase.Title, i.Number, Rep = i.Repetition ?? 1,
                Results = i.Results.Select(r => new
                {
                    r.ModelId, r.Status, r.LatencyMs, r.OutputTokens,
                    Ratings = r.Ratings.Select(x => new { x.Stars, x.UserId, Ai = x.User.JudgeModelId != null }).ToList()
                }).ToList(),
                Judges = i.JudgeRuns.Select(j => j.Status).ToList()
            })
            .ToListAsync(ct);

        var modelOrder = batch.ModelIds.Concat(its.SelectMany(i => i.Results).Select(r => r.ModelId)).Distinct().ToList();
        var models = await db.Models.Include(m => m.Provider).Where(m => modelOrder.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var aliasOf = modelOrder.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => Alias(x.i));

        // Wins: best average within each iteration that has ≥2 rated answers.
        var wins = its.Select(i => i.Results.Where(r => r.Ratings.Count > 0)
                .Select(r => (r.ModelId, Avg: r.Ratings.Average(x => x.Stars))).ToList())
            .Where(rs => rs.Count >= 2)
            .SelectMany(rs => rs.Where(r => r.Avg == rs.Max(x => x.Avg)).Select(r => r.ModelId))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        var stats = modelOrder.Select((modelId, index) =>
        {
            var rs = its.SelectMany(i => i.Results).Where(r => r.ModelId == modelId).ToList();
            var all = rs.SelectMany(r => r.Ratings).ToList();
            var human = all.Where(x => !x.Ai).ToList();
            var ai = all.Where(x => x.Ai).ToList();
            var perAnswer = rs.Where(r => r.Ratings.Count > 0).Select(r => r.Ratings.Average(x => x.Stars)).ToList();
            var ok = rs.Where(r => r.Status == ResultStatus.Completed).ToList();
            double? stdDev = perAnswer.Count >= 2
                ? Math.Sqrt(perAnswer.Sum(v => Math.Pow(v - perAnswer.Average(), 2)) / perAnswer.Count)
                : null;
            return new BatchModelStatDto(
                Alias(index),
                reveal && models.TryGetValue(modelId, out var m) ? ToRef(m) : null,
                rs.Count,
                rs.Count(r => r.Status == ResultStatus.Failed),
                all.Count,
                all.Count > 0 ? all.Average(x => x.Stars) : null,
                human.Count > 0 ? human.Average(x => x.Stars) : null,
                ai.Count > 0 ? ai.Average(x => x.Stars) : null,
                stdDev,
                wins.GetValueOrDefault(modelId),
                ok.Count(r => r.LatencyMs != null) > 0 ? ok.Where(r => r.LatencyMs != null).Average(r => (double)r.LatencyMs!.Value) : null,
                ok.Count(r => r.OutputTokens != null) > 0 ? ok.Where(r => r.OutputTokens != null).Average(r => (double)r.OutputTokens!.Value) : null);
        }).ToList();

        var rows = its.GroupBy(i => (i.TestCaseId, i.Title))
            .OrderBy(g => g.Key.Title)
            .Select(g =>
            {
                var rowRatings = g.SelectMany(i => i.Results).SelectMany(r => r.Ratings).ToList();
                return new BatchRowDto(g.Key.TestCaseId, g.Key.Title,
                g.OrderBy(i => i.Rep).Select(i =>
                {
                    var ratings = i.Results.SelectMany(r => r.Ratings).ToList();
                    var completed = i.Results.Where(r => r.Status == ResultStatus.Completed).ToList();
                    return new BatchCellDto(i.Id, i.Rep, i.Number, i.Results.Count,
                        i.Results.Count(r => r.Status is ResultStatus.Completed or ResultStatus.Failed),
                        i.Results.Count(r => r.Status == ResultStatus.Failed),
                        ratings.Count,
                        ratings.Count > 0 ? ratings.Average(x => x.Stars) : null,
                        i.Judges.Count,
                        i.Judges.Count(s => s is ResultStatus.Completed or ResultStatus.Failed),
                        userId is { } uid && completed.Count > 0 && completed.All(r => r.Ratings.Any(x => x.UserId == uid)),
                        i.Results.OrderBy(r => modelOrder.IndexOf(r.ModelId)).Select(r => new BatchCellModelDto(
                            aliasOf[r.ModelId], r.Status,
                            r.Ratings.Count > 0 ? r.Ratings.Average(x => x.Stars) : null,
                            r.Ratings.Count)).ToList());
                }).ToList(),
                rowRatings.Count > 0 ? rowRatings.Average(x => x.Stars) : null,
                modelOrder.Select(mid =>
                {
                    var rs = g.SelectMany(i => i.Results).Where(r => r.ModelId == mid).SelectMany(r => r.Ratings).ToList();
                    return new BatchAliasAvgDto(aliasOf[mid], rs.Count > 0 ? rs.Average(x => x.Stars) : null);
                }).ToList());
            })
            .ToList();

        return new BatchDto(summary, reveal, batch.JudgeModelIds, stats, rows);
    }

    /// <summary>Judges every finished iteration of the series; unfinished ones get judged automatically when they finish.</summary>
    public async Task<BatchJudgeResult> JudgeBatchAsync(Guid batchId, JudgeBatchRequest req, CancellationToken ct = default)
    {
        List<Guid> runIds;
        int ready, deferred;
        List<string> judgeNames;
        await JudgeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct) ?? throw EvalException.NotFound("series");
            var judges = await ResolveJudgesAsync(db, req.JudgeModelIds, ct);
            judgeNames = judges.Select(j => j.DisplayName).ToList();

            var states = await db.Iterations.Where(i => i.BatchId == batchId)
                .Select(i => new
                {
                    i.Id,
                    Busy = i.Results.Any(r => r.Status == ResultStatus.Pending || r.Status == ResultStatus.Running),
                    AnyOk = i.Results.Any(r => r.Status == ResultStatus.Completed)
                }).ToListAsync(ct);
            var readyIds = states.Where(s => !s.Busy && s.AnyOk).Select(s => s.Id).ToList();
            ready = readyIds.Count;
            deferred = states.Count(s => s.Busy);

            if (deferred > 0)
            {
                batch.AutoJudge = true;
                batch.JudgeModelIds = judges.Select(j => j.Id).ToList();
            }
            runIds = await CreateJudgeRunsAsync(db, readyIds, judges, req.OnlyUnjudged, req.UserId, ct);
        }
        finally
        {
            JudgeLock.Release();
        }

        foreach (var id in runIds) judgeQueue.Enqueue(id);
        events.Raise();
        return new BatchJudgeResult(runIds.Count, ready, deferred, judgeNames);
    }

    /// <summary>Called by the runner after each answer lands: if the iteration is done and its series auto-judges, queue the judges.</summary>
    public async Task AutoJudgeIfReadyAsync(Guid iterationId, CancellationToken ct = default)
    {
        List<Guid> runIds;
        await JudgeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var it = await db.Iterations.Include(i => i.Batch).Include(i => i.Results).FirstOrDefaultAsync(i => i.Id == iterationId, ct);
            if (it?.Batch is not { AutoJudge: true } batch) return;
            if (it.Results.Any(r => r.Status is ResultStatus.Pending or ResultStatus.Running)) return;
            if (it.Results.All(r => r.Status != ResultStatus.Completed)) return;

            List<LlmModel> judges;
            try { judges = await ResolveJudgesAsync(db, batch.JudgeModelIds, ct); }
            catch (EvalException) { return; } // judges were disabled meanwhile – nothing to do
            runIds = await CreateJudgeRunsAsync(db, [iterationId], judges, skipAlreadyJudged: true, batch.CreatedById, ct);
        }
        finally
        {
            JudgeLock.Release();
        }

        foreach (var id in runIds) judgeQueue.Enqueue(id);
        if (runIds.Count > 0) events.RaiseIteration(iterationId);
    }

    public async Task SetBatchAutoJudgeAsync(Guid batchId, bool autoJudge, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batch = await db.Batches.FindAsync([batchId], ct) ?? throw EvalException.NotFound("series");
        batch.AutoJudge = autoJudge;
        await db.SaveChangesAsync(ct);
        events.Raise();
    }

    public async Task DeleteBatchAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Batches.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("series");
        events.Raise();
    }

    /// <summary>Next iteration in series order (after <paramref name="afterIterationId"/>, wrapping) the user hasn't fully rated.</summary>
    public async Task<Guid?> NextUnratedInBatchAsync(Guid batchId, Guid userId, Guid? afterIterationId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ordered = await OrderedBatchIterations(db, batchId)
            .Select(i => new
            {
                i.Id,
                Unrated = i.Results.Any(r => r.Status == ResultStatus.Completed && !r.Ratings.Any(x => x.UserId == userId))
            }).ToListAsync(ct);
        var start = afterIterationId is { } after ? ordered.FindIndex(x => x.Id == after) + 1 : 0;
        return ordered.Skip(start).Concat(ordered.Take(start)).FirstOrDefault(x => x.Unrated && x.Id != afterIterationId)?.Id;
    }

    private static IQueryable<Iteration> OrderedBatchIterations(AppDbContext db, Guid batchId) =>
        db.Iterations.Where(i => i.BatchId == batchId).OrderBy(i => i.TestCase.Title).ThenBy(i => i.Repetition).ThenBy(i => i.Number);

    private static async Task<BatchNavDto?> BatchNavAsync(AppDbContext db, Guid batchId, Guid iterationId, int? repetition, CancellationToken ct)
    {
        var batch = await db.Batches.AsNoTracking().Where(b => b.Id == batchId).Select(b => new { b.Name, b.Repetitions }).FirstOrDefaultAsync(ct);
        if (batch is null) return null;
        var ids = await OrderedBatchIterations(db, batchId).Select(i => i.Id).ToListAsync(ct);
        var pos = ids.IndexOf(iterationId);
        return new BatchNavDto(batchId, batch.Name, repetition, batch.Repetitions, pos + 1, ids.Count,
            pos > 0 ? ids[pos - 1] : null,
            pos >= 0 && pos < ids.Count - 1 ? ids[pos + 1] : null);
    }
}
