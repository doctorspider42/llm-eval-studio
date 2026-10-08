namespace LlmEval.Web.Data;

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Email { get; set; }
    /// <summary>Hue 0-360 used to render the avatar gradient.</summary>
    public int AvatarHue { get; set; }
    public bool IsBot { get; set; }
    /// <summary>Set for the bot account that stores ratings produced by an AI judge model.</summary>
    public Guid? JudgeModelId { get; set; }
    public LlmModel? JudgeModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ProviderType
{
    OpenAI,
    Anthropic,
    Ollama,
    ClaudeCli,
    CodexCli,
    OpenRouter
}

public class Provider
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public ProviderType Type { get; set; }
    /// <summary>API base URL (OpenAI / OpenRouter / Anthropic / Ollama). Null = provider default.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>API key. Null = fall back to env var (OPENAI_API_KEY / OPENROUTER_API_KEY / ANTHROPIC_API_KEY).</summary>
    public string? ApiKey { get; set; }
    /// <summary>Executable path for CLI providers. Null = "claude" / "codex" from PATH.</summary>
    public string? CliPath { get; set; }
    /// <summary>Extra CLI arguments appended to the command line.</summary>
    public string? ExtraArgs { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LlmModel> Models { get; set; } = [];
}

public class LlmModel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    /// <summary>Model identifier sent to the provider, e.g. "gpt-5" or "claude-sonnet-5-5".</summary>
    public required string ModelId { get; set; }
    public required string DisplayName { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Can be picked to rate iterations ("Oceń przez AI").</summary>
    public bool IsJudge { get; set; }
}

public class TestCase
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    public string? SystemPrompt { get; set; }
    /// <summary>The instruction. May contain {{data}} placeholder; otherwise data is appended.</summary>
    public required string Prompt { get; set; }
    public string? Data { get; set; }
    /// <summary>Reference answer. Never sent to the evaluated models – only to judges (AI and human).</summary>
    public string? ExpectedAnswer { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Iteration> Iterations { get; set; } = [];
}

public class Iteration
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TestCaseId { get; set; }
    public TestCase TestCase { get; set; } = null!;
    public int Number { get; set; }
    /// <summary>Set when the iteration was started as part of a series (many test cases × N repetitions).</summary>
    public Guid? BatchId { get; set; }
    public Batch? Batch { get; set; }
    /// <summary>1..Batch.Repetitions</summary>
    public int? Repetition { get; set; }
    /// <summary>Snapshot of what was actually sent, so editing the test case doesn't rewrite history.</summary>
    public string? SystemPromptSnapshot { get; set; }
    public required string UserMessageSnapshot { get; set; }
    public string? ExpectedAnswerSnapshot { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<IterationResult> Results { get; set; } = [];
    public bool AutoJudgeSuppressed { get; set; }
    public List<JudgeRun> JudgeRuns { get; set; } = [];
}

public enum ResultStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class IterationResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid IterationId { get; set; }
    public Iteration Iteration { get; set; } = null!;
    public Guid ModelId { get; set; }
    public LlmModel Model { get; set; } = null!;
    /// <summary>1-based blind label: "Model {Slot}". Randomised at creation.</summary>
    public int Slot { get; set; }
    public ResultStatus Status { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public long? LatencyMs { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<Rating> Ratings { get; set; } = [];

    public string BlindLabel => $"Model {Slot}";
}

public class Rating
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ResultId { get; set; }
    public IterationResult Result { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>1..5</summary>
    public int Stars { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One AI judge model rating every completed answer of one iteration.</summary>
public class JudgeRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid IterationId { get; set; }
    public Iteration Iteration { get; set; } = null!;
    public Guid ModelId { get; set; }
    public LlmModel Model { get; set; } = null!;
    public ResultStatus Status { get; set; }
    public string? Error { get; set; }
    /// <summary>Raw judge output, kept for debugging parse problems.</summary>
    public string? RawOutput { get; set; }
    public decimal? CostUsd { get; set; }
    public long? LatencyMs { get; set; }
    public Guid? RequestedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Saved AI synthesis of all repetitions in a series, with background generation state.</summary>
public class BatchSummaryRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BatchId { get; set; }
    public Batch Batch { get; set; } = null!;
    public Guid ModelId { get; set; }
    public LlmModel Model { get; set; } = null!;
    public required string Language { get; set; }
    public required string SourceHash { get; set; }
    public ResultStatus Status { get; set; }
    public string? Overview { get; set; }
    public string? CasesJson { get; set; }
    public string? Error { get; set; }
    public int CompletedCases { get; set; }
    public int TotalCases { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A series: the same model set run on many test cases, each repeated N times.</summary>
public class Batch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Note { get; set; }
    public int Repetitions { get; set; } = 1;
    /// <summary>Models in the series, shuffled once at creation – the index is the blind alias ("Kandydat A").</summary>
    public List<Guid> ModelIds { get; set; } = [];
    /// <summary>Judge every iteration automatically as soon as all its answers are in.</summary>
    public bool AutoJudge { get; set; }
    /// <summary>Judges for AutoJudge; empty = all models marked as judge at that moment.</summary>
    public List<Guid> JudgeModelIds { get; set; } = [];
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Iteration> Iterations { get; set; } = [];
}
