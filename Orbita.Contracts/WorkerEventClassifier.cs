namespace Orbita.Contracts;

/// <summary>
/// Общая классификация событий/ошибок воркера по тексту сообщения и деталям.
/// Формат проблем аккаунта: «… — {метка}: {детали}».
/// </summary>
public static class WorkerEventClassifier
{
    public static string? TryParseIssueLabel(string message)
    {
        var separator = message.IndexOf(" — ", StringComparison.Ordinal);
        if (separator < 0)
            return null;

        var afterSeparator = message[(separator + 3)..];
        var colon = afterSeparator.IndexOf(':');
        if (colon <= 0)
            return null;

        return afterSeparator[..colon].Trim();
    }

    public static string? MapIssueLabelToEventType(string message)
    {
        var label = TryParseIssueLabel(message);
        if (label is null)
            return null;

        return Normalize(label) switch
        {
            "капча / блок ip" => "captcha",
            "нужен вход" => "auth",
            "не переключился" => "switch",
            "ошибка парсинга" or "таймаут" or "проблема" => "error",
            "лимит частоты adspower" or "дневной лимит adspower" or "профиль занят" => "error",
            _ => null
        };
    }

    public static string? MapIssueLabelToErrorType(string message)
    {
        var label = TryParseIssueLabel(message);
        if (label is null)
            return null;

        return Normalize(label) switch
        {
            "капча / блок ip" => "blocked",
            "нужен вход" => "auth",
            "не переключился" => "automation",
            "ошибка парсинга" => "parsing",
            "таймаут" => "network",
            "лимит частоты adspower" or "дневной лимит adspower" or "профиль занят" => "api",
            "проблема" => "automation",
            _ => null
        };
    }

    public static bool IsCaptcha(string text, string? details)
    {
        if (text.Contains("капча", StringComparison.OrdinalIgnoreCase)
            || text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || text.Contains("блок ip", StringComparison.OrdinalIgnoreCase)
            || text.Contains("firewall", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(details) || !details.TrimStart().StartsWith('{'))
            return false;

        var lowerDetails = details.ToLowerInvariant();
        return lowerDetails.Contains("\"kind\":\"captcha")
            || lowerDetails.Contains("subprofile-captcha")
            || lowerDetails.Contains("image-captcha")
            || lowerDetails.Contains("hcaptcha")
            || lowerDetails.Contains("geetest")
            || lowerDetails.Contains("\"kind\":\"firewall");
    }

    public static bool IsAutomationFailure(string text, string level)
    {
        if (IsWarningOrError(level)
            && (text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
                || text.Contains("неизвестная страница", StringComparison.OrdinalIgnoreCase)
                || text.Contains("проблема:", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (text.Contains("не удалось перейти", StringComparison.OrdinalIgnoreCase)
            || text.Contains("не удалось переключить", StringComparison.OrdinalIgnoreCase))
            return true;

        if (text.Contains("субпрофили ", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("модалка", StringComparison.OrdinalIgnoreCase)
                || text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)))
            return true;

        return text.Contains("ошибка аккаунта", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewResponseCandidate(string text, string level)
    {
        if (!level.Equals("success", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
            || text.Contains("проблема:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("неизвестная страница", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Contains("странице откликов", StringComparison.OrdinalIgnoreCase)
            || text.Contains("страница откликов", StringComparison.OrdinalIgnoreCase)
            || text.Contains("к откликам", StringComparison.OrdinalIgnoreCase)
            || text.Contains("перейти к откликам", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Contains("новый отклик", StringComparison.OrdinalIgnoreCase)
            || text.Contains("отклик получен", StringComparison.OrdinalIgnoreCase)
            || text.Contains("отклик отправлен", StringComparison.OrdinalIgnoreCase))
            return true;

        if (text.Contains("получен", StringComparison.OrdinalIgnoreCase)
            && text.Contains("отклик", StringComparison.OrdinalIgnoreCase))
            return true;

        return text.Contains("отклик", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("объект откликов", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("сбор откликов", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("мониторинг откликов", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNetworkFailure(string text) =>
        text.Contains("сеть", StringComparison.OrdinalIgnoreCase)
        || text.Contains("таймаут", StringComparison.OrdinalIgnoreCase)
        || text.Contains("подключ", StringComparison.OrdinalIgnoreCase)
        || text.Contains("connection", StringComparison.OrdinalIgnoreCase)
        || text.Contains("dns", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http ", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http/", StringComparison.OrdinalIgnoreCase)
        || text.Contains("httperror", StringComparison.OrdinalIgnoreCase)
        || text.Contains("httprequestexception", StringComparison.OrdinalIgnoreCase);

    public static bool IsWarningOrError(string level) =>
        level.Equals("warning", StringComparison.OrdinalIgnoreCase)
        || level.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string label) => label.Trim().ToLowerInvariant();
}