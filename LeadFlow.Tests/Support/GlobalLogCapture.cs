using System.Linq;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.AdsPower;

namespace LeadFlow.Tests.Support;

internal sealed class CapturedGlobalLog
{
    public required DeskLinkAuditLogLevel Level { get; init; }
    public required string Message { get; init; }
    public string? MemberName { get; init; }
    public string? ErrorKey { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = [];
    public string? SerializedPayload { get; init; }
}

/// <summary>
/// Перехватывает реальные вызовы <see cref="GlobalLogger.Instance.LogAsync"/> (тот же payload, что в журнал).
/// </summary>
internal sealed class GlobalLogCapture : IDisposable
{
    // SemaphoreSlim вместо Monitor: тесты после await возобновляются на другом потоке,
    // а Monitor имеет привязку к потоку и Monitor.Exit из другого потока бросает
    // SynchronizationLockException, оставляя статический лок захваченным навсегда
    // (это зависало все последующие тесты, вызывающие Start()).
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<CapturedGlobalLog> _entries = [];
    private bool _disposed;

    private GlobalLogCapture()
    {
    }

    public IReadOnlyList<CapturedGlobalLog> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public static GlobalLogCapture Start()
    {
        Gate.Wait();
        var capture = new GlobalLogCapture();
        GlobalLogger.TestCapture = capture.OnLog;
        return capture;
    }

    public IReadOnlyList<CapturedGlobalLog> WithCorrelation(string correlationId)
    {
        lock (_sync)
        {
            return _entries.Where(e => Equals(e.Properties.GetValueOrDefault("startup.correlationId"), correlationId)).ToList();
        }
    }

    public IReadOnlyList<CapturedGlobalLog> StartupEvents(string eventName)
    {
        lock (_sync)
        {
            return _entries.Where(e => Equals(e.Properties.GetValueOrDefault("startup.event"), eventName)).ToList();
        }
    }

    public string CombinedBlob(IEnumerable<CapturedGlobalLog>? subset = null)
    {
        IReadOnlyList<CapturedGlobalLog> logs;
        if (subset is null)
        {
            lock (_sync)
            {
                logs = _entries.ToArray();
            }
        }
        else
        {
            logs = subset as IReadOnlyList<CapturedGlobalLog> ?? subset.ToList();
        }

        return string.Join(
            "\n",
            logs.Select(e =>
                e.Message
                + "\n"
                + AdsPowerStartupLogSanitizer.SerializeForInspection(e.Properties)
                + "\n"
                + (e.SerializedPayload ?? string.Empty)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GlobalLogger.TestCapture = null;
        Gate.Release();
    }

    private void OnLog(
        DeskLinkAuditLogLevel level,
        string message,
        string? memberName,
        string? errorKey,
        IReadOnlyDictionary<string, object?> properties,
        string? serializedPayload)
    {
        var entry = new CapturedGlobalLog
        {
            Level = level,
            Message = message,
            MemberName = memberName,
            ErrorKey = errorKey,
            Properties = new Dictionary<string, object?>(properties),
            SerializedPayload = serializedPayload
        };
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }
}
