using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// План восстановления, когда Avito уже подтвердил проверку, но SPA не ушла с экрана
/// «Проверка пройдена, перенаправление…». План ограничен тремя разными действиями,
/// чтобы не создавать бесконечные reload/повторные обращения к капче.
/// </summary>
public static class AvitoCaptchaRedirectRecovery
{
    public const string ProfileItemsUrl = "https://www.avito.ru/profile/pro/items";

    public static bool RequiresRecovery(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = Regex.Replace(html, @"\s+", " ");
        return normalized.Contains("Проверка пройдена", StringComparison.OrdinalIgnoreCase)
               && (normalized.Contains("перенаправ", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("redirect", StringComparison.OrdinalIgnoreCase));
    }

    public static AvitoCaptchaRecoveryAction GetAction(int attempt) => attempt switch
    {
        1 => AvitoCaptchaRecoveryAction.NavigateCurrentPage,
        2 => AvitoCaptchaRecoveryAction.Reload,
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
