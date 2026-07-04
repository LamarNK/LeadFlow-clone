namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Avito показал форму входа во время автоматизации. Аккаунт нужно заново авторизовать в AdsPower.
/// </summary>
public sealed class AvitoLoginRequiredException : Exception
{
    public AvitoLoginRequiredException(
        string? url,
        string? title = null,
        byte[]? screenshotPng = null,
        string? subProfileId = null,
        string? subProfileName = null)
        : base(BuildMessage(url, title))
    {
        Url = url;
        Title = title;
        SubProfileId = subProfileId;
        SubProfileName = subProfileName;
        ScreenshotPng = screenshotPng is { Length: > 0 } ? screenshotPng : null;
    }

    public string? Url { get; }

    public string? Title { get; }

    public byte[]? ScreenshotPng { get; }

    public string? SubProfileId { get; }

    public string? SubProfileName { get; }

    private static string BuildMessage(string? url, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title) && title.Contains("Вход", StringComparison.OrdinalIgnoreCase))
        {
            return "Avito требует повторный вход (форма авторизации).";
        }

        return string.IsNullOrWhiteSpace(url)
            ? "Avito требует повторный вход (форма авторизации)."
            : $"Avito требует повторный вход на странице {url}.";
    }
}