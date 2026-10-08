using LlmEval.Web.Services;

namespace LlmEval.Web.Api;

public static class ApiEndpoints
{
    public static void MapEvalApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<EvalExceptionFilter>();

        // ── users ──
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/", (EvalService s, CancellationToken ct) => s.GetUsersAsync(ct))
            .WithSummary("List users");
        users.MapGet("/{id:guid}", (Guid id, EvalService s, CancellationToken ct) => s.GetUserAsync(id, ct))
            .WithSummary("Get user");
        users.MapPost("/", async (UpsertUserRequest req, EvalService s, CancellationToken ct) =>
            {
                var u = await s.CreateUserAsync(req, ct);
                return Results.Created($"/api/users/{u.Id}", u);
            })
            .WithSummary("Create user (no password – internal app). Set isBot=true for an LLM judge.");
        users.MapPut("/{id:guid}", (Guid id, UpsertUserRequest req, EvalService s, CancellationToken ct) => s.UpdateUserAsync(id, req, ct))
            .WithSummary("Update user");
        users.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteUserAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete user (also deletes their ratings)");
        users.MapGet("/{id:guid}/unrated-iterations", (Guid id, EvalService s, CancellationToken ct, int take = 50) =>
                s.GetUnratedIterationsAsync(id, take, ct))
            .WithSummary("Iterations with completed answers this user has not rated yet – the judge's to-do list");

        // ── providers & models ──
        var providers = api.MapGroup("/providers").WithTags("Providers");
        providers.MapGet("/", (EvalService s, CancellationToken ct) => s.GetProvidersAsync(ct))
            .WithSummary("List providers with their models (API keys are never returned)");
        providers.MapGet("/{id:guid}", (Guid id, EvalService s, CancellationToken ct) => s.GetProviderAsync(id, ct))
            .WithSummary("Get provider");
        providers.MapPost("/", async (UpsertProviderRequest req, EvalService s, CancellationToken ct) =>
            {
                var p = await s.CreateProviderAsync(req, ct);
                return Results.Created($"/api/providers/{p.Id}", p);
            })
            .WithSummary("Create provider. type: OpenAI | Anthropic | Ollama | ClaudeCli | CodexCli");
        providers.MapPut("/{id:guid}", (Guid id, UpsertProviderRequest req, EvalService s, CancellationToken ct) => s.UpdateProviderAsync(id, req, ct))
            .WithSummary("Update provider. apiKey: null = keep, \"\" = remove");
        providers.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteProviderAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete provider (only without result history)");
        providers.MapPatch("/{id:guid}", (Guid id, PatchProviderRequest req, EvalService s, CancellationToken ct) => s.PatchProviderAsync(id, req, ct))
            .WithSummary("Quick toggle: {\"enabled\": true|false}");
        providers.MapPost("/{id:guid}/test", (Guid id, EvalService s, CancellationToken ct) => s.TestProviderAsync(id, ct))
            .WithSummary("Test connectivity and list models the provider reports");

        var models = api.MapGroup("/models").WithTags("Models");
        models.MapGet("/", (EvalService s, CancellationToken ct, bool onlyEnabled = false) => s.GetModelsAsync(onlyEnabled, ct))
            .WithSummary("List models across providers");
        models.MapGet("/judges", (EvalService s, CancellationToken ct) => s.GetJudgeModelsAsync(ct))
            .WithSummary("Enabled models marked as AI judges");
        models.MapPost("/", async (UpsertModelRequest req, EvalService s, CancellationToken ct) =>
            {
                var m = await s.CreateModelAsync(req, ct);
                return Results.Created($"/api/models/{m.Id}", m);
            })
            .WithSummary("Add a model to a provider");
        models.MapPut("/{id:guid}", (Guid id, UpsertModelRequest req, EvalService s, CancellationToken ct) => s.UpdateModelAsync(id, req, ct))
            .WithSummary("Update model");
        models.MapPatch("/{id:guid}", (Guid id, PatchModelRequest req, EvalService s, CancellationToken ct) => s.PatchModelAsync(id, req, ct))
            .WithSummary("Quick toggles: {\"enabled\": bool?, \"isJudge\": bool?} – null fields are untouched");
        models.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteModelAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete model (only without result history)");

        // ── test cases ──
        var cases = api.MapGroup("/test-cases").WithTags("Test cases");
        cases.MapGet("/", (EvalService s, CancellationToken ct, string? search = null, string? tag = null) => s.GetTestCasesAsync(search, tag, ct))
            .WithSummary("List test cases (optional ?search= and ?tag=)");
        cases.MapGet("/tags", (EvalService s, CancellationToken ct) => s.GetTagsAsync(ct))
            .WithSummary("All distinct tags");
        cases.MapGet("/{id:guid}", (Guid id, EvalService s, CancellationToken ct) => s.GetTestCaseAsync(id, ct))
            .WithSummary("Get test case with its iteration summaries");
        cases.MapPost("/", async (UpsertTestCaseRequest req, EvalService s, CancellationToken ct) =>
            {
                var t = await s.CreateTestCaseAsync(req, ct);
                return Results.Created($"/api/test-cases/{t.Id}", t);
            })
            .WithSummary("Create test case. Prompt may contain {{data}}; otherwise data is appended in <data> tags.");
        cases.MapPut("/{id:guid}", (Guid id, UpsertTestCaseRequest req, EvalService s, CancellationToken ct) => s.UpdateTestCaseAsync(id, req, ct))
            .WithSummary("Update test case (past iterations keep their prompt snapshot)");
        cases.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteTestCaseAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete test case with all iterations");
        cases.MapPost("/{id:guid}/iterations", async (Guid id, RunIterationRequest req, EvalService s, CancellationToken ct) =>
            {
                var it = await s.RunIterationAsync(id, req, ct);
                return Results.Accepted($"/api/iterations/{it.Id}", it);
            })
            .WithSummary("Start an iteration on the selected models. Runs in the background – poll GET /api/iterations/{id}.");

        // ── import ──
        var import = api.MapGroup("/import").WithTags("Import");
        import.MapPost("/", (ImportRequest req, ImportService s, CancellationToken ct) => s.ImportAsync(req, ct))
            .WithSummary("Import test cases from JSON/JSONL text or rows. mapping fields are templates like \"{{question}}\"; " +
                         "omit mapping to guess from column names. dryRun=true returns columns + preview without saving. " +
                         "Recognised directly: title, prompt, data, expectedAnswer, systemPrompt, tags.");
        import.MapGet("/hf/presets", () => ImportService.Presets)
            .WithSummary("Ready-made permissive Hugging Face datasets with mappings");
        import.MapGet("/hf/splits", (string dataset, ImportService s, CancellationToken ct) => s.HfSplitsAsync(dataset, ct))
            .WithSummary("Configs/splits of a Hugging Face dataset, e.g. ?dataset=openai/gsm8k");
        import.MapPost("/hf", (HfImportRequest req, ImportService s, CancellationToken ct) => s.ImportFromHfAsync(req, ct))
            .WithSummary("Import rows offset..offset+length (max 2000) of a Hugging Face dataset via datasets-server. dryRun=true for preview.");

        // ── iterations ──
        var iterations = api.MapGroup("/iterations").WithTags("Iterations");
        iterations.MapGet("/", (EvalService s, CancellationToken ct, Guid? testCaseId = null, int take = 50) => s.GetIterationsAsync(testCaseId, take, ct))
            .WithSummary("Recent iterations (optional ?testCaseId=)");
        iterations.MapGet("/{id:guid}", (Guid id, EvalService s, CancellationToken ct, bool reveal = false) => s.GetIterationAsync(id, reveal, ct))
            .WithSummary("Iteration with blind results (Model 1, Model 2…). ?reveal=true includes the real model.");
        iterations.MapGet("/{id:guid}/wait", async (Guid id, EvalService s, CancellationToken ct, int timeoutSeconds = 120, bool reveal = false) =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 600));
                while (true)
                {
                    var it = await s.GetIterationAsync(id, reveal, ct);
                    if (it.IsComplete || DateTime.UtcNow >= deadline) return it;
                    await Task.Delay(1000, ct);
                }
            })
            .WithSummary("Long-poll until every result is Completed/Failed (or timeout). Check isComplete in the response.");
        iterations.MapPost("/{id:guid}/judge", async (Guid id, JudgeIterationRequest? req, EvalService s, CancellationToken ct) =>
            {
                var runs = await s.JudgeIterationAsync(id, req ?? new JudgeIterationRequest(), ct);
                return Results.Accepted($"/api/iterations/{id}", runs);
            })
            .WithSummary("AI judge: chosen judge models (default: all models marked isJudge) blindly rate every completed answer. " +
                         "Runs in the background – progress in judgeRuns of GET /api/iterations/{id}; ratings appear under the judge's bot user.");
        iterations.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteIterationAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete iteration");

        // ── series (batches) ──
        var batches = api.MapGroup("/batches").WithTags("Series");
        batches.MapGet("/", (EvalService s, CancellationToken ct, int take = 100) => s.GetBatchesAsync(take, ct))
            .WithSummary("Series with progress counters");
        batches.MapPost("/", async (CreateBatchRequest req, EvalService s, CancellationToken ct) =>
            {
                var b = await s.CreateBatchAsync(req, ct);
                return Results.Accepted($"/api/batches/{b.Summary.Id}", b);
            })
            .WithSummary("Run many test cases × models × repetitions at once. Creates one iteration per (test case, repetition). " +
                         "autoJudge=true makes judges rate each iteration as soon as it finishes.");
        batches.MapGet("/{id:guid}", (Guid id, EvalService s, CancellationToken ct, bool reveal = false, Guid? userId = null) =>
                s.GetBatchAsync(id, reveal, userId, ct))
            .WithSummary("Series overview: per-model stats (blind aliases 'A', 'B'… unless reveal=true) and the test case × repetition matrix");
        batches.MapGet("/{id:guid}/wait", async (Guid id, EvalService s, CancellationToken ct, int timeoutSeconds = 300, bool includeJudges = true) =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 1800));
                while (true)
                {
                    var b = await s.GetBatchAsync(id, false, null, ct);
                    var done = !b.Summary.IsRunning && (!includeJudges || !b.Summary.IsJudging);
                    if (done || DateTime.UtcNow >= deadline) return b;
                    await Task.Delay(2000, ct);
                }
            })
            .WithSummary("Long-poll until all answers (and, by default, judge runs) are finished");
        batches.MapPost("/{id:guid}/judge", (Guid id, JudgeBatchRequest? req, EvalService s, CancellationToken ct) =>
                s.JudgeBatchAsync(id, req ?? new JudgeBatchRequest(), ct))
            .WithSummary("AI-judge every finished iteration of the series (onlyUnjudged=true skips already judged ones). " +
                         "Unfinished iterations switch the series to autoJudge and get judged when done.");
        batches.MapPost("/{id:guid}/auto-judge", async (Guid id, bool enabled, EvalService s, CancellationToken ct) =>
            {
                await s.SetBatchAutoJudgeAsync(id, enabled, ct);
                return Results.NoContent();
            })
            .WithSummary("Turn auto-judging on/off: ?enabled=true|false");
        batches.MapGet("/{id:guid}/next-unrated", async (Guid id, Guid userId, EvalService s, CancellationToken ct, Guid? after = null) =>
                await s.NextUnratedInBatchAsync(id, userId, after, ct) is { } next ? Results.Ok(new { iterationId = next }) : Results.NoContent())
            .WithSummary("Next iteration in the series the user hasn't fully rated (204 = all done)");
        batches.MapDelete("/{id:guid}", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteBatchAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete the series with all its iterations");

        // ── results & ratings ──
        var results = api.MapGroup("/results").WithTags("Ratings");
        results.MapPut("/{id:guid}/rating", (Guid id, RateResultRequest req, EvalService s, CancellationToken ct) => s.RateAsync(id, req, ct))
            .WithSummary("Create or replace the user's rating (1–5 stars + optional comment) of one answer");
        results.MapDelete("/{id:guid}/rating/{userId:guid}", async (Guid id, Guid userId, EvalService s, CancellationToken ct) =>
            {
                await s.DeleteRatingAsync(id, userId, ct);
                return Results.NoContent();
            })
            .WithSummary("Remove the user's rating");
        results.MapPost("/{id:guid}/retry", async (Guid id, EvalService s, CancellationToken ct) =>
            {
                await s.RetryResultAsync(id, ct);
                return Results.Accepted();
            })
            .WithSummary("Re-run a single answer (drops its ratings)");

        // ── stats ──
        var stats = api.MapGroup("/stats").WithTags("Stats");
        stats.MapGet("/leaderboard", (EvalService s, CancellationToken ct) => s.GetLeaderboardAsync(ct))
            .WithSummary("Per-model averages, wins, failures, latency");
        stats.MapGet("/dashboard", (EvalService s, CancellationToken ct) => s.GetDashboardAsync(ct))
            .WithSummary("Counts + leaderboard + recent iterations");
    }
}

public class EvalExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (EvalException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }
}
