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
            "капча" or "капча / блок ip" => "captcha",
            "блок ip" => "ip_block",
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
            "капча" or "капча / блок ip" => "blocked",
            "блок ip" => "blocked",
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
        if (IsIpBlock(text, details))
            return false;

        if (text.Contains("капча", StringComparison.OrdinalIgnoreCase)
            || text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || text.Contains("firewall", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(details) || !details.TrimStart().StartsWith('{'))
            return false;

        var lowerDetails = details.ToLowerInvariant();
        return lowerDetails.Contains("\"kind\":\"captcha")
            || lowerDetails.Contains("subprofile-captcha")
            || lowerDetails.Contains("image-captcha")
            || lowerDetails.Contains("hcaptcha")
            || lowerDetails.Contains("geetest");
    }

    public static bool IsIpBlock(string text, string? details)
    {
        if (HasCaptchaChallengeSignals(text, details))
            return false;

        return text.Contains("блок ip", StringComparison.OrdinalIgnoreCase)
            || text.Contains("проблема с ip", StringComparison.OrdinalIgnoreCase)
            || text.Contains("доступ ограничен", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(details)
                && details.Contains("\"kind\":\"firewall", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Определяет приоритет ошибки для панели Орбита по уровню события и тексту.
    /// Классификация основана на реальных паттернах WorkerEvents в продакшене.
    /// </summary>
    public static string InferSeverity(string level, string message, string? details = null)
    {
        var text = $"{message} {details}";
        var lower = text.ToLowerInvariant();

        if (IsCriticalFailure(lower))
            return "critical";

        if (IsHighPriorityIncident(lower, details))
            return "high";

        if (IsLowPriorityTransient(lower))
            return "low";

        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
            && lower.Contains("err_aborted"))
            return "low";

        if (MapIssueLabelToSeverity(message, level) is { } labelSeverity)
            return labelSeverity;

        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            && (lower.Contains("timeout") || lower.Contains("таймаут") || lower.Contains("err_timed_out")))
            return "medium";

        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
            && (lower.Contains("не переключ")
                || lower.Contains("модалк")
                || lower.Contains("неизвестная страниц")
                || lower.Contains("ошибка на шаге")
                || lower.Contains("проблема:")))
            return "medium";

        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return "medium";

        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            return "low";

        return "low";
    }

    public static string? MapIssueLabelToSeverity(string message, string level)
    {
        var label = TryParseIssueLabel(message);
        if (label is null)
            return null;

        return Normalize(label) switch
        {
            "капча" or "капча / блок ip" or "блок ip" or "нужен вход" => "high",
            "профиль занят" or "лимит частоты adspower" or "дневной лимит adspower" => "low",
            "таймаут" => level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "medium" : "low",
            "не переключился" or "проблема" or "ошибка парсинга" => "medium",
            _ => null
        };
    }

    private static bool IsCriticalFailure(string lower) =>
        lower.Contains("postgres")
        || lower.Contains("база данных")
        || lower.Contains("критич")
        || lower.Contains("недоступ")
        || lower.Contains("object reference not set")
        || lower.Contains("targetcrashed")
        || lower.Contains("session closed")
        || lower.Contains("protocol error")
        || lower.Contains("err_insufficient_resources");

    private static bool IsHighPriorityIncident(string lower, string? details) =>
        IsCaptcha(lower, details)
        || lower.Contains("нужен вход")
        || lower.Contains("повторная авторизация")
        || lower.Contains("требуется повторная авторизация")
        || lower.Contains("avito требует повторный вход")
        || lower.Contains("доступ ограничен")
        || lower.Contains("проблема с ip")
        || lower.Contains("bitrix")
        || lower.Contains("crm");

    private static bool IsLowPriorityTransient(string lower) =>
        lower.Contains("профиль занят")
        || lower.Contains("профиль adspower занят")
        || AdsPowerErrorMessageNormalizer.LooksLikeProfileInUse(lower)
        || lower.Contains("лимит частоты adspower")
        || lower.Contains("дневной лимит adspower")
        || lower.Contains("rate limit adspower");

    /// <summary>
    /// Капча с «Продолжить»/GeeTest важнее заголовка «проблема с IP»: иначе событие
    /// попадает в группу «Блок IP» и панель не предлагает решить капчу.
    /// Явная метка «блок IP» остаётся блоком IP.
    /// </summary>
    private static bool HasCaptchaChallengeSignals(string text, string? details)
    {
        var label = TryParseIssueLabel(text);
        if (label is not null)
        {
            var normalized = Normalize(label);
            if (normalized is "капча" or "капча / блок ip")
                return true;
            if (normalized == "блок ip")
                return false;
        }

        if (text.Contains("решения капчи", StringComparison.OrdinalIgnoreCase)
            || text.Contains("для решения капчи", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(details) || !details.TrimStart().StartsWith('{'))
            return false;

        var lowerDetails = details.ToLowerInvariant();
        return lowerDetails.Contains("\"kind\":\"geetest")
            || lowerDetails.Contains("\"kind\":\"hcaptcha")
            || lowerDetails.Contains("\"kind\":\"image-captcha")
            || lowerDetails.Contains("\"kind\":\"captcha");
    }

    private static string Normalize(string label) => label.Trim().ToLowerInvariant();
}
