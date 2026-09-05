namespace Orbita.Contracts;

/// <summary>
/// Безопасная диагностика запуска обычного Chrome. Не кладёт в текст пароли Avito/прокси, логин прокси, Bearer и API keys.
/// </summary>
public static class LocalChromeLaunchDiagnostics
{
    public const string ErrorKey = "local.chrome.launch";
    public const string FailurePrefix = "Обычный браузер: запуск Chrome не удался — ";
    public const string ProcessLaunchStage = "запуск процесса";
    public const string ProfileBusyStage = "профиль занят";
    public const string WaitDevToolsStage = "ожидание DevTools";
    public const string ConnectStage = "подключение";
    public const string ProtocolStage = "протокол CDP";
    public const string ProtocolMismatchReason =
        "установленный Chrome несовместим с этим воркером — обновите воркер";
    public const int MaxErrorLength = 420;

    public static bool IsProfileBusy(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsProfileBusy(FlattenMessages(exception))
            || IsProfileBusy(exception.ToString());
    }

    public static bool IsProfileBusy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ContainsAny(
            text,
            "already running",
            "processsingleton",
            "failed to create a processsingleton",
            "use a different userdatadir",
            "профиль занят");
    }

    public static bool IsProtocolMismatch(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsProtocolMismatch(FlattenMessages(exception));
    }

    public static bool IsProtocolMismatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsProfileBusy(text))
        {
            return false;
        }

        if (ContainsAny(text, "devtoolsactiveport", "devtools active port"))
        {
            return false;
        }

        return ContainsAny(
            text,
            "protocol error",
            "runtime.callfunctionon",
            "target.setdiscovertargets",
            "method not found");
    }

    public static string ClassifyStage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = FlattenMessages(exception);
        if (IsProfileBusy(text))
        {
            return ProfileBusyStage;
        }

        if (ContainsAny(text, "devtoolsactiveport", "devtools active port", "waiting for chrome", "waiting for the browser", "browser to be ready"))
        {
            return WaitDevToolsStage;
        }

        if (IsProtocolMismatch(text))
        {
            return ProtocolStage;
        }

        if (ContainsAny(text, "websocket", "ws://", "failed to connect", "unable to connect", "connection refused", "target closed", "browser has been closed", "browser closed", "cdp"))
        {
            return ConnectStage;
        }

        return ProcessLaunchStage;
    }

    public static string DescribeExceptionType(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = UnwrapAggregate(exception);
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.GetType().Name;
    }

    public static string ToSafeError(
        Exception exception,
        string? chromePath,
        string? profilePath,
        bool proxyEnabled,
        params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Message.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            return Finalize(exception.Message, secrets);
        }

        var reason = IsProtocolMismatch(exception)
            ? ProtocolMismatchReason
            : BrowserProviderProbeSanitizer.Sanitize(
                FlattenMessages(exception),
                maxLength: 240,
                fallback: "неизвестная ошибка",
                secrets);
        var chrome = DisplayPath(chromePath, secrets);
        var profile = DisplayPath(profilePath, secrets);
        var stage = ClassifyStage(exception);
        var type = DescribeExceptionType(exception);
        var proxy = proxyEnabled ? "true" : "false";
        return Finalize(
            $"{FailurePrefix}{reason} (этап: {stage}; {type}; chrome={chrome}; profile={profile}; proxy={proxy})",
            secrets);
    }

    public static InvalidOperationException Wrap(
        Exception exception,
        string? chromePath,
        string? profilePath,
        bool proxyEnabled,
        params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is InvalidOperationException existing
            && existing.Message.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            return existing;
        }

        return new InvalidOperationException(
            ToSafeError(exception, chromePath, profilePath, proxyEnabled, secrets),
            exception);
    }

    private static string Finalize(string message, string?[] secrets)
    {
        var result = BrowserProviderProbeSanitizer.StripSecrets(message, secrets);
        if (result.Length == 0)
        {
            result = FailurePrefix + "неизвестная ошибка";
        }

        if (result.Length <= MaxErrorLength)
        {
            return result;
        }

        return result[..MaxErrorLength].TrimEnd() + "…";
    }

    private static string DisplayPath(string? path, string?[] secrets)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "не задан" : path.Trim();
        var safe = BrowserProviderProbeSanitizer.StripSecrets(trimmed, secrets);
        return string.IsNullOrWhiteSpace(safe) ? "не задан" : safe;
    }

    private static Exception UnwrapAggregate(Exception exception)
    {
        var current = exception;
        while (current is AggregateException aggregate && aggregate.InnerException is not null)
        {
            current = aggregate.InnerException;
        }

        return current;
    }

    private static string FlattenMessages(Exception exception)
    {
        var parts = new List<string>();
        for (var current = UnwrapAggregate(exception); current is not null && parts.Count < 3; current = current.InnerException)
        {
            if (string.IsNullOrWhiteSpace(current.Message))
            {
                continue;
            }

            var message = current.Message.Trim();
            if (!parts.Exists(part => string.Equals(part, message, StringComparison.Ordinal)))
            {
                parts.Add(message);
            }
        }

        return parts.Count == 0 ? string.Empty : string.Join(" → ", parts);
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
