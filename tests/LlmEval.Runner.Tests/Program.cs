using LlmEval.Web.Data;
using LlmEval.Web.Llm;
using LlmEval.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var factory = new DbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
var queue = new ResultQueue();
var judges = new JudgeQueue();
var events = new EvalEvents();
var control = new RunControl();
var client = new FakeClient();
var clients = new LlmClientFactory([client]);
var svc = new EvalService(factory, queue, judges, events, clients, control);
var config = new ConfigurationBuilder().Build();

var provider = new Provider { Name = "Fake", Type = ProviderType.Ollama };
var model = new LlmModel { Provider = provider, ModelId = "fake", DisplayName = "Fake", IsJudge = true };
var test = new TestCase { Title = "Cancellation test", Prompt = "question" };
var batch = new Batch { Name = "Test series", AutoJudge = true, ModelIds = [model.Id], JudgeModelIds = [model.Id] };
var iteration = new Iteration { TestCase = test, Batch = batch, Number = 1, UserMessageSnapshot = "question" };
var result = new IterationResult { Iteration = iteration, Model = model, Slot = 1 };
await using (var db = factory.CreateDbContext()) { db.Results.Add(result); await db.SaveChangesAsync(); }

await svc.CancelResultAsync(result.Id);
await AssertResult(ResultStatus.Cancelled);
using var runner = new IterationRunner(queue, factory, clients, events, control, svc, config, NullLogger<IterationRunner>.Instance);
await runner.StartAsync(default);
queue.Enqueue(result.Id); // cancelled jobs must also be skipped if already in the channel
await Task.Delay(150);
Check(client.Calls == 0, "cancelled pending jobs are not sent to provider or requeued on startup");

await svc.RetryResultAsync(result.Id);
await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await svc.CancelIterationAsync(iteration.Id);
await control.WaitForIdleAsync(result.Id, default);
await AssertResult(ResultStatus.Cancelled);
Check(client.Cancelled, "active generation receives cancellation");
await using (var db = factory.CreateDbContext())
{
    Check((await db.Iterations.FindAsync(iteration.Id))!.AutoJudgeSuppressed, "stopping iteration suppresses auto judge");
    Check(!await db.JudgeRuns.AnyAsync(), "cancelled generation does not start auto judge");
}

// A provider that returns a late response despite cancellation must not overwrite Cancelled.
client.Reset(ignoreCancellation: true);
await svc.RetryResultAsync(result.Id);
await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await svc.CancelResultAsync(result.Id);
client.Release.TrySetResult();
await control.WaitForIdleAsync(result.Id, default);
await AssertResult(ResultStatus.Cancelled);

client.Reset();
await svc.RetryResultAsync(result.Id);
await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
client.Release.TrySetResult();
await control.WaitForIdleAsync(result.Id, default);
await AssertResult(ResultStatus.Completed);
await using (var db = factory.CreateDbContext()) Check((await db.Results.FindAsync(result.Id))!.CostUsd == 0.012345m, "generation cost persisted after retry");

var judge = new JudgeRun { IterationId = iteration.Id, ModelId = model.Id };
await using (var db = factory.CreateDbContext()) { db.JudgeRuns.Add(judge); await db.SaveChangesAsync(); }
client.Reset(ignoreCancellation: true);
using var judgeRunner = new JudgeRunner(judges, factory, clients, events, control, config, NullLogger<JudgeRunner>.Instance);
await judgeRunner.StartAsync(default);
await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await svc.CancelJudgeAsync(judge.Id);
client.Release.TrySetResult();
await control.WaitForIdleAsync(judge.Id, default);
await using (var db = factory.CreateDbContext())
{
    Check((await db.JudgeRuns.FindAsync(judge.Id))!.Status == ResultStatus.Cancelled, "late judge cannot overwrite cancellation");
    Check(!await db.Ratings.AnyAsync(), "cancelled judge does not save ratings");
}

var pending = new JudgeRun { IterationId = iteration.Id, ModelId = model.Id };
await using (var db = factory.CreateDbContext()) { db.JudgeRuns.Add(pending); await db.SaveChangesAsync(); }
await svc.CancelBatchAsync(batch.Id, judges: true);
await using (var db = factory.CreateDbContext())
{
    Check((await db.JudgeRuns.FindAsync(pending.Id))!.Status == ResultStatus.Cancelled, "series stops pending judges");
    Check(!(await db.Batches.FindAsync(batch.Id))!.AutoJudge, "series disables future auto judge");
    Check((await db.Results.FindAsync(result.Id))!.Status == ResultStatus.Completed, "completed answers remain available");
}

client.Reset();
var completedJudge = new JudgeRun { IterationId = iteration.Id, ModelId = model.Id };
await using (var db = factory.CreateDbContext()) { db.JudgeRuns.Add(completedJudge); await db.SaveChangesAsync(); }
judges.Enqueue(completedJudge.Id);
await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
client.Release.TrySetResult();
await control.WaitForIdleAsync(completedJudge.Id, default);
await using (var db = factory.CreateDbContext())
{
    var saved = (await db.JudgeRuns.FindAsync(completedJudge.Id))!;
    Check(saved.Status == ResultStatus.Completed && saved.CostUsd == 0.012345m, "successful judge stores cost");
    Check(await db.Ratings.CountAsync() == 1, "successful judge saves ratings");
}

var pendingAnswer = new IterationResult { IterationId = iteration.Id, ModelId = model.Id, Slot = 2 };
await using (var db = factory.CreateDbContext()) { db.Results.Add(pendingAnswer); await db.SaveChangesAsync(); }
await svc.CancelBatchAsync(batch.Id);
await using (var db = factory.CreateDbContext())
{
    Check((await db.Results.FindAsync(pendingAnswer.Id))!.Status == ResultStatus.Cancelled, "series stops queued answers");
    Check((await db.Results.FindAsync(result.Id))!.Status == ResultStatus.Completed, "series retains completed answers");
}

await runner.StopAsync(default);
await judgeRunner.StopAsync(default);
Console.WriteLine("PASS: pending/running cancellation, retry, late response races, costs, judge ratings and auto-judge suppression");

async Task AssertResult(ResultStatus expected)
{
    await using var db = factory.CreateDbContext();
    var saved = (await db.Results.FindAsync(result.Id))!;
    Check(saved.Status == expected, $"expected result status {expected}, got {saved.Status}");
    if (expected == ResultStatus.Cancelled) Check(saved.Output is null && saved.CompletedAt is not null, "cancelled state persists without output");
}
static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }

sealed class DbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}

sealed class FakeClient : ILlmClient
{
    public ProviderType Type => ProviderType.Ollama;
    public int Calls;
    public bool Cancelled;
    private bool _ignoreCancellation;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset(bool ignoreCancellation = false)
    {
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Cancelled = false;
        _ignoreCancellation = ignoreCancellation;
    }
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Calls++;
        Entered.TrySetResult();
        try { await Release.Task.WaitAsync(_ignoreCancellation ? CancellationToken.None : ct); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        return new("{\"ratings\":[{\"label\":\"Model 1\",\"stars\":4,\"comment\":\"OK\"}]}", 10, 20, 0.012345m);
    }
    public Task<IReadOnlyList<string>> ListModelsAsync(Provider provider, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["fake"]);
}
