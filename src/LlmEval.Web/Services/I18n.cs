using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.JSInterop;

namespace LlmEval.Web.Services;

/// <summary>
/// Translations loaded from embedded <c>Resources/i18n/{lang}/*.json</c> files (flat "key": "text" maps; all files of a
/// language are merged). Missing keys fall back to the other language, then to the key itself – so an untranslated
/// literal passed as a key simply shows up as-is.
/// </summary>
public static class I18n
{
    public const string Polish = "pl";
    public const string English = "en";
    public static readonly string[] Languages = [Polish, English];

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Tables = Load();

    public static CultureInfo CultureOf(string lang) => CultureInfo.GetCultureInfo(lang == Polish ? "pl-PL" : "en-GB");

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, FrozenDictionary<string, string>>();
        foreach (var lang in Languages)
        {
            var map = new Dictionary<string, string>();
            // Embedded names look like "LlmEval.Web.Resources.i18n.pl.batch.json".
            var prefix = $".Resources.i18n.{lang}.";
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(prefix) && n.EndsWith(".json")).Order())
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
                foreach (var (k, v) in entries) map[k] = v;
            }
            result[lang] = map.ToFrozenDictionary();
        }
        return result.ToFrozenDictionary();
    }

    public static string Get(string lang, string key)
    {
        if (Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return v;
        foreach (var other in Languages)
            if (other != lang && Tables[other].TryGetValue(key, out var fb)) return fb;
        return key;
    }

    public static string Format(string lang, string key, params object?[] args)
    {
        var template = Get(lang, key);
        if (args.Length == 0) return template;
        try { return string.Format(CultureOf(lang), template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>
    /// Plural forms: <c>key.one</c>, <c>key.few</c> (pl: 2–4, 22–24…), <c>key.many</c>; English uses one/many.
    /// The text gets the number as {0}.
    /// </summary>
    public static string Plural(string lang, string key, long n, params object?[] extra)
    {
        string form;
        if (n == 1) form = "one";
        else if (lang == Polish && n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) form = "few";
        else form = "many";
        var k = $"{key}.{form}";
        if (Get(lang, k) == k && form == "few") k = $"{key}.many";
        return Format(lang, k, [n, .. extra]);
    }

    /// <summary>Keys present in one language but not the other – logged at startup in Development.</summary>
    public static IEnumerable<string> MissingKeys() =>
        from lang in Languages
        from other in Languages
        where lang != other
        from key in Tables[other].Keys
        where !Tables[lang].ContainsKey(key) && !key.EndsWith(".few") // English has no "few" form
        select $"{lang}: {key}";

    // ───────────── formatting ─────────────

    public static string Ago(string lang, DateTimeOffset? when)
    {
        if (when is null) return "—";
        var d = DateTimeOffset.UtcNow - when.Value;
        if (d.TotalSeconds < 60) return Get(lang, "time.justNow");
        if (d.TotalMinutes < 60) return Format(lang, "time.minutesAgo", (int)d.TotalMinutes);
        if (d.TotalHours < 24) return Format(lang, "time.hoursAgo", (int)d.TotalHours);
        if (d.TotalDays < 7) return Format(lang, "time.daysAgo", (int)d.TotalDays);
        return when.Value.ToLocalTime().ToString("d MMM yyyy", CultureOf(lang));
    }

    public static string Date(string lang, DateTimeOffset when) => when.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureOf(lang));

    public static string Ms(string lang, double? ms) => ms switch
    {
        null => "—",
        < 1000 => $"{ms:0} ms",
        < 60_000 => (ms.Value / 1000).ToString("0.0", CultureOf(lang)) + " s",
        _ => $"{(long)ms.Value / 60000} min {(long)ms.Value % 60000 / 1000} s"
    };

    public static string Stars(string lang, double? v) => v is null ? "—" : v.Value.ToString("0.0", CultureOf(lang));

    public static string Num(string lang, double v, string format = "N0") => v.ToString(format, CultureOf(lang));
}

/// <summary>Per-circuit translator. Language lives in localStorage; switching refreshes the routed components.</summary>
public class Localizer(IJSRuntime js)
{
    private const string StorageKey = "llmeval.lang";

    public string Lang { get; private set; } = I18n.English;
    public event Action? Changed;
    public CultureInfo Culture => I18n.CultureOf(Lang);

    public string this[string key] => I18n.Get(Lang, key);
    public string this[string key, params object?[] args] => I18n.Format(Lang, key, args);

    public string Plural(string key, long n, params object?[] extra) => I18n.Plural(Lang, key, n, extra);

    public async Task InitAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        if (stored is null)
        {
            var browser = await js.InvokeAsync<string?>("eval", "navigator.language || ''");
            stored = browser?.StartsWith("pl", StringComparison.OrdinalIgnoreCase) == true ? I18n.Polish : I18n.English;
        }
        Lang = I18n.Languages.Contains(stored) ? stored : I18n.English;
        await js.InvokeVoidAsync("document.documentElement.setAttribute", "lang", Lang);
    }

    public async Task SetAsync(string lang)
    {
        if (!I18n.Languages.Contains(lang)) return;
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, lang);
        await js.InvokeVoidAsync("document.documentElement.setAttribute", "lang", lang);
        Lang = lang;
        Changed?.Invoke();
    }

    /// <summary>User-facing text for any exception; EvalException carries a translation key.</summary>
    public string Error(Exception ex) => ex switch
    {
        EvalException e => this[e.Key, e.Args],
        _ => this["errors.generic", ex.Message]
    };

    public string Ago(DateTimeOffset? when) => I18n.Ago(Lang, when);
    public string Date(DateTimeOffset when) => I18n.Date(Lang, when);
    public string Ms(double? ms) => I18n.Ms(Lang, ms);
    public string Ms(long? ms) => I18n.Ms(Lang, ms);
    public string Stars(double? v) => I18n.Stars(Lang, v);
    public string Num(double v, string format = "N0") => I18n.Num(Lang, v, format);
}
