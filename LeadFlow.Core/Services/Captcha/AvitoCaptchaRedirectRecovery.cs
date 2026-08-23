using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// План восстановления только для залипшего экрана после успешного verify:
/// зелёный текст «Проверка пройдена, перенаправление…» при скрытом GeeTest.
/// Живую капчу с кнопкой «Продолжить» не перезагружаем.
/// </summary>
public static class AvitoCaptchaRedirectRecovery
{
    public const string ProfileItemsUrl = "https://www.avito.ru/profile/pro/items";

    private static readonly Regex ScriptOrStyleBlock = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static bool RequiresRecovery(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var visible = ScriptOrStyleBlock.Replace(html, " ");
        var normalized = Regex.Replace(visible, @"\s+", " ");
        return normalized.Contains("Проверка пройдена", StringComparison.OrdinalIgnoreCase)
               && (normalized.Contains("перенаправ", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("redirect", StringComparison.OrdinalIgnoreCase));
    }

    public static AvitoCaptchaRecoveryAction GetAction(int attempt) => attempt switch
    {
        1 => AvitoCaptchaRecoveryAction.Reload,
        2 => AvitoCaptchaRecoveryAction.NavigateCurrentPage,
        3 => AvitoCaptchaRecoveryAction.NavigateProfileItems,
        _ => AvitoCaptchaRecoveryAction.None
    };

    public static string GetCurrentPageTarget(string? currentUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri)
            || (!uri.Host.Equals("avito.ru", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.EndsWith(".avito.ru", StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileItemsUrl;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}

public enum AvitoCaptchaRecoveryAction
{
    None = 0,
    NavigateCurrentPage,
    Reload,
    NavigateProfileItems
}
