using System.Text.Json;

namespace Orbita.Contracts;

public static class CrmStages
{
    public const string Lead = "Лид";
    public const string Ndz73 = "НДЗ 73";
    public const string Ndz26 = "НДЗ 2.6";
    public const string Substitution = "Подменка";
    public const string Negotiations = "Переговоры";
    public const string Questionnaire = "Анкета";
    public const string Ticket = "Билет";

    public const int MinCount = 1;
    public const int MaxCount = 20;
    public const int MaxNameLength = 64;

    public static readonly IReadOnlyList<string> All =
    [
        Lead, Ndz73, Ndz26, Substitution, Negotiations, Questionnaire, Ticket
    ];

    /// <summary>Default funnel used when an office has no custom stages.</summary>
    public static IReadOnlyList<string> Default => All;

    public static bool IsValid(string? stage) =>
        All.Contains(stage ?? string.Empty, StringComparer.Ordinal);

    public static bool IsValidName(string? stage) =>
        !string.IsNullOrWhiteSpace(stage) && stage.Trim().Length <= MaxNameLength;

    /// <summary>
    /// Normalize a free-form stage list: trim, drop empties, de-dupe (ordinal), enforce limits.
    /// Returns null when the list is empty or exceeds <see cref="MaxCount"/>.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string?>? stages)
    {
        if (stages is null)
        {
            return null;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stages)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            if (name.Length > MaxNameLength || !seen.Add(name))
            {
                continue;
            }

            result.Add(name);
            if (result.Count > MaxCount)
            {
                return null;
            }
        }

        return result.Count < MinCount ? null : result;
    }

    public static IReadOnlyList<string> Resolve(string? stagesJson)
    {
        if (string.IsNullOrWhiteSpace(stagesJson))
        {
            return Default;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(stagesJson);
            var normalized = Normalize(parsed);
            return normalized ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static string Serialize(IReadOnlyList<string> stages) =>
        JsonSerializer.Serialize(stages);

    public static bool Contains(IReadOnlyList<string> stages, string? stage) =>
        stages.Contains(stage ?? string.Empty, StringComparer.Ordinal);
}

public static class CrmTaskStatuses
{
    public const string Open = "Open";
    public const string Completed = "Completed";
}

public static class CrmCloseReasons
{
    public const string Refused = "Отказ";
    public const string Unreachable = "Недозвон";
    public const string Duplicate = "Дубль";
    public const string Success = "Успех";
    public const string Other = "Другое";

    public static readonly IReadOnlyList<string> All =
    [
        Refused, Unreachable, Duplicate, Success, Other
    ];

    public static bool IsValid(string? reason) =>
        All.Contains(reason ?? string.Empty, StringComparer.Ordinal);
}

public static class CrmBoardScopes
{
    public const string Mine = "mine";
    public const string Team = "team";
    public const string Unassigned = "unassigned";
    public const string Closed = "closed";

    public static bool IsValid(string? scope) =>
        scope is null or "" or Mine or Team or Unassigned or Closed;
}

public sealed record CrmBoardQuery(
    string? Search = null,
    string? Scope = null,
    string? City = null,
    string? Vacancy = null,
    bool OverdueOnly = false,
    bool ActiveLoadOnly = false,
    bool IncludeClosed = false);

public sealed record CrmBoardDto(
    bool IsEnabled,
    bool RequireStageComment,
    bool IsShiftActive,
    int Capacity,
    int ActiveLoad,
    IReadOnlyList<CrmStageDto> Stages,
    IReadOnlyList<CrmManagerDto> Managers,
    int UnassignedCount,
    int OpenTaskCount,
    int OverdueTaskCount,
    bool IsAdmin,
    bool CanEdit,
    CrmTeamStatsDto TeamStats,
    string Scope,
    string? Search,
    string? City,
    string? Vacancy,
    bool OverdueOnly,
    bool ActiveLoadOnly,
    bool IncludeClosed,
    IReadOnlyList<string> FunnelStages);

public sealed record CrmStageDto(string Name, IReadOnlyList<CrmCandidateCardDto> Cards, int TotalCount);

public sealed record CrmManagerDto(
    string UserId,
    string DisplayName,
    bool IsShiftActive,
    int Capacity,
    int ActiveLoad);

public sealed record CrmTeamStatsDto(
    int TotalActiveCards,
    int UnassignedCount,
    int OnShiftManagers,
    int TotalManagers,
    int ClosedToday,
    int AssignedToday,
    IReadOnlyList<CrmStageCountDto> StageCounts);

public sealed record CrmStageCountDto(string Stage, int Count);

public sealed record CrmCandidateCardDto(
    Guid Id,
    Guid ResponseId,
    string FullName,
    int? Age,
    string PhoneRaw,
    string City,
    string Vacancy,
    string? MessengerUrl,
    string Stage,
    string? ManagerUserId,
    string? ManagerName,
    bool IsInActiveLoad,
    DateTime CreatedAtUtc,
    DateTime StageChangedAtUtc,
    DateTime? LastContactAtUtc,
    DateTime? NextActionAtUtc,
    bool IsClosed,
    string? CloseReason,
    int OpenTaskCount,
    bool HasOverdueTask,
    double HoursInStage,
    string? SourceUrl,
    string? VacancyUrl,
    string? AccountName,
    string? SourceResponseId);

public sealed record CrmCandidateDetailDto(
    CrmCandidateCardDto Card,
    IReadOnlyList<CrmNoteDto> Notes,
    IReadOnlyList<CrmTaskDto> Tasks,
    IReadOnlyList<CrmHistoryDto> History,
    IReadOnlyList<CrmActivityItemDto> Activity,
    IReadOnlyList<CrmManagerDto> Managers,
    IReadOnlyList<string> Stages,
    bool CanEdit,
    IReadOnlyList<CrmChatMessageDto> Chat,
    IReadOnlyList<CrmPhoneHistoryDto> PhoneHistory);

public sealed record CrmChatMessageDto(
    string Text,
    string TimeLabel,
    string Tone);

public sealed record CrmPhoneHistoryDto(
    string PhoneRaw,
    string PhoneNormalized,
    DateTime RecordedAtUtc);

public sealed record CrmNoteDto(
    Guid Id,
    string AuthorUserId,
    string AuthorName,
    string Text,
    DateTime CreatedAtUtc);

public sealed record CrmTaskDto(
    Guid Id,
    Guid? CardId,
    string? CandidateName,
    string Title,
    string? Description,
    string AssigneeUserId,
    string AssigneeName,
    string CreatorUserId,
    string CreatorName,
    DateTime? DueAtUtc,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    bool IsOverdue);

public sealed record CrmHistoryDto(
    Guid Id,
    string Action,
    string? Details,
    string ActorUserId,
    string ActorName,
    DateTime CreatedAtUtc);

public sealed record CrmActivityItemDto(
    string Kind,
    string Title,
    string? Body,
    string ActorName,
    DateTime AtUtc,
    Guid? TaskId = null);

public sealed record CrmAssignRequest(string ManagerUserId);
public sealed record CrmMoveRequest(string Stage, string? Comment = null);
public sealed record CrmNoteCreateRequest(string Text);
public sealed record CrmTaskCreateRequest(
    Guid? CardId,
    string Title,
    string? Description,
    string AssigneeUserId,
    DateTime? DueAtUtc);
public sealed record CrmFollowUpRequest(int Minutes, string? Title = null);
public sealed record CrmCloseRequest(string Reason, string? Comment = null);
public sealed record CrmCapacityRequest(int Capacity);
public sealed record CrmOfficeSettingsRequest(bool IsEnabled, bool RequireStageComment = false);
public sealed record CrmOfficeSettingsDto(bool IsEnabled, bool RequireStageComment, IReadOnlyList<string> Stages);
public sealed record CrmOfficeFunnelRequest(IReadOnlyList<string> Stages);
