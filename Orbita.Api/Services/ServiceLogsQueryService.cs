using Microsoft.Extensions.Configuration;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

public sealed class ServiceLogsQueryService(IConfiguration configuration)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly Lazy<Logger> aggregateReader = new(() =>
    {
        var sharedRoot = configuration["Logs:SharedRoot"];
        var root = string.IsNullOrWhiteSpace(sharedRoot)
            ? GlobalLogger.ResolveLogsRootDirectory(string.Empty)
            : Path.GetFullPath(sharedRoot);
        return new Logger(Path.Combine(root, "Orbita.Api"), aggregateReadRoot: root);
    });

    public async Task<ServiceLogsPageDto> SearchAsync(
        string? searchText,
        string? level,
        string? service,
        DateTime? date,
        Guid? workerId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var parsedLevel = ParseLevel(level);
        IReadOnlyCollection<DeskLinkAuditLogLevel>? parsedLevels = parsedLevel.HasValue
            ? [parsedLevel.Value]
            : null;
        var filterDate = date?.Date ?? DateTime.UtcNow.Date;
        var entries = await aggregateReader.Value.SearchLogsAsync(
            parsedLevels,
            searchText,
            filterDate,
            workerId.HasValue ? $"worker:{workerId.Value:D}" : null,
            string.IsNullOrWhiteSpace(service) ? null : service.Trim());

        ct.ThrowIfCancellationRequested();

        var total = entries.Count;
        var items = entries
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapEntry)
            .ToList();

        return new ServiceLogsPageDto(items, total, page, pageSize);
    }

    private static ServiceLogEntryDto MapEntry(LogFileEntry entry)
    {
        var level = entry.Level switch
        {
            DeskLinkAuditLogLevel.Error => "Error",
            DeskLinkAuditLogLevel.Warning => "Warning",
            DeskLinkAuditLogLevel.Debug => "Debug",
            _ => "Info"
        };

        return new ServiceLogEntryDto(
            entry.Timestamp,
            level,
            string.IsNullOrWhiteSpace(entry.Service) ? "—" : entry.Service,
            string.IsNullOrWhiteSpace(entry.Prefix) ? "—" : entry.Prefix,
            entry.Message ?? string.Empty,
            string.IsNullOrWhiteSpace(entry.TraceId) ? null : entry.TraceId,
            entry.IsTampered);
    }

    private static DeskLinkAuditLogLevel? ParseLevel(string? level) => level?.Trim() switch
    {
        "Info" => DeskLinkAuditLogLevel.Info,
        "Debug" => DeskLinkAuditLogLevel.Debug,
        "Warning" => DeskLinkAuditLogLevel.Warning,
        "Error" => DeskLinkAuditLogLevel.Error,
        _ => null
    };
}
