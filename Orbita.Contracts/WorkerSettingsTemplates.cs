namespace Orbita.Contracts;

public static class WorkerSettingsTemplateRules
{
    public const int MaxNameLength = 200;

    public static (string? Name, string? Error) NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "Название шаблона обязательно.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            return (null, $"Название шаблона не должно превышать {MaxNameLength} символов.");
        }

        return (trimmed, null);
    }

    public static string NormalizeKey(string name) => name.ToLowerInvariant();
}

public sealed record WorkerSettingsTemplatePayload(
    int MaxConcurrentAccounts = 1,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    int? PhoneUnchangedHours = null,
    bool AutoDeliverToCrm = false,
    bool AutoDeliverToBitrix = true,
    bool AdsPowerEnabled = true,
    bool MultiloginEnabled = true,
    bool LocalChromeEnabled = true)
{
    public static WorkerSettingsTemplatePayload Normalize(WorkerSettingsTemplatePayload? source)
    {
        source ??= new();
        var filters = ResponseCollectionFilters.Normalize(
            source.ResponseFilterEnabled,
            source.ResponseFilterExcludeFemale,
            source.ResponseFilterExcludeMale,
            source.ResponseFilterMaxAgeMale,
            source.ResponseFilterMaxAgeFemale,
            source.ResponseFilterMaxResponseAgeDays);
        var buckets = ResponseHighlightRules.NormalizeBucketsCsv(source.ResponseHighlightAgeBuckets);
        var days = WorkerScheduleRules.NormalizeDaysCsv(source.AutoScheduleDays);
        var from = WorkerScheduleRules.NormalizeTime(source.AutoScheduleFromLocalTime);
        var to = WorkerScheduleRules.NormalizeTime(source.AutoScheduleToLocalTime);
        var message = NormalizeAutoReplyMessage(source.MessengerAutoReplyMessage);

        return source with
        {
            MaxConcurrentAccounts = Math.Max(1, source.MaxConcurrentAccounts),
            ResponseFilterEnabled = filters.Enabled,
            ResponseFilterExcludeFemale = filters.ExcludeFemale,
            ResponseFilterExcludeMale = filters.ExcludeMale,
            ResponseFilterMaxAgeMale = filters.MaxAgeMaleInclusive,
            ResponseFilterMaxAgeFemale = filters.MaxAgeFemaleInclusive,
            ResponseFilterMaxResponseAgeDays = filters.EffectiveMaxResponseAgeDays,
            ResponseHighlightAgeBuckets = string.IsNullOrWhiteSpace(buckets) ? null : buckets,
            AutoScheduleDays = string.IsNullOrWhiteSpace(days) ? null : days,
            AutoScheduleFromLocalTime = from,
            AutoScheduleToLocalTime = to,
            MessengerAutoReplyMessage = message,
            PhoneUnchangedHours = ResponsePhoneWatchRules.ClampUnchangedHours(source.PhoneUnchangedHours)
        };
    }

    private static string? NormalizeAutoReplyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        return trimmed.Length > 2000 ? trimmed[..2000] : trimmed;
    }
}

public sealed record WorkerSettingsTemplateDto(
    Guid Id,
    Guid OfficeId,
    string Name,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    WorkerSettingsTemplatePayload Settings);

public sealed record CreateWorkerSettingsTemplateRequest(
    string Name,
    WorkerSettingsTemplatePayload Settings);

public sealed record UpdateWorkerSettingsTemplateRequest(
    string Name,
    WorkerSettingsTemplatePayload Settings);

public sealed record WorkerSettingsTemplateMutationResultDto(
    WorkerSettingsTemplateDto? Template,
    IReadOnlyList<WorkerSettingsTemplateDto> Templates,
    string? Message = null);
