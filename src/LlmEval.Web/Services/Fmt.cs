using System.Globalization;

namespace LlmEval.Web.Services;

public static class Fmt
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "—";
        var d = DateTimeOffset.UtcNow - when.Value;
        if (d.TotalSeconds < 60) return "przed chwilą";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min temu";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h temu";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d temu";
        return when.Value.ToLocalTime().ToString("d MMM yyyy", Pl);
    }

    public static string Date(DateTimeOffset when) => when.ToLocalTime().ToString("d MMM yyyy, HH:mm", Pl);

    public static string Ms(long? ms) => ms switch
    {
        null => "—",
        < 1000 => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:0.0} s",
        _ => $"{ms / 60000} min {ms % 60000 / 1000} s"
    };

    public static string Ms(double? ms) => Ms(ms is null ? null : (long?)ms.Value);

    public static string Stars(double? v) => v is null ? "—" : v.Value.ToString("0.0", Pl);

    public static int SlotHue(int slot) => (slot * 67 + 250) % 360;
}
