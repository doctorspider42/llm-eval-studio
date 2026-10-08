using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmEval.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Services;

/// <summary>
/// How a source row becomes a test case. Each field is a template: <c>{{column}}</c> (or <c>{{a.b}}</c>) is replaced
/// with the row's value; anything else is literal text, so "Odpowiedz: {{question}}" works too.
/// </summary>
public record ImportMapping(string Prompt, string? Title = null, string? Data = null, string? ExpectedAnswer = null,
    string? SystemPrompt = null, string? Tags = null);

/// <param name="Text">JSON array, JSONL, or an object with a rows/items/data array.</param>
/// <param name="Rows">Alternatively, rows as JSON objects (API clients).</param>
/// <param name="Mapping">null = guessed from column names.</param>
/// <param name="DryRun">true = only return columns, the guessed mapping and a preview.</param>
public record ImportRequest(string? Text = null, List<JsonObject>? Rows = null, ImportMapping? Mapping = null,
    List<string>? Tags = null, Guid? UserId = null, bool DryRun = false, int? Limit = null);

public record HfImportRequest(string Dataset, string? Config = null, string? Split = null, int Offset = 0, int Length = 50,
    ImportMapping? Mapping = null, List<string>? Tags = null, Guid? UserId = null, bool DryRun = false);

public record ImportPreview(int RowCount, List<string> Columns, ImportMapping Mapping, List<UpsertTestCaseRequest> Sample,
    int Created, List<string> Errors, List<Guid>? CreatedIds = null);

public record HfSplit(string Config, string Split);

public record HfPreset(string Label, string Dataset, string Config, string Split, string License, ImportMapping Mapping, string Description);

public partial class ImportService(IHttpClientFactory httpFactory, IDbContextFactory<AppDbContext> dbFactory, EvalEvents events)
{
    public const int MaxRows = 2000;
    private const string HfApi = "https://datasets-server.huggingface.co";

    /// <summary>Known-good permissive datasets (licenses checked on their Hugging Face cards).</summary>
    public static readonly HfPreset[] Presets =
    [
        new("IFEval", "google/IFEval", "default", "train", "Apache-2.0",
            new ImportMapping("{{prompt}}", Title: "IFEval {{key}}"),
            "541 poleceń z twardymi ograniczeniami formy („bez przecinków”, „3 akapity”…)."),
        new("GSM8K", "openai/gsm8k", "main", "test", "MIT",
            new ImportMapping("{{question}}", ExpectedAnswer: "{{answer}}"),
            "Zadania tekstowe z matematyki z rozwiązaniem krok po kroku."),
        new("TruthfulQA", "truthfulqa/truthful_qa", "generation", "validation", "Apache-2.0",
            new ImportMapping("{{question}}", Title: "TruthfulQA: {{category}}",
                ExpectedAnswer: "Najlepsza odpowiedź: {{best_answer}}\n\nPoprawne: {{correct_answers}}\n\nBłędne (pułapki): {{incorrect_answers}}",
                Tags: "{{category}}"),
            "Pytania-pułapki sprawdzające, czy model powtarza mity i błędne przekonania."),
    ];

    // ───────────────────────── parsing ─────────────────────────

    public static List<JsonObject> ParseRows(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0) return [];

        if (text[0] == '[' || (text[0] == '{' && !LooksLikeJsonLines(text)))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException ex) { throw EvalException.Invalid($"Niepoprawny JSON: {ex.Message}"); }

            var array = node as JsonArray
                        ?? node?["rows"] as JsonArray ?? node?["items"] as JsonArray ?? node?["data"] as JsonArray
                        ?? (node is JsonObject single ? new JsonArray(single.DeepClone()) : null)
                        ?? throw EvalException.Invalid("Oczekuję tablicy obiektów (albo obiektu z polem rows/items/data)");
            return array.Select(Unwrap).OfType<JsonObject>().ToList();
        }

        var rows = new List<JsonObject>();
        var lineNo = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (Unwrap(JsonNode.Parse(line)) is JsonObject o) rows.Add(o);
            }
            catch (JsonException ex)
            {
                throw EvalException.Invalid($"JSONL, linia {lineNo}: {ex.Message}");
            }
        }
        return rows;
    }

    private static bool LooksLikeJsonLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 1 && lines.Take(3).All(l => l.TrimStart().StartsWith('{') && l.TrimEnd().EndsWith('}'));
    }

    /// <summary>HF rows API wraps each row as {"row_idx":…,"row":{…}}.</summary>
    private static JsonNode? Unwrap(JsonNode? n) => n is JsonObject o && o["row"] is JsonObject inner && o.ContainsKey("row_idx") ? inner : n;

    public static List<string> Columns(IEnumerable<JsonObject> rows) =>
        rows.Take(50).SelectMany(r => r.Select(kv => kv.Key)).Distinct().ToList();

    public static ImportMapping GuessMapping(IReadOnlyCollection<string> columns)
    {
        string? Pick(params string[] names) =>
            names.Select(n => columns.FirstOrDefault(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null);
        string? T(string? col) => col is null ? null : $"{{{{{col}}}}}";

        var prompt = Pick("prompt", "instruction", "question", "query", "task", "input", "user", "text");
        var data = Pick("data", "context", "document", "transcript", "passage", "article", "content", "source", "input");
        if (data == prompt) data = null;
        return new ImportMapping(
            T(prompt) ?? "",
            Title: T(Pick("title", "name", "key", "id", "uid")),
            Data: T(data),
            ExpectedAnswer: T(Pick("expected_answer", "expectedAnswer", "expected", "answer", "best_answer", "reference", "target", "output", "solution", "response", "completion")),
            SystemPrompt: T(Pick("system_prompt", "systemPrompt", "system")),
            Tags: T(Pick("tags", "category", "categories", "type")));
    }

    [GeneratedRegex(@"\{\{\s*([\w.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static string Render(string? template, JsonObject row)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return Placeholder().Replace(template, m => ValueToText(Resolve(row, m.Groups[1].Value))).Trim();
    }

    private static JsonNode? Resolve(JsonObject row, string path)
    {
        JsonNode? cur = row;
        foreach (var part in path.Split('.'))
        {
            cur = cur switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (cur is null) return null;
        }
        return cur;
    }

    private static string ValueToText(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        JsonArray a when a.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)) => string.Join("\n", a.Select(x => "- " + x!.GetValue<string>())),
        JsonValue jv => jv.ToJsonString(),
        _ => v.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
    };

    public static (List<UpsertTestCaseRequest> Items, List<string> Errors) Map(IReadOnlyList<JsonObject> rows, ImportMapping mapping,
        IEnumerable<string>? extraTags, Guid? userId)
    {
        var items = new List<UpsertTestCaseRequest>();
        var errors = new List<string>();
        var tags = (extraTags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var prompt = Render(mapping.Prompt, row);
            if (prompt.Length == 0)
            {
                if (errors.Count < 20) errors.Add($"Wiersz {i + 1}: pusty prompt – pominięty");
                continue;
            }
            var title = Render(mapping.Title, row);
            if (title.Length == 0) title = prompt.ReplaceLineEndings(" ") is var flat && flat.Length > 70 ? flat[..70].TrimEnd() + "…" : flat;
            if (title.Length > 290) title = title[..290] + "…";

            var rowTags = Render(mapping.Tags, row).Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.TrimStart('-', ' ')).Where(t => t.Length is > 0 and < 40);

            items.Add(new UpsertTestCaseRequest(title, prompt,
                Data: NullIfEmpty(Render(mapping.Data, row)),
                SystemPrompt: NullIfEmpty(Render(mapping.SystemPrompt, row)),
                Tags: tags.Concat(rowTags).Distinct().ToList(),
                UserId: userId,
                ExpectedAnswer: NullIfEmpty(Render(mapping.ExpectedAnswer, row))));
        }
        return (items, errors);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ───────────────────────── import ─────────────────────────

    public async Task<ImportPreview> ImportAsync(ImportRequest req, CancellationToken ct = default)
    {
        var rows = req.Rows ?? ParseRows(req.Text ?? "");
        if (req.Limit is > 0) rows = rows.Take(req.Limit.Value).ToList();
        return await RunAsync(rows, req.Mapping, req.Tags, req.UserId, req.DryRun, ct);
    }

    public async Task<ImportPreview> ImportFromHfAsync(HfImportRequest req, CancellationToken ct = default)
    {
        var rows = await HfRowsAsync(req.Dataset, req.Config, req.Split, req.Offset, req.Length, ct);
        var tags = req.Tags ?? [req.Dataset.Split('/').Last().ToLowerInvariant()];
        return await RunAsync(rows, req.Mapping, tags, req.UserId, req.DryRun, ct);
    }

    private async Task<ImportPreview> RunAsync(List<JsonObject> rows, ImportMapping? mapping, List<string>? tags, Guid? userId, bool dryRun,
        CancellationToken ct)
    {
        if (rows.Count == 0) throw EvalException.Invalid("Brak wierszy do zaimportowania");
        if (rows.Count > MaxRows) throw EvalException.Invalid($"Za dużo wierszy ({rows.Count}) – limit to {MaxRows}");

        var columns = Columns(rows);
        mapping ??= GuessMapping(columns);
        if (string.IsNullOrWhiteSpace(mapping.Prompt)) throw EvalException.Invalid("Ustaw szablon promptu, np. {{question}}");

        var (items, errors) = Map(rows, mapping, tags, userId);
        List<Guid> ids = [];
        if (!dryRun && items.Count > 0) ids = await CreateBulkAsync(items, ct);
        return new ImportPreview(rows.Count, columns, mapping, items.Take(dryRun ? 5 : 3).ToList(), ids.Count, errors, ids);
    }

    private async Task<List<Guid>> CreateBulkAsync(List<UpsertTestCaseRequest> items, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var userId = items[0].UserId is { } uid && await db.Users.AnyAsync(u => u.Id == uid, ct) ? uid : (Guid?)null;
        var entities = new List<TestCase>();
        foreach (var r in items)
        {
            entities.Add(new TestCase
            {
                Title = r.Title,
                Prompt = r.Prompt,
                Data = r.Data,
                SystemPrompt = r.SystemPrompt,
                ExpectedAnswer = r.ExpectedAnswer,
                Tags = (r.Tags ?? []).Select(t => t.ToLowerInvariant()).Distinct().ToList(),
                CreatedById = userId,
            });
        }
        db.TestCases.AddRange(entities);
        await db.SaveChangesAsync(ct);
        events.Raise();
        return entities.Select(e => e.Id).ToList();
    }

    // ───────────────────────── Hugging Face ─────────────────────────

    public async Task<List<HfSplit>> HfSplitsAsync(string dataset, CancellationToken ct = default)
    {
        var json = await HfGetAsync($"/splits?dataset={Uri.EscapeDataString(dataset.Trim())}", ct);
        return json["splits"]?.AsArray()
                   .Select(s => new HfSplit(s!["config"]!.GetValue<string>(), s["split"]!.GetValue<string>())).ToList()
               ?? [];
    }

    public async Task<List<JsonObject>> HfRowsAsync(string dataset, string? config, string? split, int offset, int length, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(split))
        {
            var first = (await HfSplitsAsync(dataset, ct)).FirstOrDefault() ?? throw EvalException.Invalid("Dataset nie ma dostępnych splitów");
            config ??= first.Config;
            split ??= first.Split;
        }
        length = Math.Clamp(length, 1, MaxRows);
        var rows = new List<JsonObject>();
        // The API serves at most 100 rows per request.
        for (var pos = Math.Max(0, offset); rows.Count < length; pos += 100)
        {
            var take = Math.Min(100, length - rows.Count);
            var json = await HfGetAsync($"/rows?dataset={Uri.EscapeDataString(dataset.Trim())}&config={Uri.EscapeDataString(config)}" +
                                        $"&split={Uri.EscapeDataString(split)}&offset={pos}&length={take}", ct);
            var page = json["rows"]?.AsArray().Select(Unwrap).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? [];
            rows.AddRange(page);
            if (page.Count < take) break;
        }
        return rows;
    }

    private async Task<JsonNode> HfGetAsync(string path, CancellationToken ct)
    {
        using var res = await httpFactory.CreateClient("hf").GetAsync(HfApi + path, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var msg = JsonNode.Parse(body)?["error"]?.ToString() ?? body;
            throw new EvalException(res.StatusCode == System.Net.HttpStatusCode.NotFound ? 404 : 400, $"Hugging Face: {msg}");
        }
        return JsonNode.Parse(body)!;
    }
}
