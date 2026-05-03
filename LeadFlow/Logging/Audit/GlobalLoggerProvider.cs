using Microsoft.Extensions.Logging;

namespace LeadFlow.Logging.Audit;

public static class GlobalLoggerLoggingBuilderExtensions
{
    public static ILoggingBuilder AddGlobalLoggerProvider(this ILoggingBuilder builder)
    {
        builder.AddProvider(new GlobalLoggerProvider());
        return builder;
    }
}

public sealed class GlobalLoggerProvider : ILoggerProvider
{
    internal static bool TestNextBridgeLogThrows { get; set; }

    public ILogger CreateLogger(string categoryName) => new GlobalLoggerBridge(categoryName);
    public void Dispose() { }

    private sealed class GlobalLoggerBridge : ILogger
    {
        private readonly string _categoryName;

        public GlobalLoggerBridge(string categoryName) => _categoryName = categoryName;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
                return;

            if (exception is not null)
                message = string.IsNullOrWhiteSpace(message) ? exception.ToString() : $"{message}{Environment.NewLine}{exception}";

            var level = MapLevel(logLevel);
            try
            {
                if (TestNextBridgeLogThrows)
                {
                    TestNextBridgeLogThrows = false;
                    throw new InvalidOperationException("auditlog-test");
                }

                GlobalLogger.Instance.LogAsync(
                    message ?? string.Empty,
                    level,
                    memberName: string.Empty,
                    filePath: string.Empty,
                    properties: new Dictionary<string, object?>
                    {
                        ["logger.category"] = _categoryName,
                        ["event.id"] = eventId.Id,
                        ["event.name"] = eventId.Name ?? string.Empty
                    })
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // Не пробрасываем исключения из логгера в бизнес-поток.
            }
        }

        private static DeskLinkAuditLogLevel MapLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => DeskLinkAuditLogLevel.Debug,
            LogLevel.Debug => DeskLinkAuditLogLevel.Debug,
            LogLevel.Information => DeskLinkAuditLogLevel.Info,
            LogLevel.Warning => DeskLinkAuditLogLevel.Warning,
            LogLevel.Error => DeskLinkAuditLogLevel.Error,
            LogLevel.Critical => DeskLinkAuditLogLevel.Error,
            _ => DeskLinkAuditLogLevel.Info
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
