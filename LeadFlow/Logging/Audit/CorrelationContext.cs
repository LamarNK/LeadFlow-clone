using System.Threading;

namespace LeadFlow.Logging.Audit;

/// <summary>
/// Текущий correlation / request id для async-потока (устанавливается <see cref="CorrelationIdMiddleware"/>).
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentHolder = new();

    public static string? Current => CurrentHolder.Value;

    public static void Set(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;
        CurrentHolder.Value = requestId.Trim();
    }

    public static void Clear() => CurrentHolder.Value = null;
}
