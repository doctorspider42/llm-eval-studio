namespace LlmEval.Web.Services;

/// <summary>Language-independent display helpers. Text and number formatting lives in <see cref="Localizer"/> / <see cref="I18n"/>.</summary>
public static class Fmt
{
    public static int SlotHue(int slot) => (slot * 67 + 250) % 360;
}
