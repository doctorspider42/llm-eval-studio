using LlmEval.Web.Data;
using LlmEval.Web.Llm;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

/// <summary>
/// Domain error with a translation key (see Resources/i18n/*/errors.json). Message is the English text, which is what
/// the REST API returns; the UI shows it in the user's language via <see cref="Localizer.Error"/>.
/// </summary>
public class EvalException(int statusCode, string key, params object?[] args) : Exception(I18n.Format(I18n.English, key, args))
{
    public int StatusCode { get; } = statusCode;
    public string Key { get; } = key;
    public object?[] Args { get; } = args;

    /// <param name="entity">user, provider, model, testCase, iteration, result, series</param>
    public static EvalException NotFound(string entity) => new(404, $"errors.notFound.{entity}");
    public static EvalException Invalid(string key, params object?[] args) => new(400, key, args);
    public static EvalException Conflict(string key, params object?[] args) => new(409, key, args);
}

public static class PromptComposer
{
    public const string DataPlaceholder = "{{data}}";

    public static string Compose(string prompt, string? data)
    {
        if (prompt.Contains(DataPlaceholder)) return prompt.Replace(DataPlaceholder, data ?? "");
        return string.IsNullOrWhiteSpace(data) ? prompt : $"{prompt}\n\n<data>\n{data}\n</data>";
    }
}

/// <summary>All business logic; shared by the Blazor UI and the REST API so both behave identically.</summary>
public partial class EvalService(IDbContextFactory<AppDbContext> dbFactory, ResultQueue queue, JudgeQueue judgeQueue, EvalEvents events, LlmClientFactory clients)
{
    // ───────────────────────── users ─────────────────────────

    public async Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.OrderBy(u => u.Name).Select(u => ToDto(u)).ToListAsync(ct);
    }

    public async Task<UserDto> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var u = await db.Users.FindAsync([id], ct) ?? throw EvalException.NotFound("user");
        return ToDto(u);
    }

    public async Task<UserDto> CreateUserAsync(UpsertUserRequest req, CancellationToken ct = default)
    {
        var name = Required(req.Name, "errors.required.name");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Name == name, ct)) throw EvalException.Conflict("errors.userExists", name);
        var user = new User { Name = name, Email = Blank(req.Email), IsBot = req.IsBot, AvatarHue = HueFor(name) };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(user);
    }

    public async Task<UserDto> UpdateUserAsync(Guid id, UpsertUserRequest req, CancellationToken ct = default)
    {
        var name = Required(req.Name, "errors.required.name");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FindAsync([id], ct) ?? throw EvalException.NotFound("user");
        if (await db.Users.AnyAsync(u => u.Name == name && u.Id != id, ct)) throw EvalException.Conflict("errors.userExists", name);
        user.Name = name;
        user.Email = Blank(req.Email);
        user.IsBot = req.IsBot || user.JudgeModelId is not null;
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(user);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("user");
        events.Raise();
    }

    // ───────────────────────── providers & models ─────────────────────────

    public async Task<List<ProviderDto>> GetProvidersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.Providers.Include(p => p.Models).OrderBy(p => p.Name).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<ProviderDto> GetProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Providers.Include(x => x.Models).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw EvalException.NotFound("provider");
        return ToDto(p);
    }

    public async Task<ProviderDto> CreateProviderAsync(UpsertProviderRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = new Provider { Name = Required(req.Name, "errors.required.name") };
        Apply(p, req);
        db.Providers.Add(p);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(p);
    }

    public async Task<ProviderDto> UpdateProviderAsync(Guid id, UpsertProviderRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Providers.Include(x => x.Models).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw EvalException.NotFound("provider");
        p.Name = Required(req.Name, "errors.required.name");
        Apply(p, req);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(p);
    }

    private static void Apply(Provider p, UpsertProviderRequest req)
    {
        p.Type = req.Type;
        p.BaseUrl = Blank(req.BaseUrl);
        if (req.ApiKey is not null) p.ApiKey = Blank(req.ApiKey);
        p.CliPath = Blank(req.CliPath);
        p.ExtraArgs = Blank(req.ExtraArgs);
        p.TimeoutSeconds = req.TimeoutSeconds is > 0 ? req.TimeoutSeconds.Value : 300;
        p.Enabled = req.Enabled;
    }

    public async Task DeleteProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Results.AnyAsync(r => r.Model.ProviderId == id, ct) || await db.JudgeRuns.AnyAsync(r => r.Model.ProviderId == id, ct))
            throw EvalException.Conflict("errors.providerHasHistory");
        var n = await db.Providers.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("provider");
        events.Raise();
    }

    public async Task<ProviderDto> PatchProviderAsync(Guid id, PatchProviderRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Providers.Include(x => x.Models).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw EvalException.NotFound("provider");
        if (req.Enabled is { } enabled) p.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(p);
    }

    public async Task<ConnectionTestResult> TestProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Providers.FindAsync([id], ct) ?? throw EvalException.NotFound("provider");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var models = await clients.For(p.Type).ListModelsAsync(p, cts.Token);
            var cli = p.Type is ProviderType.ClaudeCli or ProviderType.CodexCli;
            return new ConnectionTestResult(true, cli ? "CLI – the model list is only a suggestion (the connection is checked when an iteration runs)" : $"OK – {models.Count} models", models);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message, []);
        }
    }

    public async Task<List<ModelDto>> GetModelsAsync(bool onlyEnabled = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Models.Include(m => m.Provider).AsQueryable();
        if (onlyEnabled) q = q.Where(m => m.Enabled && m.Provider.Enabled);
        var list = await q.OrderBy(m => m.Provider.Name).ThenBy(m => m.DisplayName).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<ModelDto> CreateModelAsync(UpsertModelRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var provider = await db.Providers.FindAsync([req.ProviderId], ct) ?? throw EvalException.NotFound("provider");
        var modelId = Required(req.ModelId, "errors.required.modelId");
        if (await db.Models.AnyAsync(m => m.ProviderId == req.ProviderId && m.ModelId == modelId, ct))
            throw EvalException.Conflict("errors.modelExists", modelId);
        var m = new LlmModel { ProviderId = provider.Id, Provider = provider, ModelId = modelId, DisplayName = modelId };
        Apply(m, req);
        db.Models.Add(m);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(m);
    }

    public async Task<ModelDto> UpdateModelAsync(Guid id, UpsertModelRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var m = await db.Models.Include(x => x.Provider).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw EvalException.NotFound("model");
        m.ModelId = Required(req.ModelId, "errors.required.modelId");
        Apply(m, req);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(m);
    }

    private static void Apply(LlmModel m, UpsertModelRequest req)
    {
        m.DisplayName = Blank(req.DisplayName) ?? m.ModelId;
        m.Temperature = req.Temperature;
        m.MaxTokens = req.MaxTokens;
        m.Enabled = req.Enabled;
        m.IsJudge = req.IsJudge;
    }

    public async Task<ModelDto> PatchModelAsync(Guid id, PatchModelRequest req, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var m = await db.Models.Include(x => x.Provider).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw EvalException.NotFound("model");
        if (req.Enabled is { } enabled) m.Enabled = enabled;
        if (req.IsJudge is { } judge) m.IsJudge = judge;
        await db.SaveChangesAsync(ct);
        events.Raise();
        return ToDto(m);
    }

    public async Task DeleteModelAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Results.AnyAsync(r => r.ModelId == id, ct) || await db.JudgeRuns.AnyAsync(r => r.ModelId == id, ct))
            throw EvalException.Conflict("errors.modelHasHistory");
        var n = await db.Models.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("model");
        events.Raise();
    }

    // ───────────────────────── test cases ─────────────────────────

    public async Task<List<TestCaseSummaryDto>> GetTestCasesAsync(string? search = null, string? tag = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.TestCases.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(t => EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Prompt, pattern));
        }
        if (!string.IsNullOrWhiteSpace(tag)) q = q.Where(t => t.Tags.Contains(tag));

        return await q.OrderByDescending(t => t.UpdatedAt)
            .Select(t => new TestCaseSummaryDto(
                t.Id, t.Title,
                t.Prompt.Length > 220 ? t.Prompt.Substring(0, 220) + "…" : t.Prompt,
                t.Tags,
                t.Iterations.Count,
                t.Iterations.Max(i => (DateTimeOffset?)i.CreatedAt),
                t.Iterations.SelectMany(i => i.Results).SelectMany(r => r.Ratings).Average(r => (double?)r.Stars),
                t.CreatedBy != null ? t.CreatedBy.Name : null,
                t.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tags = await db.TestCases.Select(t => t.Tags).ToListAsync(ct);
        return tags.SelectMany(t => t).Distinct().Order().ToList();
    }

    public async Task<TestCaseDto> GetTestCaseAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.TestCases.Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw EvalException.NotFound("testCase");
        var iterations = await IterationSummaries(db.Iterations.Where(i => i.TestCaseId == id).OrderByDescending(i => i.Number))
            .ToListAsync(ct);
        return new TestCaseDto(t.Id, t.Title, t.SystemPrompt, t.Prompt, t.Data, t.Tags, t.CreatedById, t.CreatedBy?.Name,
            t.CreatedAt, t.UpdatedAt, iterations, t.ExpectedAnswer);
    }

    public async Task<TestCaseDto> CreateTestCaseAsync(UpsertTestCaseRequest req, CancellationToken ct = default)
    {
        Guid id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var t = new TestCase { Title = Required(req.Title, "errors.required.title"), Prompt = Required(req.Prompt, "errors.required.prompt") };
            Apply(t, req);
            t.CreatedById = await ExistingUserId(db, req.UserId, ct);
            db.TestCases.Add(t);
            await db.SaveChangesAsync(ct);
            id = t.Id;
        }
        events.Raise();
        return await GetTestCaseAsync(id, ct);
    }

    public async Task<TestCaseDto> UpdateTestCaseAsync(Guid id, UpsertTestCaseRequest req, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var t = await db.TestCases.FindAsync([id], ct) ?? throw EvalException.NotFound("testCase");
            t.Title = Required(req.Title, "errors.required.title");
            t.Prompt = Required(req.Prompt, "errors.required.prompt");
            Apply(t, req);
            t.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        events.Raise();
        return await GetTestCaseAsync(id, ct);
    }

    private static void Apply(TestCase t, UpsertTestCaseRequest req)
    {
        t.Data = string.IsNullOrWhiteSpace(req.Data) ? null : req.Data;
        t.ExpectedAnswer = string.IsNullOrWhiteSpace(req.ExpectedAnswer) ? null : req.ExpectedAnswer;
        t.SystemPrompt = Blank(req.SystemPrompt);
        t.Tags = (req.Tags ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
    }

    public async Task DeleteTestCaseAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.TestCases.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("testCase");
        events.Raise();
    }

    // ───────────────────────── iterations ─────────────────────────

    public async Task<IterationDto> RunIterationAsync(Guid testCaseId, RunIterationRequest req, CancellationToken ct = default)
    {
        var modelIds = (req.ModelIds ?? []).Distinct().ToList();
        if (modelIds.Count == 0) throw EvalException.Invalid("errors.selectModel");

        Guid iterationId;
        List<Guid> resultIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var t = await db.TestCases.FindAsync([testCaseId], ct) ?? throw EvalException.NotFound("testCase");
            var models = await db.Models.Include(m => m.Provider).Where(m => modelIds.Contains(m.Id)).ToListAsync(ct);
            var missing = modelIds.Except(models.Select(m => m.Id)).ToList();
            if (missing.Count > 0) throw EvalException.Invalid("errors.unknownModels", string.Join(", ", missing));
            var disabled = models.Where(m => !m.Enabled || !m.Provider.Enabled).Select(m => m.DisplayName).ToList();
            if (disabled.Count > 0) throw EvalException.Invalid("errors.disabledModels", string.Join(", ", disabled));

            var number = (await db.Iterations.Where(i => i.TestCaseId == testCaseId).MaxAsync(i => (int?)i.Number, ct) ?? 0) + 1;
            var iteration = new Iteration
            {
                TestCaseId = t.Id,
                Number = number,
                Note = Blank(req.Note),
                SystemPromptSnapshot = t.SystemPrompt,
                UserMessageSnapshot = PromptComposer.Compose(t.Prompt, t.Data),
                ExpectedAnswerSnapshot = t.ExpectedAnswer,
                CreatedById = await ExistingUserId(db, req.UserId, ct),
            };

            // Shuffle so "Model 1" carries no information about selection order.
            var shuffled = models.OrderBy(_ => Random.Shared.Next()).ToList();
            for (var i = 0; i < shuffled.Count; i++)
                iteration.Results.Add(new IterationResult { ModelId = shuffled[i].Id, Slot = i + 1, Status = ResultStatus.Pending });

            db.Iterations.Add(iteration);
            await db.SaveChangesAsync(ct);
            iterationId = iteration.Id;
            resultIds = iteration.Results.Select(r => r.Id).ToList();
        }

        foreach (var id in resultIds) queue.Enqueue(id);
        events.RaiseIteration(iterationId);
        return await GetIterationAsync(iterationId, reveal: false, ct);
    }

    public async Task<List<IterationSummaryDto>> GetIterationsAsync(Guid? testCaseId = null, int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Iterations.AsQueryable();
        if (testCaseId is { } tc) q = q.Where(i => i.TestCaseId == tc);
        return await IterationSummaries(q.OrderByDescending(i => i.CreatedAt).Take(Math.Clamp(take, 1, 500))).ToListAsync(ct);
    }

    /// <summary>Iterations in which the given user hasn't rated every completed result yet.</summary>
    public async Task<List<IterationSummaryDto>> GetUnratedIterationsAsync(Guid userId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Iterations.Where(i => i.Results.Any(r => r.Status == ResultStatus.Completed && !r.Ratings.Any(x => x.UserId == userId)));
        return await IterationSummaries(q.OrderByDescending(i => i.CreatedAt).Take(Math.Clamp(take, 1, 500))).ToListAsync(ct);
    }

    public async Task<IterationDto> GetIterationAsync(Guid id, bool reveal, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var it = await db.Iterations.AsNoTracking().AsSplitQuery()
                     .Include(i => i.TestCase)
                     .Include(i => i.CreatedBy)
                     .Include(i => i.Results).ThenInclude(r => r.Model).ThenInclude(m => m.Provider)
                     .Include(i => i.Results).ThenInclude(r => r.Ratings).ThenInclude(r => r.User)
                     .Include(i => i.JudgeRuns).ThenInclude(j => j.Model).ThenInclude(m => m.Provider)
                     .FirstOrDefaultAsync(i => i.Id == id, ct)
                 ?? throw EvalException.NotFound("iteration");

        var results = it.Results.OrderBy(r => r.Slot).Select(r => new ResultDto(
            r.Id, r.Slot, r.BlindLabel,
            reveal ? ToRef(r.Model) : null,
            r.Status, r.Output, r.Error, r.InputTokens, r.OutputTokens, r.LatencyMs, r.StartedAt, r.CompletedAt,
            r.Ratings.Count > 0 ? r.Ratings.Average(x => x.Stars) : null,
            r.Ratings.OrderBy(x => x.CreatedAt).Select(ToDto).ToList())).ToList();

        return new IterationDto(it.Id, it.TestCaseId, it.TestCase.Title, it.Number, it.Note, it.SystemPromptSnapshot,
            it.UserMessageSnapshot, it.CreatedBy?.Name, it.CreatedAt, reveal,
            results.All(r => r.Status is ResultStatus.Completed or ResultStatus.Failed), results,
            it.JudgeRuns.OrderBy(j => j.CreatedAt).Select(j => new JudgeRunDto(j.Id, j.ModelId, j.Model.DisplayName,
                j.Model.Provider.Type, j.Status, j.Error, j.RawOutput, j.LatencyMs, j.CreatedAt, j.CompletedAt)).ToList(),
            it.BatchId is { } batchId ? await BatchNavAsync(db, batchId, it.Id, it.Repetition, ct) : null,
            it.ExpectedAnswerSnapshot);
    }

    public async Task DeleteIterationAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Iterations.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw EvalException.NotFound("iteration");
        events.Raise();
    }

    public async Task RetryResultAsync(Guid resultId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.Results.FindAsync([resultId], ct) ?? throw EvalException.NotFound("result");
        if (r.Status is ResultStatus.Pending or ResultStatus.Running) return;
        r.Status = ResultStatus.Pending;
        r.Output = null;
        r.Error = null;
        r.LatencyMs = null;
        r.InputTokens = r.OutputTokens = null;
        r.StartedAt = r.CompletedAt = null;
        await db.Ratings.Where(x => x.ResultId == resultId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(resultId);
        events.RaiseIteration(r.IterationId);
    }

    // ───────────────────────── AI judge ─────────────────────────

    /// <summary>Serialises judge-run creation so concurrent triggers (UI, API, auto-judge) can't double-book a judge.</summary>
    private static readonly SemaphoreSlim JudgeLock = new(1, 1);

    /// <summary>Queues every requested judge model to rate all completed answers of the iteration (blind).</summary>
    public async Task<List<JudgeRunDto>> JudgeIterationAsync(Guid iterationId, JudgeIterationRequest req, CancellationToken ct = default)
    {
        List<Guid> runIds;
        await JudgeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var it = await db.Iterations.Include(i => i.Results).FirstOrDefaultAsync(i => i.Id == iterationId, ct)
                     ?? throw EvalException.NotFound("iteration");
            if (it.Results.Any(r => r.Status is ResultStatus.Pending or ResultStatus.Running))
                throw EvalException.Invalid("errors.waitForModels");
            if (it.Results.All(r => r.Status != ResultStatus.Completed))
                throw EvalException.Invalid("errors.noSuccessfulAnswers");

            var judges = await ResolveJudgesAsync(db, req.JudgeModelIds, ct);
            runIds = await CreateJudgeRunsAsync(db, [iterationId], judges, skipAlreadyJudged: false, req.UserId, ct);
        }
        finally
        {
            JudgeLock.Release();
        }

        foreach (var id in runIds) judgeQueue.Enqueue(id);
        events.RaiseIteration(iterationId);
        var dto = await GetIterationAsync(iterationId, reveal: false, ct);
        return dto.JudgeRuns.Where(j => runIds.Contains(j.Id)).ToList();
    }

    private static async Task<List<LlmModel>> ResolveJudgesAsync(AppDbContext db, List<Guid>? requestedIds, CancellationToken ct)
    {
        var q = db.Models.Include(m => m.Provider).Where(m => m.Enabled && m.Provider.Enabled);
        var requested = (requestedIds ?? []).Distinct().ToList();
        var judges = requested.Count > 0
            ? await q.Where(m => requested.Contains(m.Id)).ToListAsync(ct)
            : await q.Where(m => m.IsJudge).ToListAsync(ct);
        if (judges.Count == 0)
            throw EvalException.Invalid(requested.Count > 0
                ? "errors.judgesNotFound"
                : "errors.noJudges");
        return judges;
    }

    /// <summary>
    /// Adds one Pending JudgeRun per (iteration, judge). Never duplicates a run that is still queued/running;
    /// with <paramref name="skipAlreadyJudged"/> also skips pairs that already finished successfully.
    /// Caller must hold <see cref="JudgeLock"/>.
    /// </summary>
    private static async Task<List<Guid>> CreateJudgeRunsAsync(AppDbContext db, List<Guid> iterationIds, List<LlmModel> judges,
        bool skipAlreadyJudged, Guid? requestedById, CancellationToken ct)
    {
        var existing = await db.JudgeRuns
            .Where(j => iterationIds.Contains(j.IterationId))
            .Select(j => new { j.IterationId, j.ModelId, j.Status })
            .ToListAsync(ct);
        var blocked = existing
            .Where(j => j.Status is ResultStatus.Pending or ResultStatus.Running || (skipAlreadyJudged && j.Status == ResultStatus.Completed))
            .Select(j => (j.IterationId, j.ModelId))
            .ToHashSet();

        var runs = (from itId in iterationIds
                    from m in judges
                    where !blocked.Contains((itId, m.Id))
                    select new JudgeRun { IterationId = itId, ModelId = m.Id, Status = ResultStatus.Pending, RequestedById = requestedById })
            .ToList();
        db.JudgeRuns.AddRange(runs);
        await db.SaveChangesAsync(ct);
        return runs.Select(r => r.Id).ToList();
    }

    public async Task<List<ModelDto>> GetJudgeModelsAsync(CancellationToken ct = default) =>
        (await GetModelsAsync(onlyEnabled: true, ct)).Where(m => m.IsJudge).ToList();

    // ───────────────────────── ratings ─────────────────────────

    public async Task<RatingDto> RateAsync(Guid resultId, RateResultRequest req, CancellationToken ct = default)
    {
        if (req.Stars is < 1 or > 5) throw EvalException.Invalid("errors.starsRange");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.Results.FindAsync([resultId], ct) ?? throw EvalException.NotFound("result");
        if (result.Status != ResultStatus.Completed) throw EvalException.Invalid("errors.rateOnlyCompleted");
        var user = await db.Users.FindAsync([req.UserId], ct) ?? throw EvalException.NotFound("user");

        var rating = await db.Ratings.FirstOrDefaultAsync(r => r.ResultId == resultId && r.UserId == req.UserId, ct);
        if (rating is null)
        {
            rating = new Rating { ResultId = resultId, UserId = user.Id };
            db.Ratings.Add(rating);
        }
        rating.Stars = req.Stars;
        rating.Comment = Blank(req.Comment);
        rating.UpdatedAt = DateTimeOffset.UtcNow;
        rating.User = user;
        await db.SaveChangesAsync(ct);
        events.RaiseIteration(result.IterationId);
        return ToDto(rating);
    }

    public async Task DeleteRatingAsync(Guid resultId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.Results.FindAsync([resultId], ct) ?? throw EvalException.NotFound("result");
        await db.Ratings.Where(r => r.ResultId == resultId && r.UserId == userId).ExecuteDeleteAsync(ct);
        events.RaiseIteration(result.IterationId);
    }

    // ───────────────────────── stats ─────────────────────────

    public async Task<List<LeaderboardEntry>> GetLeaderboardAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Results.AsNoTracking()
            .Select(r => new
            {
                r.IterationId,
                r.ModelId,
                r.Status,
                r.LatencyMs,
                Avg = r.Ratings.Average(x => (double?)x.Stars),
                Count = r.Ratings.Count,
                HumanSum = r.Ratings.Where(x => x.User.JudgeModelId == null).Sum(x => x.Stars),
                HumanCount = r.Ratings.Count(x => x.User.JudgeModelId == null),
                AiSum = r.Ratings.Where(x => x.User.JudgeModelId != null).Sum(x => x.Stars),
                AiCount = r.Ratings.Count(x => x.User.JudgeModelId != null)
            }).ToListAsync(ct);
        var models = await db.Models.Include(m => m.Provider).ToDictionaryAsync(m => m.Id, ct);

        // A "win" = best average score within an iteration with ≥2 rated results (ties count for everyone on top).
        var wins = rows.Where(r => r.Avg != null).GroupBy(r => r.IterationId).Where(g => g.Count() >= 2)
            .SelectMany(g =>
            {
                var best = g.Max(x => x.Avg);
                return g.Where(x => x.Avg == best).Select(x => x.ModelId);
            })
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        return rows.GroupBy(r => r.ModelId)
            .Where(g => models.ContainsKey(g.Key))
            .Select(g =>
            {
                var rated = g.Where(x => x.Count > 0).ToList();
                var ok = g.Where(x => x.Status == ResultStatus.Completed && x.LatencyMs != null).ToList();
                return new LeaderboardEntry(
                    ToRef(models[g.Key]),
                    g.Count(),
                    g.Count(x => x.Status == ResultStatus.Failed),
                    rated.Sum(x => x.Count),
                    rated.Count > 0 ? rated.Sum(x => x.Avg!.Value * x.Count) / rated.Sum(x => x.Count) : null,
                    ok.Count > 0 ? ok.Average(x => (double)x.LatencyMs!.Value) : null,
                    wins.GetValueOrDefault(g.Key),
                    g.Sum(x => x.HumanCount) > 0 ? (double)g.Sum(x => x.HumanSum) / g.Sum(x => x.HumanCount) : null,
                    g.Sum(x => x.AiCount) > 0 ? (double)g.Sum(x => x.AiSum) / g.Sum(x => x.AiCount) : null);
            })
            .OrderByDescending(e => e.AvgStars ?? -1).ThenByDescending(e => e.Wins).ThenBy(e => e.Model.DisplayName)
            .ToList();
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var leaderboard = await GetLeaderboardAsync(ct);
        var recent = await GetIterationsAsync(take: 8, ct: ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new DashboardDto(
            await db.Users.CountAsync(ct),
            await db.TestCases.CountAsync(ct),
            await db.Iterations.CountAsync(ct),
            await db.Results.CountAsync(ct),
            await db.Ratings.CountAsync(ct),
            leaderboard, recent);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static IQueryable<IterationSummaryDto> IterationSummaries(IQueryable<Iteration> q) =>
        q.Select(i => new IterationSummaryDto(
            i.Id, i.TestCaseId, i.TestCase.Title, i.Number, i.Note,
            i.CreatedBy != null ? i.CreatedBy.Name : null,
            i.CreatedAt,
            i.Results.Count,
            i.Results.Count(r => r.Status == ResultStatus.Completed),
            i.Results.Count(r => r.Status == ResultStatus.Failed),
            i.Results.SelectMany(r => r.Ratings).Count(),
            i.Results.SelectMany(r => r.Ratings).Average(r => (double?)r.Stars),
            i.BatchId,
            i.Batch != null ? i.Batch.Name : null,
            i.Repetition));

    private static async Task<Guid?> ExistingUserId(AppDbContext db, Guid? id, CancellationToken ct) =>
        id is { } uid && await db.Users.AnyAsync(u => u.Id == uid, ct) ? uid : null;

    /// <param name="key">Translation key of the whole message, e.g. "errors.required.title".</param>
    private static string Required(string? value, string key) =>
        string.IsNullOrWhiteSpace(value) ? throw EvalException.Invalid(key) : value.Trim();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static int HueFor(string name) => (int)((uint)name.Aggregate(17, (h, c) => h * 31 + c) % 360);

    private static UserDto ToDto(User u) => new(u.Id, u.Name, u.Email, u.AvatarHue, u.IsBot, u.CreatedAt, u.JudgeModelId);

    private static ModelRef ToRef(LlmModel m) => new(m.Id, m.DisplayName, m.ModelId, m.Provider.Name, m.Provider.Type);

    private static ModelDto ToDto(LlmModel m) => new(m.Id, m.ProviderId, m.Provider.Name, m.Provider.Type, m.ModelId,
        m.DisplayName, m.Temperature, m.MaxTokens, m.Enabled, m.Provider.Enabled, m.IsJudge);

    private static ProviderDto ToDto(Provider p) => new(p.Id, p.Name, p.Type, p.BaseUrl, !string.IsNullOrEmpty(p.ApiKey),
        p.CliPath, p.ExtraArgs, p.TimeoutSeconds, p.Enabled,
        p.Models.OrderBy(m => m.DisplayName).Select(ToDto).ToList());

    public static RatingDto ToDto(Rating r) => new(r.Id, r.ResultId, r.UserId, r.User.Name, r.User.AvatarHue, r.User.IsBot,
        r.User.JudgeModelId is not null, r.Stars, r.Comment, r.CreatedAt, r.UpdatedAt);
}
