using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DashboardEventMapper
{
    private const int MaxMessageLength = 180;
    private const int MaxSubtitleLength = 96;

    public static DashboardEventRowViewModel Map(WorkerEventListItem item)
    {
        var level = NormalizeLevel(item.Level);
        var subtitle = Truncate(BuildSubtitle(item), MaxSubtitleLength);

        if (level == "error")
        {
            var error = ErrorsIndexBuilder.MapEvent(item);
            return new()
            {
                Id = error.Id,
                WorkerId = error.WorkerId,
                Message = Truncate(error.Message, MaxMessageLength),
                Subtitle = subtitle,
                TimeUtc = item.CreatedAtUtc,
                WorkerName = error.WorkerName,
                Level = level,
                LevelLabel = error.SeverityLabel,
                IconClass = "fa-regular fa-circle-xmark",
                IconTone = "error",
                AccountId = error.AccountId,
                AccountName = error.AccountName,
                DetailTitle = error.ErrorTypeLabel,
                DetailSubtitle = $"{error.WorkerName} · {error.SeverityLabel}",
                DetailBody = error.Message,
                CopyText = error.CopyText,
                AttachmentId = error.AttachmentId,
                IsError = true,
                CanSolveCaptcha = error.CanSolveCaptcha,
                CaptchaUrl = error.CaptchaUrl,
                CaptchaKind = string.IsNullOrWhiteSpace(error.CaptchaKind) ? "captcha" : error.CaptchaKind,
                CaptchaSubProfileId = error.CaptchaSubProfileId
            };
        }

        var evt = EventsIndexBuilder.MapEvent(item);
        return new()
        {
            Id = evt.Id,
            WorkerId = evt.WorkerId,
            Message = Truncate(evt.Description, MaxMessageLength),
            Subtitle = subtitle,
            TimeUtc = item.CreatedAtUtc,
            WorkerName = evt.WorkerName,
            Level = level,
            LevelLabel = evt.LevelLabel,
            IconClass = evt.EventTypeIcon,
            IconTone = evt.EventTypeTone,
            AccountId = evt.AccountId,
            AccountName = evt.AccountName,
            DetailTitle = evt.EventTypeLabel,
            DetailSubtitle = $"{evt.WorkerName} · {evt.LevelLabel}",
            DetailBody = evt.Description,
            CopyText = evt.CopyText,
            AttachmentId = evt.AttachmentId,
            IsError = false,
            CanSolveCaptcha = evt.CanSolveCaptcha,
            CaptchaUrl = evt.CaptchaUrl,
            CaptchaKind = string.IsNullOrWhiteSpace(evt.CaptchaKind) ? "captcha" : evt.CaptchaKind,
            CaptchaSubProfileId = evt.CaptchaSubProfileId
        };
    }

    private static string BuildSubtitle(WorkerEventListItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Details) && item.Details.TrimStart().StartsWith('{'))
        {
            var parts = new List<string>();
            var subProfile = WorkerEventDetailsParser.TryParseDiagnosticSubProfileName(item.Details);
            if (!string.IsNullOrWhiteSpace(subProfile))
                parts.Add($"Субпрофиль «{subProfile.Trim()}»");

            if (!string.IsNullOrWhiteSpace(item.AccountDisplayName))
                parts.Add(item.AccountDisplayName);
            else if (item.AccountId.HasValue)
                parts.Add("Аккаунт");

            return parts.Count > 0 ? string.Join(" · ", parts) : string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(item.Details))
            return item.Details.Trim();

        if (!string.IsNullOrWhiteSpace(item.AccountDisplayName))
            return item.AccountDisplayName;

        return item.AccountId.HasValue ? "Аккаунт" : string.Empty;
    }

    private static string NormalizeLevel(string level) =>
        level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error"
        : level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning"
        : level.Equals("Info", StringComparison.OrdinalIgnoreCase) ? "info"
        : "success";

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 1)].TrimEnd() + "…";
    }
}
