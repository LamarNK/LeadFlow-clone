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
    public const string PreparingToSend = "Готовится к отправке";
    public const string InTransit = "В пути";
    public const string Signing = "На подписании";
    public const string DealSuccessful = "Сделка успешна";

    public const int MinCount = 1;
    public const int MaxCount = 20;
    public const int MaxNameLength = 64;

    public static readonly IReadOnlyList<string> All =
    [
        Lead, Ndz73, Ndz26, Substitution, Negotiations, Questionnaire, Ticket,
        PreparingToSend, InTransit, Signing, DealSuccessful
    ];

    private static readonly IReadOnlyList<string> LegacyDefault =
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
            if (normalized is not null && normalized.SequenceEqual(LegacyDefault, StringComparer.Ordinal))
            {
                return Default;
            }

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

public static class CrmManagerLoadRules
{
    /// <summary>
    /// Cards in the office-specific Robot stage remain assigned and visible,
    /// but do not consume the assigned manager's active capacity.
    /// </summary>
    public const string RobotStage = "Робот";

    public static bool CountsTowardsLoad(string? stage, bool isInActiveLoad, bool isClosed) =>
        isInActiveLoad
        && !isClosed
        && !string.Equals(stage, RobotStage, StringComparison.Ordinal);

    public static bool IsExcludedStage(string? stage) =>
        string.Equals(stage, RobotStage, StringComparison.Ordinal);
}

public static class CrmTaskStatuses
{
    public const string Open = "Open";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public static class CrmTaskImportances
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static readonly IReadOnlyList<string> All = [Low, Medium, High];

    public static bool IsValid(string? importance) =>
        All.Contains(importance ?? string.Empty, StringComparer.Ordinal);

    public static string GetLabel(string? importance) => importance switch
    {
        Low => "Низкая",
        High => "Высокая",
        _ => "Средняя"
    };
}

public static class CrmTaskTypes
{
    public const string Unspecified = "Unspecified";
    public const string Contact = "Contact";
    public const string CallBack = "CallBack";
    public const string FollowUp = "FollowUp";
    public const string BuyTicket = "BuyTicket";
    public const string Send = "Send";
    public const string Meet = "Meet";
    public const string SignContract = "SignContract";
    public const string Decision = "Decision";
    public const string Documents = "Documents";

    public static readonly IReadOnlyList<string> All =
        [Contact, CallBack, FollowUp, BuyTicket, Send, Meet, SignContract, Decision, Documents];

    public static bool IsValid(string? taskType) =>
        All.Contains(taskType ?? string.Empty, StringComparer.Ordinal);

    public static string GetLabel(string? taskType) => taskType switch
    {
        Contact => "Связаться",
        CallBack => "Перезвонить",
        FollowUp => "Дожать",
        BuyTicket => "Купить билет",
        Send => "Отправить",
        Meet => "Встретить",
        SignContract => "Подписать контракт",
        Decision => "Что решил",
        Documents => "Документы",
        _ => "Тип не указан"
    };
}

public static class CrmTaskNotificationKinds
{
    public const string DueIn24Hours = "due_24h";
    public const string DueIn1Hour = "due_1h";
    public const string Overdue = "overdue";
    public const string PhoneChanged = "phone_changed";

    public static bool IsValid(string? kind) =>
        kind is DueIn24Hours or DueIn1Hour or Overdue or PhoneChanged;
}

public static class CrmContactPhoneLimits
{
    public const int MaxPhonesPerPerson = 5;
}

public static class CrmTaskAttachmentLimits
{
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;
}

public static class CrmSuccessDocumentLimits
{
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;
    public const long MaxReportSizeBytes = 200 * 1024 * 1024;
    public const int MaxFilesPerReport = 50;
}

public static class CrmSuccessDocumentCategories
{
    public const string Correspondence = "correspondence";
    public const string Ticket = "ticket";
    public const string TicketReceipt = "ticket_receipt";
    public const string Contract = "contract";
    public const string Relationship = "relationship";
    public const string CandidateDocument = "candidate_document";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        Correspondence,
        Ticket,
        TicketReceipt,
        Contract,
        Relationship,
        CandidateDocument,
        Other
    ];

    public static readonly IReadOnlyList<string> Required =
    [
        Correspondence,
        Ticket,
        TicketReceipt,
        CandidateDocument
    ];

    public static bool IsValid(string? category) =>
        All.Contains(category ?? string.Empty, StringComparer.Ordinal);

    public static string GetLabel(string? category) => category switch
    {
        Correspondence => "Переписка",
        Ticket => "Билеты",
        TicketReceipt => "Чеки на билеты",
        Contract => "Контракт",
        Relationship => "Отношение",
        CandidateDocument => "Документы кандидата",
        Other => "Прочие файлы",
        _ => "Файл отчёта"
    };
}

public static class CrmCloseReasons
{
    public const string NoAnswer = "НДЗ";
    public const string Woman = "Женщина";
    public const string Contract = "Контракт";
    public const string SelectedOthers = "Выбрали других";
    public const string Age = "Возраст";
    public const string Health = "Здоровье";
    public const string AlreadyAtSvo = "Уже был на СВО";
    public const string NotRelevant = "Неактуально";
    public const string Disappeared = "Исчез, слился";
    public const string Officer = "Офицер";
    public const string Success = "Успех";

    public static readonly IReadOnlyList<string> All =
    [
        NoAnswer,
        Woman,
        Contract,
        SelectedOthers,
        Age,
        Health,
        AlreadyAtSvo,
        NotRelevant,
        Disappeared,
        Officer,
        Success
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

public static class CrmBoardViews
{
    public const string Board = "board";
    public const string List = "list";

    public static string Normalize(string? value) =>
        string.Equals(value, List, StringComparison.OrdinalIgnoreCase) ? List : Board;
}

public static class CrmBoardListOptions
{
    public const int DefaultPageSize = 20;
    public static readonly IReadOnlyList<int> PageSizes = [20, 50, 100];

    public static int NormalizePage(int page) => Math.Max(1, page);

    public static int NormalizePageSize(int pageSize) =>
        PageSizes.Contains(pageSize) ? pageSize : DefaultPageSize;
}

public static class CrmBoardSorts
{
    public const string Candidate = "candidate";
    public const string Phone = "phone";
    public const string Vacancy = "vacancy";
    public const string Stage = "stage";
    public const string Manager = "manager";
    public const string Created = "created";
    public const string Changed = "changed";

    public static readonly IReadOnlyList<string> All =
        [Candidate, Phone, Vacancy, Stage, Manager, Created, Changed];

    public static string Normalize(string? value) =>
        All.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? value!.Trim().ToLowerInvariant()
            : Created;

    public static string NormalizeDirection(string? value) =>
        string.Equals(value, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
}

public sealed record CrmBoardQuery(
    string? Search = null,
    string? Scope = null,
    string? City = null,
    string? Vacancy = null,
    bool OverdueOnly = false,
    bool ActiveLoadOnly = false,
    bool IncludeClosed = false,
    string? ManagerUserId = null,
    string? CloseReason = null,
    string? View = null,
    int Page = 1,
    int PageSize = CrmBoardListOptions.DefaultPageSize,
    string? Sort = null,
    string? SortDir = null,
    string? Stage = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    string? CreatedFrom = null,
    string? CreatedTo = null,
    int TimeZoneOffsetMinutes = 0);

/// <summary>
/// CRM analytics filter. The period is a half-open UTC interval: [FromUtc, ToUtc).
/// </summary>
public sealed record CrmAnalyticsQuery(
    DateTime FromUtc,
    DateTime ToUtc,
    Guid? OfficeId = null,
    string? ManagerUserId = null);

public sealed record CrmAnalyticsDto(
    DateTime FromUtc,
    DateTime ToUtc,
    Guid? OfficeId,
    string? ManagerUserId,
    CrmAnalyticsCardMetricsDto Cards,
    IReadOnlyList<CrmAnalyticsCloseReasonDto> CloseReasons,
    IReadOnlyList<CrmAnalyticsOfficeFunnelDto> Funnels,
    IReadOnlyList<CrmAnalyticsManagerOptionDto> ManagerOptions,
    IReadOnlyList<CrmAnalyticsManagerDto> Managers,
    CrmAnalyticsDecompositionDto? Decomposition,
    DateTime GeneratedAtUtc);

public sealed record CrmAnalyticsDecompositionDto(
    int Leads,
    int Contacts,
    int Questionnaires,
    int Tickets,
    int Contracts,
    double ContactConversionPercent,
    double QuestionnaireConversionPercent,
    double TicketConversionPercent,
    double ContractConversionPercent,
    IReadOnlyList<CrmAnalyticsContactBreakdownDto> ContactBreakdown);

public sealed record CrmAnalyticsContactBreakdownDto(
    string Label,
    int Count);

public sealed record CrmAnalyticsCardMetricsDto(
    int Received,
    int Assigned,
    int Active,
    int Closed,
    int SuccessfulClosed,
    double AssignmentRatePercent,
    double CloseRatePercent,
    double SuccessRatePercent,
    double SuccessAmongClosedPercent);

public sealed record CrmAnalyticsCloseReasonDto(
    string Reason,
    int Count,
    double PercentOfClosed);

public sealed record CrmAnalyticsOfficeFunnelDto(
    Guid OfficeId,
    string OfficeName,
    int Received,
    IReadOnlyList<CrmAnalyticsFunnelStageDto> Stages);

public sealed record CrmAnalyticsFunnelStageDto(
    string Stage,
    int Position,
    int CurrentCount,
    int ReachedCount,
    double ConversionFromPreviousPercent,
    double ConversionFromReceivedPercent,
    bool IsArchive = false);

public sealed record CrmAnalyticsManagerOptionDto(
    string UserId,
    string DisplayName,
    Guid OfficeId,
    string OfficeName);

public sealed record CrmAnalyticsManagerDto(
    Guid OfficeId,
    string OfficeName,
    string UserId,
    string DisplayName,
    bool IsShiftActive,
    int Capacity,
    int CurrentAssignedCards,
    int ActiveLoad,
    double CapacityUtilizationPercent,
    int CardsInPeriod,
    int StageChangedCardsInPeriod,
    int StageChangesInPeriod,
    int ClosedCardsInPeriod,
    int SuccessfulClosedCardsInPeriod,
    int TasksTotal,
    int OpenTasks,
    int OverdueTasks);

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
    IReadOnlyList<string> FunnelStages,
    bool DeadlineNotificationsEnabled = false,
    string? ManagerUserId = null,
    string? CloseReason = null,
    string View = CrmBoardViews.Board,
    int Page = 1,
    int PageSize = CrmBoardListOptions.DefaultPageSize,
    int TotalItems = 0,
    string Sort = CrmBoardSorts.Created,
    string SortDir = "desc",
    IReadOnlyList<CrmCandidateCardDto>? ListCards = null,
    string? Stage = null,
    string? CreatedFrom = null,
    string? CreatedTo = null);

public sealed record CrmStageDto(string Name, IReadOnlyList<CrmCandidateCardDto> Cards, int TotalCount);

public sealed record CrmManagerDto(
    string UserId,
    string DisplayName,
    bool IsShiftActive,
    int Capacity,
    int ActiveLoad,
    /// <summary>Начало текущей смены (UTC), если <see cref="IsShiftActive"/>.</summary>
    DateTime? ShiftStartedAtUtc = null,
    /// <summary>Конец последней закрытой смены (UTC) — «когда вышел» / последний раз.</summary>
    DateTime? LastShiftEndedAtUtc = null);

public sealed record CrmTeamStatsDto(
    int TotalActiveCards,
    int UnassignedCount,
    int OnShiftManagers,
    int TotalManagers,
    int ClosedToday,
    int AssignedToday,
    int RedistributedToday,
    int RedistributedNdzToday,
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
    string? SourceResponseId,
    /// <summary>Unread chat messages for the current viewer (0 if none / no chat).</summary>
    int ChatUnreadCount = 0,
    string Citizenship = "");

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
    IReadOnlyList<CrmPhoneHistoryDto> PhoneHistory,
    IReadOnlyList<CrmContactPhoneDto> ContactPhones = null!,
    int ChatUnreadCount = 0,
    IReadOnlyList<CrmTaskCommentDto>? TaskComments = null,
    CrmClientTimeDto? ClientTime = null,
    IReadOnlyList<CrmSuccessDocumentDto>? SuccessDocuments = null,
    string? SuccessContractMissingReason = null);

public sealed record CrmClientTimeDto(
    int UtcOffsetMinutes,
    DateTime LocalTime,
    string TimeZoneLabel,
    string SourceLabel);

public sealed record CrmChatMessageDto(
    string Text,
    string TimeLabel,
    string Tone,
    Guid? Id = null,
    string? Status = null,
    string? StatusLabel = null,
    bool CanCancel = false);

public static class CrmOutboundChatStatuses
{
    public const string Planned = "planned";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const int MaxTextLength = 2000;

    public static string GetLabel(string? status) => status switch
    {
        Planned => "Запланировано",
        Sending => "Отправляется",
        Sent => "Отправлено",
        _ => string.Empty
    };
}

public sealed record CrmChatSendRequest(string Text);

public sealed record CrmPhoneHistoryDto(
    string PhoneRaw,
    string PhoneNormalized,
    DateTime RecordedAtUtc);

public sealed record CrmContactPhoneDto(
    Guid Id,
    string PhoneRaw,
    string PhoneNormalized,
    bool IsPrimary,
    string? Label,
    DateTime CreatedAtUtc);

public sealed record CrmContactPhoneCreateRequest(string PhoneRaw, string? Label = null, bool SetAsPrimary = false);

public sealed record CrmManualCardCreateRequest(
    string FullName,
    string PhoneRaw,
    string? City = null,
    string? Vacancy = null,
    int? Age = null,
    string? Source = null,
    string? SourceResponseId = null,
    string? Stage = null,
    bool AssignToMe = true,
    string? Citizenship = null);

public sealed record CrmManualCardCreateResult(Guid Id);

public sealed record CrmLeadFileImportEntry(
    string FullName,
    string PhoneRaw,
    string? Vacancy,
    int SourceLine);

public sealed record CrmLeadFileImportRequest(
    string FileName,
    IReadOnlyList<CrmLeadFileImportEntry> Entries,
    int DuplicateRowsInFile = 0);

public sealed record CrmLeadFileImportResult(
    int RecognizedCount,
    int CreatedCount,
    int SkippedExistingCount,
    int DuplicateRowsInFile,
    int AssignedCount,
    int ManagersOnShift);

public sealed record CrmNoteDto(
    Guid Id,
    string AuthorUserId,
    string AuthorName,
    string Text,
    DateTime CreatedAtUtc,
    bool IsPinned = false,
    DateTime? UpdatedAtUtc = null,
    bool CanEdit = false,
    bool CanDelete = false,
    bool CanPin = false);

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
    bool IsOverdue,
    string Importance = CrmTaskImportances.Medium,
    string TaskType = CrmTaskTypes.Unspecified,
    DateTime? UpdatedAtUtc = null);

public sealed record CrmTaskCommentDto(
    Guid Id,
    Guid TaskId,
    string AuthorUserId,
    string AuthorName,
    string Text,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc = null,
    bool CanEdit = false,
    bool CanDelete = false);

public sealed record CrmTaskAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string UploadedByName,
    DateTime CreatedAtUtc);

public sealed record CrmSuccessDocumentDto(
    Guid Id,
    string Category,
    string FileName,
    string ContentType,
    long SizeBytes,
    string UploadedByName,
    DateTime CreatedAtUtc);

public sealed record CrmTaskDetailDto(
    CrmTaskDto Task,
    IReadOnlyList<CrmTaskCommentDto> Comments,
    bool CanComplete,
    IReadOnlyList<CrmTaskAttachmentDto> Attachments,
    bool CanManage,
    IReadOnlyList<CrmManagerDto> Managers);

public sealed record CrmTaskNotificationDto(
    Guid Id,
    Guid TaskId,
    Guid? CardId,
    string Kind,
    string TaskTitle,
    string Message,
    DateTime DueAtUtc,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

public sealed record CrmTaskNotificationsDto(
    int UnreadCount,
    IReadOnlyList<CrmTaskNotificationDto> Items,
    bool Enabled = false);

public sealed record CrmTaskNotificationSummaryDto(
    int UnreadCount,
    bool Enabled = false,
    int OpenTaskCount = 0);

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
    Guid? TaskId = null,
    Guid? NoteId = null,
    bool IsPinned = false,
    bool CanEdit = false,
    bool CanDelete = false,
    bool CanPin = false,
    string? CompletionReason = null,
    string? ActionComment = null,
    DateTime? UpdatedAtUtc = null,
    Guid? CallId = null,
    string? CallDirection = null,
    int? CallDurationSeconds = null,
    string? CallRecordingUrl = null,
    bool CallRecordingStored = false,
    string? CallClientPhone = null);

public static class CrmActivityDetails
{
    private const string CommentSeparator = "\nКомментарий: ";

    public static string WithComment(string? details, string comment)
    {
        var normalizedDetails = details?.Trim();
        var normalizedComment = comment.Trim();
        return string.IsNullOrWhiteSpace(normalizedDetails)
            ? $"Комментарий: {normalizedComment}"
            : $"{normalizedDetails}{CommentSeparator}{normalizedComment}";
    }

    public static (string? Details, string? Comment) Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var separatorIndex = value.IndexOf(CommentSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0) return (value.Trim(), null);
        return (
            value[..separatorIndex].Trim(),
            value[(separatorIndex + CommentSeparator.Length)..].Trim());
    }
}

public sealed record CrmAssignRequest(string ManagerUserId);
public sealed record CrmMoveRequest(string Stage, string? Comment = null);

public static class CrmBulkTransitionOperations
{
    public const string Move = "move";
    public const string Close = "close";

    public static bool IsValid(string? operation) => operation is Move or Close;
}

public sealed record CrmBulkAssignRequest(
    IReadOnlyList<Guid> CardIds,
    string ManagerUserId);

public sealed record CrmBulkTransitionRequest(
    IReadOnlyList<Guid> CardIds,
    string Operation,
    string? Stage = null,
    string? CloseReason = null,
    string? Comment = null);

public sealed record CrmBulkActionResult(
    int Requested,
    int Updated,
    int Failed,
    IReadOnlyList<string> Errors);
public sealed record CrmNoteCreateRequest(string Text);
public sealed record CrmNoteUpdateRequest(string Text);
public sealed record CrmNotePinRequest(bool IsPinned);

/// <summary>CRM card field edit. Updates the linked candidate response (+ person sync).</summary>
public sealed record CrmCardUpdateRequest(
    string FullName,
    string PhoneRaw,
    string City,
    string Vacancy,
    int? Age = null,
    string? SourceResponseId = null,
    string? AccountName = null,
    string? SourceUrl = null,
    string? VacancyUrl = null,
    string? MessengerUrl = null,
    string? Citizenship = null);
public sealed record CrmTaskCreateRequest(
    Guid? CardId,
    string Title,
    string? Description,
    string AssigneeUserId,
    DateTime? DueAtUtc,
    string Importance = CrmTaskImportances.Medium,
    string TaskType = CrmTaskTypes.Contact);

public sealed record CrmTaskUpdateRequest(
    string Title,
    string? Description,
    string AssigneeUserId,
    DateTime? DueAtUtc,
    string Importance = CrmTaskImportances.Medium,
    string? TaskType = null);

public sealed record CrmTaskCommentCreateRequest(string Text);
public sealed record CrmTaskCommentUpdateRequest(string Text);
/// <summary>Comment is required by the service; empty/missing body is rejected with 400.</summary>
public sealed record CrmTaskCompleteRequest(string? Comment = null);
public sealed record CrmFollowUpRequest(int Minutes, string? Title = null);
public sealed record CrmCloseRequest(string Reason, string? Comment = null);
public sealed record CrmCapacityRequest(int Capacity);
public sealed record CrmOfficeSettingsRequest(
    bool IsEnabled,
    bool RequireStageComment = true,
    bool? DeadlineNotificationsEnabled = null);

public sealed record CrmOfficeSettingsDto(
    bool IsEnabled,
    bool RequireStageComment,
    IReadOnlyList<string> Stages,
    bool DeadlineNotificationsEnabled = false);
public sealed record CrmOfficeFunnelRequest(IReadOnlyList<string> Stages);
