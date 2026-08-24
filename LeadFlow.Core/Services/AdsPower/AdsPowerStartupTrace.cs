using System.Diagnostics;
using LeadFlow.Core.Logging.Audit;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Корреляция одной попытки старта аккаунта AdsPower. Логи best-effort: методы не бросают
/// и не меняют control flow вызывающего кода.
/// </summary>
internal sealed class AdsPowerStartupTrace : IDisposable
{
    private static readonly AsyncLocal<AdsPowerStartupTrace?> CurrentHolder = new();

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Action<AdsPowerStartupDiagnosticEvent>? _onEvent;
    private readonly List<AdsPowerStartupDiagnosticEvent> _events = [];
    private AdsPowerStartupTrace? _previous;
    private bool _activated;
    private AdsPowerStartupDiagnosticEvent? _failure;

    private AdsPowerStartupTrace(
        string correlationId,
        string adsPowerUserId,
        int attempt,
        Action<AdsPowerStartupDiagnosticEvent>? onEvent)
    {
        CorrelationId = correlationId;
        AdsPowerUserId = adsPowerUserId;
        Attempt = attempt;
        _onEvent = onEvent;
        Stage = "start";
    }

    public static AdsPowerStartupTrace? Current => CurrentHolder.Value;

    public string CorrelationId { get; }

    public string AdsPowerUserId { get; }

    public int Attempt { get; }

    public string Stage { get; private set; }

    public string? LastSuccessfulLocalApiOperation { get; private set; }

    public string? LastSuccessfulCdpOperation { get; private set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public IReadOnlyList<AdsPowerStartupDiagnosticEvent> Events => _events;

    public static AdsPowerStartupTrace Begin(
        string adsPowerUserId,
        int attempt,
        Action<AdsPowerStartupDiagnosticEvent>? onEvent = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new AdsPowerStartupTrace(id, adsPowerUserId, attempt, onEvent);
    }

    public AdsPowerStartupTrace Activate()
    {
        if (_activated)
        {
            return this;
        }

        _previous = CurrentHolder.Value;
        CurrentHolder.Value = this;
        _activated = true;
        return this;
    }

    public void Dispose()
    {
        if (_activated && ReferenceEquals(CurrentHolder.Value, this))
        {
            CurrentHolder.Value = _previous;
        }

        _activated = false;
    }

    public void SetStage(string stage)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(stage))
            {
                Stage = stage;
            }
        }
        catch
        {
            // диагностика не должна ломать старт
        }
    }

    public AdsPowerStartupDiagnosticEvent? RecordLocalApi(AdsPowerLocalApiStartSnapshot snapshot)
    {
        return SafeRecord(() =>
        {
            if (snapshot.Ok)
            {
                LastSuccessfulLocalApiOperation = AdsPowerStartupLogSanitizer.BrowserStartOperation;
            }

            var properties = AdsPowerStartupLogSanitizer.ToProperties(snapshot);
            CopyIdentityTo(properties);
            var message = snapshot.Ok
                ? "AdsPower startup: Local API browser/start завершён."
                : "AdsPower startup: Local API browser/start ошибка.";
            return Complete(
                "browser_start",
                "local_api",
                message,
                snapshot.Ok,
                properties);
        });
    }

    public AdsPowerStartupDiagnosticEvent? RecordCdp(
        string call,
        string operation,
        TimeSpan duration,
        bool ok,
        int? pagesCount = null,
        string? urlClasses = null)
    {
        return SafeRecord(() =>
        {
            if (ok)
            {
                LastSuccessfulCdpOperation = call;
            }

            var properties = new Dictionary<string, object?>
            {
                ["startup.boundary"] = "cdp",
                ["startup.event"] = call == "Connect" ? "cdp_connect" : "pages_async",
                ["cdp.call"] = call,
                ["cdp.operation"] = operation,
                ["cdp.durationMs"] = duration.TotalMilliseconds,
                ["cdp.ok"] = ok,
                ["cdp.pagesCount"] = pagesCount,
                ["cdp.urlClasses"] = urlClasses
            };
            CopyIdentityTo(properties);
            var pages = pagesCount is { } count ? $", pages={count}" : string.Empty;
            var message =
                $"AdsPower startup: CDP {call} ({duration.TotalMilliseconds:F0} мс{pages}).";
            return Complete(
                call == "Connect" ? "cdp_connect" : "pages_async",
                "cdp",
                message,
                ok,
                properties);
        });
    }

    public AdsPowerStartupDiagnosticEvent? RecordFailure(Exception exception)
    {
        return SafeRecord(() =>
        {
            if (exception is OperationCanceledException)
            {
                // Внутренняя отмена (3-мин CTS / HTTP cancel) не занимает единственный
                // failure-event: внешний TimeoutException должен остаться в журнале.
                return null;
            }

            if (_failure is not null)
            {
                return null;
            }

            var properties = new Dictionary<string, object?>
            {
                ["startup.boundary"] = "failure",
                ["startup.event"] = "startup_failure",
                ["startup.lastSuccessfulLocalApi"] = LastSuccessfulLocalApiOperation,
                ["startup.lastSuccessfulCdp"] = LastSuccessfulCdpOperation,
                ["error.type"] = AdsPowerStartupLogSanitizer.DescribeExceptionType(exception),
                ["error.message"] = AdsPowerStartupLogSanitizer.LimitText(exception.Message)
            };
            CopyIdentityTo(properties);
            var message =
                $"AdsPower startup: сбой на этапе «{Stage}», прошло {Elapsed.TotalSeconds:F0} с.";
            _failure = Complete("startup_failure", "failure", message, ok: false, properties);
            return _failure;
        });
    }

    public void CopyIdentityTo(Dictionary<string, object?> properties)
    {
        try
        {
            properties["startup.correlationId"] = CorrelationId;
            properties["startup.attempt"] = Attempt;
            properties["startup.stage"] = Stage;
            properties["startup.elapsedMs"] = _stopwatch.Elapsed.TotalMilliseconds;
            properties["adsPower.userId"] = AdsPowerUserId;
            properties["startup.lastSuccessfulLocalApi"] = LastSuccessfulLocalApiOperation;
            properties["startup.lastSuccessfulCdp"] = LastSuccessfulCdpOperation;
        }
        catch
        {
            // ignore
        }
    }

    private AdsPowerStartupDiagnosticEvent Complete(
        string name,
        string boundary,
        string message,
        bool ok,
        Dictionary<string, object?> properties)
    {
        var evt = new AdsPowerStartupDiagnosticEvent(name, boundary, message, ok, properties);
        _events.Add(evt);
        try
        {
            _onEvent?.Invoke(evt);
        }
        catch
        {
            // sink не должен ломать старт
        }

        return evt;
    }

    private AdsPowerStartupDiagnosticEvent? SafeRecord(Func<AdsPowerStartupDiagnosticEvent> factory)
    {
        try
        {
            return factory();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record AdsPowerStartupDiagnosticEvent(
    string Name,
    string Boundary,
    string Message,
    bool Ok,
    IReadOnlyDictionary<string, object?> Properties);

internal static class AdsPowerStartupDiagnostics
{
    public static void TryLog(
        AdsPowerStartupDiagnosticEvent? evt,
        DeskLinkAuditLogLevel? level = null,
        string? memberName = null)
    {
        if (evt is null)
        {
            return;
        }

        try
        {
            var properties = evt.Properties as Dictionary<string, object?>
                             ?? new Dictionary<string, object?>(evt.Properties);
            _ = GlobalLogger.Instance.LogAsync(
                evt.Message,
                level ?? (evt.Ok ? DeskLinkAuditLogLevel.Info : DeskLinkAuditLogLevel.Warning),
                memberName: memberName ?? "AdsPowerStartup",
                properties: properties);
        }
        catch
        {
            // логирование не меняет control flow
        }
    }

    public static void TryCopyIdentity(Dictionary<string, object?> properties)
    {
        try
        {
            AdsPowerStartupTrace.Current?.CopyIdentityTo(properties);
        }
        catch
        {
            // ignore
        }
    }
}
