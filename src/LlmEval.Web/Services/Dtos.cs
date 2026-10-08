using LlmEval.Web.Data;

namespace LlmEval.Web.Services;

public record UserDto(Guid Id, string Name, string? Email, int AvatarHue, bool IsBot, DateTimeOffset CreatedAt, Guid? JudgeModelId = null)
{
    public bool IsAiJudge => JudgeModelId is not null;
}

public record ModelDto(Guid Id, Guid ProviderId, string ProviderName, ProviderType ProviderType, string ModelId,
    string DisplayName, double? Temperature, int? MaxTokens, bool Enabled, bool ProviderEnabled, bool IsJudge);

public record ProviderDto(Guid Id, string Name, ProviderType Type, string? BaseUrl, bool HasApiKey, string? CliPath,
    string? ExtraArgs, int TimeoutSeconds, bool Enabled, List<ModelDto> Models);

public record ModelRef(Guid Id, string DisplayName, string ModelId, string ProviderName, ProviderType ProviderType);

public record TestCaseSummaryDto(Guid Id, string Title, string PromptPreview, List<string> Tags, int IterationCount,
    DateTimeOffset? LastRunAt, double? AvgStars, string? CreatedBy, DateTimeOffset CreatedAt, string? Category = null);

public record TestCaseDto(Guid Id, string Title, string? SystemPrompt, string Prompt, string? Data, List<string> Tags,
    Guid? CreatedById, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<IterationSummaryDto> Iterations,
    string? ExpectedAnswer = null, string? Category = null);

public record IterationSummaryDto(Guid Id, Guid TestCaseId, string TestCaseTitle, int Number, string? Note, string? CreatedBy,
    DateTimeOffset CreatedAt, int ResultCount, int CompletedCount, int FailedCount, int RatingCount, double? AvgStars,
    Guid? BatchId = null, string? BatchName = null, int? Repetition = null, int CancelledCount = 0)
{
    public bool IsRunning => CompletedCount + FailedCount + CancelledCount < ResultCount;
}

public record RatingDto(Guid Id, Guid ResultId, Guid UserId, string UserName, int UserAvatarHue, bool UserIsBot, bool UserIsAiJudge,
    int Stars, string? Comment, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Judge model is always visible – it's the evaluator, not the evaluated.</summary>
public record JudgeRunDto(Guid Id, Guid ModelId, string ModelName, ProviderType ProviderType, ResultStatus Status, string? Error,
    string? RawOutput, long? LatencyMs, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, decimal? CostUsd = null);

/// <summary>Model is null unless the iteration was requested with reveal=true.</summary>
public record ResultDto(Guid Id, int Slot, string Label, ModelRef? Model, ResultStatus Status, string? Output, string? Error,
    int? InputTokens, int? OutputTokens, long? LatencyMs, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    double? AvgStars, List<RatingDto> Ratings, decimal? CostUsd = null);

public record IterationDto(Guid Id, Guid TestCaseId, string TestCaseTitle, int Number, string? Note, string? SystemPrompt,
    string UserMessage, string? CreatedBy, DateTimeOffset CreatedAt, bool Revealed, bool IsComplete, List<ResultDto> Results,
    List<JudgeRunDto> JudgeRuns, BatchNavDto? Batch = null, string? ExpectedAnswer = null);

/// <summary>Where an iteration sits inside its series – drives the prev/next bar.</summary>
public record BatchNavDto(Guid BatchId, string BatchName, int? Repetition, int Repetitions, int Position, int Count,
    Guid? PrevIterationId, Guid? NextIterationId);

public record BatchSummaryDto(Guid Id, string Name, string? Note, string? CreatedBy, DateTimeOffset CreatedAt,
    int TestCaseCount, int ModelCount, int Repetitions, int IterationCount,
    int ResultTotal, int ResultDone, int ResultFailed, int JudgeTotal, int JudgeDone, bool AutoJudge, double? AvgStars)
{
    public bool IsRunning => ResultDone < ResultTotal;
    public bool IsJudging => JudgeDone < JudgeTotal;
}

/// <summary>Per-model aggregate over the whole series. Model is null unless revealed – Alias ("Kandydat A") is stable within the series.</summary>
public record BatchModelStatDto(string Alias, ModelRef? Model, int Results, int Failures, int RatingCount, double? AvgStars,
    double? AvgStarsHuman, double? AvgStarsAi, double? StdDev, int Wins, double? AvgLatencyMs, double? AvgOutputTokens);

/// <summary>One candidate's answer inside a matrix cell.</summary>
public record BatchCellModelDto(string Alias, ResultStatus Status, double? AvgStars, int RatingCount);

public record BatchCellDto(Guid IterationId, int Repetition, int Number, int Total, int Done, int Failed, int RatingCount,
    double? AvgStars, int JudgeTotal, int JudgeDone, bool RatedByMe, List<BatchCellModelDto> Models);

public record BatchAliasAvgDto(string Alias, double? AvgStars);

public record BatchRowDto(Guid TestCaseId, string Title, List<BatchCellDto> Cells, double? AvgStars, List<BatchAliasAvgDto> ModelAvgs);

public record BatchDto(BatchSummaryDto Summary, bool Revealed, List<Guid> JudgeModelIds, List<BatchModelStatDto> Models, List<BatchRowDto> Rows);

public record BatchJudgeResult(int QueuedRuns, int Iterations, int Deferred, List<string> Judges);

public record LeaderboardEntry(ModelRef Model, int Runs, int Failures, int RatingCount, double? AvgStars, double? AvgLatencyMs, int Wins,
    double? AvgStarsHuman, double? AvgStarsAi);

public record DashboardDto(int Users, int TestCases, int Iterations, int Results, int Ratings,
    List<LeaderboardEntry> Leaderboard, List<IterationSummaryDto> Recent);

// ---- requests ----

public record UpsertUserRequest(string Name, string? Email = null, bool IsBot = false);

/// <param name="ApiKey">null = keep current key, "" = remove key, anything else = set.</param>
public record UpsertProviderRequest(string Name, ProviderType Type, string? BaseUrl = null, string? ApiKey = null,
    string? CliPath = null, string? ExtraArgs = null, int? TimeoutSeconds = null, bool Enabled = true);

public record UpsertModelRequest(Guid ProviderId, string ModelId, string? DisplayName = null, double? Temperature = null,
    int? MaxTokens = null, bool Enabled = true, bool IsJudge = false);

public record UpsertTestCaseRequest(string Title, string Prompt, string? Data = null, string? SystemPrompt = null,
    List<string>? Tags = null, Guid? UserId = null, string? ExpectedAnswer = null, string? Category = null);

public record SetTestCaseCategoryRequest(List<Guid> TestCaseIds, string? Category = null);

public record RunIterationRequest(List<Guid> ModelIds, Guid? UserId = null, string? Note = null);

/// <summary>Partial update for quick toggles; null fields are left untouched.</summary>
public record PatchModelRequest(bool? Enabled = null, bool? IsJudge = null);

public record PatchProviderRequest(bool? Enabled = null);

/// <param name="JudgeModelIds">null/empty = every enabled model marked as judge.</param>
public record JudgeIterationRequest(List<Guid>? JudgeModelIds = null, Guid? UserId = null);

/// <param name="Repetitions">How many times each test case is run on every model (1–20).</param>
/// <param name="JudgeModelIds">For AutoJudge; null/empty = all models marked as judge.</param>
public record CreateBatchRequest(List<Guid> TestCaseIds, List<Guid> ModelIds, int Repetitions = 1, string? Name = null,
    string? Note = null, Guid? UserId = null, bool AutoJudge = false, List<Guid>? JudgeModelIds = null);

/// <param name="OnlyUnjudged">true = skip iterations a judge already rated; false = re-judge everything.</param>
public record JudgeBatchRequest(List<Guid>? JudgeModelIds = null, bool OnlyUnjudged = true, Guid? UserId = null);

public record RateResultRequest(Guid UserId, int Stars, string? Comment = null);

public record ConnectionTestResult(bool Ok, string Message, IReadOnlyList<string> Models);
