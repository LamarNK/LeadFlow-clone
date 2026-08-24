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
    private static readonly object Gate = new();
    private readonly List<CapturedGlobalLog> _entries = [];
    private bool _disposed;

    private GlobalLogCapture()
    {
    }

    public IReadOnlyList<CapturedGlobalLog> Entries => _entries;

    public static GlobalLogCapture Start()
    {
        Monitor.Enter(Gate);
        var capture = new GlobalLogCapture();
        GlobalLogger.TestCapture = capture.OnLog;
        return capture;
    }

    public IReadOnlyList<CapturedGlobalLog> WithCorrelation(string correlationId) =>
        _entries.Where(e => Equals(e.Properties.GetValueOrDefault("startup.correlationId"), correlationId)).ToList();

    public IReadOnlyList<CapturedGlobalLog> StartupEvents(string eventName) =>
        _entries.Where(e => Equals(e.Properties.GetValueOrDefault("startup.event"), eventName)).ToList();

    public string CombinedBlob(IEnumerable<CapturedGlobalLog>? subset = null)
    {
        var logs = subset ?? _entries;
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
        Monitor.Exit(Gate);
    }

    private void OnLog(
        DeskLinkAuditLogLevel level,
        string message,
        string? memberName,
        string? errorKey,
        IReadOnlyDictionary<string, object?> properties,
        string? serializedPayload)
    {
        _entries.Add(new CapturedGlobalLog
        {
            Level = level,
            Message = message,
            MemberName = memberName,
            ErrorKey = errorKey,
            Properties = new Dictionary<string, object?>(properties),
            SerializedPayload = serializedPayload
        });
    }
}
