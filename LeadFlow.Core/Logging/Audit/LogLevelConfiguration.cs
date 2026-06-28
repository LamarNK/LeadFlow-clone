namespace LeadFlow.Core.Logging.Audit;

/// <summary>
/// Минимальный уровень для записи в audit/file channel и фильтрация до тяжёлых вычислений (LOG_LEVEL).
/// </summary>
public static class LogLevelConfiguration
{
    private static readonly Lazy<int> MinSeverityLazy = new(ComputeMinSeverity);

    /// <summary>
    /// Debug=10, Info=20, Warning=30, Error=40.
    /// </summary>
    public static int Severity(DeskLinkAuditLogLevel level) => level switch
    {
        DeskLinkAuditLogLevel.Debug => 10,
        DeskLinkAuditLogLevel.Info => 20,
        DeskLinkAuditLogLevel.Warning => 30,
        DeskLinkAuditLogLevel.Error => 40,
        _ => 20
    };

    public static bool IsEnabled(DeskLinkAuditLogLevel level) => Severity(level) >= MinSeverityLazy.Value;

    private static int ComputeMinSeverity()
    {
        var raw = Environment.GetEnvironmentVariable("LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var r = raw.Trim().ToLowerInvariant();
            if (r == "none")
                return 1000; // выше любого уровня — ничего не пишем в audit
            return Severity(ParseAuditLogLevel(raw));
        }

        var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                     ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                     ?? "";
        // В production по умолчанию не пишем Debug в файлы.
        return aspEnv.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Severity(DeskLinkAuditLogLevel.Info)
            : Severity(DeskLinkAuditLogLevel.Debug);
    }

    private static DeskLinkAuditLogLevel ParseAuditLogLevel(string raw)
    {
        raw = raw.Trim();
        return raw.ToLowerInvariant() switch
        {
            "trace" or "verbose" or "debug" => DeskLinkAuditLogLevel.Debug,
            "info" or "information" => DeskLinkAuditLogLevel.Info,
            "warn" or "warning" => DeskLinkAuditLogLevel.Warning,
            "error" => DeskLinkAuditLogLevel.Error,
            "fatal" or "critical" => DeskLinkAuditLogLevel.Error,
            _ => DeskLinkAuditLogLevel.Info
        };
    }
}
