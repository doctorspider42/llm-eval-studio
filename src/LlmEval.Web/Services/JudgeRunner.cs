using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LlmEval.Web.Data;
using LlmEval.Web.Llm;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

public class JudgeQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public void Enqueue(Guid judgeRunId) => _channel.Writer.TryWrite(judgeRunId);
}

/// <summary>
/// Runs AI judges: one LLM call per (iteration, judge model) that rates every completed answer.
/// The judge sees only the task and "Model N" labels – never which model produced what.
/// </summary>
public partial class JudgeRunner(
    JudgeQueue queue,
    IDbContextFactory<AppDbContext> dbFactory,
    LlmClientFactory clients,
    EvalEvents events,
    RunControl control,
    IConfiguration config,
    ILogger<JudgeRunner> log) : BackgroundService
{
    public const string SystemPrompt =
        """
        You are an impartial expert evaluator of AI assistant answers.
        You receive a task (the exact prompt the assistants got) and several anonymous answers labelled "Model 1", "Model 2", etc.
        Rate every answer independently on a 1–5 scale:
        5 – fully correct, follows every instruction (format, length, language), nothing important missing, no fabrication.
        4 – correct and useful; minor omissions or style issues.
        3 – partly right; noticeable gaps, an ignored instruction, or padding.
        2 – significant errors, hallucinated facts, or the wrong format when a format was explicitly requested.
        1 – wrong, off-task, refuses without reason, or empty.
        Rules:
        - Verify claims against the provided data yourself (recompute numbers, check logic, check code/SQL against the given schema). Facts not supported by the data are a severe defect.
        - Read all answers before scoring so the scores are calibrated against each other, but do not rank on a curve – two equally good answers get the same score.
        - Ignore answer length unless the task sets a limit. Ignore the position/order of answers.
        - Do not try to guess which model wrote an answer.
        - If an <expected_answer> is given, it is the test author's reference: treat it as the gold standard for correctness and required content.
          Answers don't have to match its wording or format unless the task demands it, but contradicting it or missing its key points lowers the score.
          Mention in the comment what the answer got right or missed relative to the reference.
        - Write each comment in the language of the task (usually Polish), 1–3 sentences, naming the concrete reason for the score.
        Respond with ONLY a JSON object, no markdown fences, in exactly this shape:
        {"ratings":[{"label":"Model 1","stars":4,"comment":"..."}]}
        Include every answer you were given exactly once.
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = config.GetValue("Runner:MaxJudgeParallelism", 3), CancellationToken = stoppingToken },
            async (id, ct) =>
            {
                try { await RunAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { log.LogError(ex, "Judge runner crashed on {JudgeRunId}", id); }
            });
    }

    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = await db.JudgeRuns
                .Where(r => r.Status == ResultStatus.Pending || r.Status == ResultStatus.Running)
                .Select(r => r.Id).ToListAsync(ct);
            foreach (var id in ids) queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not requeue unfinished judge runs");
        }
    }

    private Task RunAsync(Guid runId, CancellationToken ct) =>
        control.RunAsync(runId, ct, token => RunCoreAsync(runId, token));

    private async Task RunCoreAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await control.Gate.WaitAsync(ct);
        JudgeRun? run;
        try
        {
            run = await db.JudgeRuns
                .Include(r => r.Model).ThenInclude(m => m.Provider)
                .Include(r => r.Iteration).ThenInclude(i => i.Results)
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null || run.Status is not (ResultStatus.Pending or ResultStatus.Running)) return;

            run.Status = ResultStatus.Running;
            run.Error = null;
            await db.SaveChangesAsync(ct);
        }
        finally { control.Gate.Release(); }
        events.RaiseIteration(run.IterationId);

        var sw = Stopwatch.StartNew();
        try
        {
            var answers = run.Iteration.Results.Where(r => r.Status == ResultStatus.Completed).ToList();
            if (answers.Count == 0) throw new LlmException("No successful answers to rate");

            var prompt = BuildPrompt(run.Iteration, answers);
            var response = await clients.For(run.Model.Provider.Type)
                .CompleteAsync(new LlmRequest(run.Model.Provider, run.Model, SystemPrompt, prompt), ct);
            run.CostUsd = response.CostUsd;
            run.RawOutput = response.Output;

            var verdicts = Parse(response.Output, answers);
            // Creating the bot must not flush this run's unfinished output/cost to the database.
            User judgeUser;
            await using (var userDb = await dbFactory.CreateDbContextAsync(ct))
                judgeUser = await EnsureJudgeUserAsync(userDb, run.Model, ct);
            var resultIds = verdicts.Keys.Select(r => r.Id).ToList();
            var existing = await db.Ratings.Where(x => x.UserId == judgeUser.Id && resultIds.Contains(x.ResultId)).ToListAsync(ct);

            foreach (var (result, (stars, comment)) in verdicts)
            {
                var rating = existing.FirstOrDefault(x => x.ResultId == result.Id);
                if (rating is null)
                {
                    rating = new Rating { ResultId = result.Id, UserId = judgeUser.Id };
                    db.Ratings.Add(rating);
                }
                rating.Stars = stars;
                rating.Comment = comment;
                rating.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var missing = answers.Count - verdicts.Count;
            run.Status = ResultStatus.Completed;
            run.Error = missing > 0 ? $"Judge skipped {missing} answer(s)" : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Judge run {JudgeRunId} failed", runId);
            run.Status = ResultStatus.Failed;
            run.Error = ex is LlmException or HttpRequestException or TaskCanceledException ? ex.Message : ex.ToString();
        }

        run.LatencyMs = sw.ElapsedMilliseconds;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await control.Gate.WaitAsync(CancellationToken.None);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!await db.JudgeRuns.AsNoTracking().AnyAsync(x => x.Id == runId && x.Status == ResultStatus.Running)) return;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally { control.Gate.Release(); }
        events.RaiseIteration(run.IterationId);
    }

    private static string BuildPrompt(Iteration iteration, List<IterationResult> answers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<task>");
        if (!string.IsNullOrWhiteSpace(iteration.SystemPromptSnapshot))
            sb.AppendLine("<system_prompt>").AppendLine(iteration.SystemPromptSnapshot).AppendLine("</system_prompt>");
        sb.AppendLine("<user_message>").AppendLine(iteration.UserMessageSnapshot).AppendLine("</user_message>");
        sb.AppendLine("</task>").AppendLine();
        if (!string.IsNullOrWhiteSpace(iteration.ExpectedAnswerSnapshot))
            sb.AppendLine("<expected_answer>").AppendLine(iteration.ExpectedAnswerSnapshot).AppendLine("</expected_answer>").AppendLine();
        sb.AppendLine("<answers>");
        // Present in random order: labels stay stable, but position bias doesn't line up with the slot number.
        foreach (var r in answers.OrderBy(_ => Random.Shared.Next()))
            sb.AppendLine($"<answer label=\"{r.BlindLabel}\">").AppendLine(r.Output).AppendLine("</answer>");
        sb.AppendLine("</answers>").AppendLine();
        sb.Append("Rate every answer. Reply with the JSON object only.");
        return sb.ToString();
    }

    private static Dictionary<IterationResult, (int Stars, string? Comment)> Parse(string output, List<IterationResult> answers)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) throw new LlmException("Judge did not return JSON");

        JsonNode? json;
        try { json = JsonNode.Parse(output[start..(end + 1)]); }
        catch (Exception ex) { throw new LlmException($"Judge returned invalid JSON: {ex.Message}"); }

        var items = json?["ratings"] as JsonArray ?? throw new LlmException("Judge response has no 'ratings' array");
        var bySlot = answers.ToDictionary(a => a.Slot);
        var verdicts = new Dictionary<IterationResult, (int, string?)>();

        foreach (var item in items.OfType<JsonObject>())
        {
            var label = item["label"]?.ToString() ?? item["model"]?.ToString() ?? item["slot"]?.ToString();
            var digits = label is null ? null : SlotNumber().Match(label).Value;
            if (!int.TryParse(digits, out var slot) || !bySlot.TryGetValue(slot, out var result)) continue;

            if (!double.TryParse(item["stars"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var starsRaw)) continue;
            var stars = (int)Math.Clamp(Math.Round(starsRaw), 1, 5);
            var comment = item["comment"]?.ToString();
            verdicts[result] = (stars, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim());
        }

        if (verdicts.Count == 0) throw new LlmException("Could not read any rating from the judge response");
        return verdicts;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex SlotNumber();

    /// <summary>Each judge model rates under its own bot account, so its ratings sit next to human ones.</summary>
    private static async Task<User> EnsureJudgeUserAsync(AppDbContext db, LlmModel model, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.JudgeModelId == model.Id, ct);
        if (user is not null) return user;

        var baseName = $"AI · {model.DisplayName}";
        var name = baseName;
        for (var i = 2; await db.Users.AnyAsync(u => u.Name == name, ct); i++) name = $"{baseName} ({i})";

        user = new User { Name = name, IsBot = true, JudgeModelId = model.Id, AvatarHue = EvalService.HueFor(name) };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }
}
