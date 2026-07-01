using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DashboardEventMapper
{
    private const int MaxMessageLength = 180;
    private const int MaxSubtitleLength = 96;

    public static DashboardEventRowViewModel Map(WorkerEventListItem item) =>
        new()
        {
            Message = Truncate(WorkerEventDetailsParser.FormatForDisplay(item.Message, item.Details), MaxMessageLength),
            Subtitle = Truncate(BuildSubtitle(item), MaxSubtitleLength),
            TimeUtc = item.CreatedAtUtc,
            WorkerName = item.WorkerDisplayName,
            Level = NormalizeLevel(item.Level)
        };

    private static string BuildSubtitle(WorkerEventListItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Details) && item.Details.TrimStart().StartsWith('{'))
        {
            var parts = new List<string>();
            var subProfile = WorkerEventDetailsParser.TryParseDiagnosticSubProfileName(item.Details);
            if (!string.IsNullOrWhiteSpace(subProfile))
                parts.Add($"Субпрофиль «{subProfile.Trim()}»");

            if (item.AccountId.HasValue)
                parts.Add("Аккаунт");

            return parts.Count > 0 ? string.Join(" · ", parts) : string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(item.Details))
            return item.Details.Trim();

        return item.AccountId.HasValue ? "Аккаунт" : string.Empty;
    }

    private static string NormalizeLevel(string level) =>
        level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error"
        : level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning"
        : "success";

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 1)].TrimEnd() + "…";
    }
}