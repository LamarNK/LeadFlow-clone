namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Обёртка над ошибкой внутри CDP-сессии: сохраняет скриншот страницы для диагностики в панели.
/// </summary>
public sealed class SessionDiagnosticException : Exception
{
    public SessionDiagnosticException(
        Exception inner,
        string diagnosticKind,
        byte[]? screenshotPng,
        string? pageUrl,
        string? subProfileId = null,
        string? subProfileName = null)
        : base(inner.Message, inner)
    {
        DiagnosticKind = diagnosticKind;
        ScreenshotPng = screenshotPng is { Length: > 0 } ? screenshotPng : null;
        PageUrl = pageUrl;
        SubProfileId = subProfileId;
        SubProfileName = subProfileName;
    }

    public string DiagnosticKind { get; }

    public byte[]? ScreenshotPng { get; }

    public string? PageUrl { get; }

    public string? SubProfileId { get; }

    public string? SubProfileName { get; }
}