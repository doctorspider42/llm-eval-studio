using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmEval.Web.Data;

namespace LlmEval.Web.Llm;

public record LlmRequest(Provider Provider, LlmModel Model, string? SystemPrompt, string UserMessage);

public record LlmResponse(string Output, int? InputTokens = null, int? OutputTokens = null);

public interface ILlmClient
{
    ProviderType Type { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
    /// <summary>Models the provider reports as available (used for discovery / connection test).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(Provider provider, CancellationToken ct);
}

public class LlmException(string message) : Exception(message);

public class LlmClientFactory(IEnumerable<ILlmClient> clients)
{
    private readonly Dictionary<ProviderType, ILlmClient> _clients = clients.ToDictionary(c => c.Type);

    public ILlmClient For(ProviderType type) =>
        _clients.TryGetValue(type, out var c) ? c : throw new LlmException($"No client registered for {type}");
}

internal static class HttpHelpers
{
    public static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new LlmException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}: {Truncate(body, 1500)}");
        return JsonNode.Parse(body) ?? throw new LlmException("Empty response body");
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

public class OpenAiClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public ProviderType Type => ProviderType.OpenAI;

    private static string BaseUrl(Provider p) => string.IsNullOrWhiteSpace(p.BaseUrl) ? "https://api.openai.com/v1" : p.BaseUrl;

    private static string ApiKey(Provider p) =>
        p.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? throw new LlmException("Brak klucza API (ustaw w providerze albo OPENAI_API_KEY)");

    public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "developer", ["content"] = r.SystemPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = r.UserMessage });

        var body = new JsonObject { ["model"] = r.Model.ModelId, ["messages"] = messages };
        if (r.Model.Temperature is { } t) body["temperature"] = t;
        if (r.Model.MaxTokens is { } m) body["max_completion_tokens"] = m;

        using var req = new HttpRequestMessage(HttpMethod.Post, HttpHelpers.Combine(BaseUrl(r.Provider), "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(r.Provider));

        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        var text = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return new LlmResponse(text,
            json["usage"]?["prompt_tokens"]?.GetValue<int>(),
            json["usage"]?["completion_tokens"]?.GetValue<int>());
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(Provider p, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, HttpHelpers.Combine(BaseUrl(p), "models"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(p));
        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        return json["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).Order().ToList();
    }
}

public class AnthropicClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public ProviderType Type => ProviderType.Anthropic;

    private static string BaseUrl(Provider p) => string.IsNullOrWhiteSpace(p.BaseUrl) ? "https://api.anthropic.com/v1" : p.BaseUrl;

    private static void Auth(HttpRequestMessage req, Provider p)
    {
        var key = p.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new LlmException("Brak klucza API (ustaw w providerze albo ANTHROPIC_API_KEY)");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = r.Model.ModelId,
            ["max_tokens"] = r.Model.MaxTokens ?? 8192,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = r.UserMessage })
        };
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt)) body["system"] = r.SystemPrompt;
        if (r.Model.Temperature is { } t) body["temperature"] = t;

        using var req = new HttpRequestMessage(HttpMethod.Post, HttpHelpers.Combine(BaseUrl(r.Provider), "messages"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        Auth(req, r.Provider);

        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        var text = string.Concat(json["content"]!.AsArray()
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>()));
        return new LlmResponse(text,
            json["usage"]?["input_tokens"]?.GetValue<int>(),
            json["usage"]?["output_tokens"]?.GetValue<int>());
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(Provider p, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, HttpHelpers.Combine(BaseUrl(p), "models?limit=100"));
        Auth(req, p);
        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        return json["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToList();
    }
}

public class OllamaClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public ProviderType Type => ProviderType.Ollama;

    private static string BaseUrl(Provider p) => string.IsNullOrWhiteSpace(p.BaseUrl) ? "http://localhost:11434" : p.BaseUrl;

    public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = r.SystemPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = r.UserMessage });

        var options = new JsonObject();
        if (r.Model.Temperature is { } t) options["temperature"] = t;
        if (r.Model.MaxTokens is { } m) options["num_predict"] = m;

        var body = new JsonObject { ["model"] = r.Model.ModelId, ["messages"] = messages, ["stream"] = false, ["options"] = options };
        using var req = new HttpRequestMessage(HttpMethod.Post, HttpHelpers.Combine(BaseUrl(r.Provider), "api/chat"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        return new LlmResponse(json["message"]?["content"]?.GetValue<string>() ?? "",
            json["prompt_eval_count"]?.GetValue<int>(),
            json["eval_count"]?.GetValue<int>());
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(Provider p, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, HttpHelpers.Combine(BaseUrl(p), "api/tags"));
        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("llm"), req, ct);
        return json["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).Order().ToList();
    }
}

/// <summary>Shared plumbing for CLI-based providers: prompt goes in on stdin, runs in a throwaway dir.</summary>
public abstract class CliClientBase : ILlmClient
{
    public abstract ProviderType Type { get; }
    protected abstract string DefaultExecutable { get; }
    public abstract Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
    public abstract Task<IReadOnlyList<string>> ListModelsAsync(Provider provider, CancellationToken ct);

    protected static string ComposePrompt(LlmRequest r) =>
        string.IsNullOrWhiteSpace(r.SystemPrompt) ? r.UserMessage : $"{r.SystemPrompt}\n\n---\n\n{r.UserMessage}";

    protected async Task<(string Stdout, string Stderr)> RunAsync(Provider p, IEnumerable<string> args, string stdin, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(p.CliPath) ? DefaultExecutable : p.CliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var a in SplitArgs(p.ExtraArgs)) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, p.TimeoutSeconds)));

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new LlmException($"Nie udało się uruchomić {psi.FileName}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new LlmException($"Nie znaleziono '{psi.FileName}' ({ex.Message}). Ustaw ścieżkę CLI w providerze.");
        }

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                throw new LlmException($"Timeout po {p.TimeoutSeconds}s");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new LlmException($"{psi.FileName} exit code {proc.ExitCode}: {HttpHelpers.Truncate(stderr.Length > 0 ? stderr : stdout, 2000)}");
            return (stdout, stderr);
        }
    }

    protected static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llm-eval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static IEnumerable<string> SplitArgs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

public class ClaudeCliClient : CliClientBase
{
    public override ProviderType Type => ProviderType.ClaudeCli;
    protected override string DefaultExecutable => "claude";

    public override async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        var dir = NewWorkDir();
        try
        {
            // Isolate the run so we evaluate the model, not the user's Claude Code setup:
            // no tools, no CLAUDE.md / settings, no MCP servers, no coding-agent system prompt.
            List<string> args =
            [
                "-p", "--output-format", "json", "--model", r.Model.ModelId,
                "--tools", "", "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence",
                "--system-prompt", string.IsNullOrWhiteSpace(r.SystemPrompt) ? "You are a helpful assistant." : r.SystemPrompt
            ];

            var (stdout, _) = await RunAsync(r.Provider, args, r.UserMessage, dir, ct);
            var json = JsonNode.Parse(stdout) ?? throw new LlmException("Pusty output z claude CLI");
            if (json["is_error"]?.GetValue<bool>() == true)
                throw new LlmException($"claude CLI: {json["result"]}");
            var usage = json["usage"];
            int? input = usage is null ? null
                : (usage["input_tokens"]?.GetValue<int>() ?? 0)
                  + (usage["cache_read_input_tokens"]?.GetValue<int>() ?? 0)
                  + (usage["cache_creation_input_tokens"]?.GetValue<int>() ?? 0);
            return new LlmResponse(json["result"]?.GetValue<string>() ?? "", input, usage?["output_tokens"]?.GetValue<int>());
        }
        catch (JsonException)
        {
            throw new LlmException("claude CLI zwrócił niepoprawny JSON");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public override Task<IReadOnlyList<string>> ListModelsAsync(Provider provider, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["opus", "sonnet", "haiku", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001"]);
}

public class CodexCliClient : CliClientBase
{
    public override ProviderType Type => ProviderType.CodexCli;
    protected override string DefaultExecutable => "codex";

    public override async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        var dir = NewWorkDir();
        var outFile = Path.Combine(dir, "last-message.txt");
        try
        {
            List<string> args = ["exec", "--skip-git-repo-check", "--sandbox", "read-only", "--output-last-message", outFile];
            if (!string.IsNullOrWhiteSpace(r.Model.ModelId) && r.Model.ModelId != "default")
                args.AddRange(["--model", r.Model.ModelId]);
            args.Add("-"); // read prompt from stdin

            var (stdout, _) = await RunAsync(r.Provider, args, ComposePrompt(r), dir, ct);
            var text = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct) : stdout;
            return new LlmResponse(text.Trim());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public override Task<IReadOnlyList<string>> ListModelsAsync(Provider provider, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["default", "gpt-5-codex", "gpt-5"]);
}
