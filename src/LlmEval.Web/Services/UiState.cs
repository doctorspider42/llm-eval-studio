using Microsoft.JSInterop;

namespace LlmEval.Web.Services;

/// <summary>Per-circuit "who am I" + theme, persisted in the browser's localStorage.</summary>
public class CurrentUser(IJSRuntime js, EvalService svc)
{
    private const string UserKey = "llmeval.user";
    private const string ThemeKey = "llmeval.theme";

    public static readonly (string Id, string Name, string Swatch)[] Themes =
    [
        ("aurora", "Aurora", "linear-gradient(135deg,#8b5cf6,#22d3ee)"),
        ("ember", "Ember", "linear-gradient(135deg,#f97316,#f43f5e)"),
        ("matrix", "Matrix", "linear-gradient(135deg,#10b981,#a3e635)"),
        ("abyss", "Abyss", "linear-gradient(135deg,#3b82f6,#06b6d4)"),
        ("mono", "Mono", "linear-gradient(135deg,#fafafa,#525252)"),
    ];

    public UserDto? User { get; private set; }
    public string Theme { get; private set; } = "aurora";
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public async Task InitAsync()
    {
        if (Loaded) return;
        var theme = await js.InvokeAsync<string?>("localStorage.getItem", ThemeKey);
        if (Themes.Any(t => t.Id == theme)) Theme = theme!;
        await ApplyThemeAsync();

        var raw = await js.InvokeAsync<string?>("localStorage.getItem", UserKey);
        if (Guid.TryParse(raw, out var id))
        {
            try { User = await svc.GetUserAsync(id); }
            catch (EvalException) { User = null; }
        }
        Loaded = true;
        Changed?.Invoke();
    }

    public async Task SetUserAsync(UserDto? user)
    {
        User = user;
        if (user is null) await js.InvokeVoidAsync("localStorage.removeItem", UserKey);
        else await js.InvokeVoidAsync("localStorage.setItem", UserKey, user.Id.ToString());
        Changed?.Invoke();
    }

    public async Task SetThemeAsync(string theme)
    {
        Theme = theme;
        await js.InvokeVoidAsync("localStorage.setItem", ThemeKey, theme);
        await ApplyThemeAsync();
        Changed?.Invoke();
    }

    private ValueTask ApplyThemeAsync() => js.InvokeVoidAsync("document.documentElement.setAttribute", "data-theme", Theme);
}

public class Toaster
{
    public record Toast(Guid Id, string Message, bool IsError);

    public List<Toast> Items { get; } = [];
    public event Action? Changed;

    public void Ok(string message) => Show(message, false);
    public void Error(string message) => Show(message, true);

    private void Show(string message, bool isError)
    {
        var t = new Toast(Guid.NewGuid(), message, isError);
        Items.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(isError ? 6000 : 3200).ContinueWith(_ => Dismiss(t.Id));
    }

    public void Dismiss(Guid id)
    {
        if (Items.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>Runs an action and turns EvalException / anything else into a toast. Returns success.</summary>
    public async Task<bool> Try(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            if (success is not null) Ok(success);
            return true;
        }
        catch (EvalException ex)
        {
            Error(ex.Message);
        }
        catch (Exception ex)
        {
            Error($"Coś poszło nie tak:{ex.Message}");
        }
        return false;
    }
}
