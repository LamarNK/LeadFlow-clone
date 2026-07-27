using Orbita.Contracts;

namespace Orbita.Api.Data;

public sealed class OfficeEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RegistrationSecretHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool BitrixTransmissionEnabled { get; set; } = true;
    public string? BitrixWebhookUrlProtected { get; set; }
    public string? BitrixPortalHost { get; set; }
    public string BitrixValidationStatus { get; set; } = BitrixValidationStatuses.NotConfigured;
    public string? BitrixValidationMessage { get; set; }
    public DateTime? BitrixLastValidatedAtUtc { get; set; }
    public DateTime? BitrixUpdatedAtUtc { get; set; }
    public string? BitrixUpdatedByUserId { get; set; }

    public ICollection<WorkerEntity> Workers { get; set; } = [];
    public ICollection<PanelUserProfileEntity> UserProfiles { get; set; } = [];
}

public sealed class PanelUserProfileEntity
{
    public string UserId { get; set; } = string.Empty;
    public Guid? OfficeId { get; set; }

    public OfficeEntity? Office { get; set; }
}

public sealed class WorkerEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string MonitoringStatus { get; set; } = "Stopped";
    public string? MonitoringStatusMessage { get; set; }
    public bool IsMonitoringActive { get; set; }
    public DateTime? NextCycleCheckAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? ApiKeyRotatedAtUtc { get; set; }
    public int MaxConcurrentAccounts { get; set; } = 1;

    /// <summary>Включить фильтры сбора откликов (пол/возраст) на воркере.</summary>
    public bool ResponseFilterEnabled { get; set; }

    /// <summary>Отсекать карточки с женским полом (card/ФИО).</summary>
    public bool ResponseFilterExcludeFemale { get; set; }

    /// <summary>Отсекать карточки с мужским полом (card/ФИО).</summary>
    public bool ResponseFilterExcludeMale { get; set; }

    /// <summary>Устаревший единый макс. возраст; используется как fallback, если раздельные не заданы.</summary>
    public int? ResponseFilterMaxAge { get; set; }

    /// <summary>Макс. возраст мужчин включительно (62 ⇒ отсечь 63+). null — без лимита.</summary>
    public int? ResponseFilterMaxAgeMale { get; set; }

    /// <summary>Макс. возраст женщин включительно. null — без лимита.</summary>
    public int? ResponseFilterMaxAgeFemale { get; set; }

    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    public int? ResponseFilterMaxAgeDays { get; set; }

    /// <summary>Включить пометку откликов по возрастным группам для этого воркера.</summary>
    public bool ResponseHighlightEnabled { get; set; }

    /// <summary>CSV возрастных групп для подсветки на панели, например "45+,63+".</summary>
    public string? ResponseHighlightAgeBuckets { get; set; }

    /// <summary>Автоматически включать/выключать воркер по локальному расписанию сервера.</summary>
    public bool AutoScheduleEnabled { get; set; }

    /// <summary>CSV дней недели Mon..Sun.</summary>
    public string? AutoScheduleDays { get; set; }

    /// <summary>Локальное время старта окна, формат HH:mm.</summary>
    public string? AutoScheduleFromLocalTime { get; set; }

    /// <summary>Локальное время окончания окна, формат HH:mm.</summary>
    public string? AutoScheduleToLocalTime { get; set; }

    /// <summary>Включить автоответ в чатах Avito.</summary>
    public bool MessengerAutoReplyEnabled { get; set; }

    /// <summary>Текст автоответа в чатах Avito.</summary>
    public string? MessengerAutoReplyMessage { get; set; }

    /// <summary>
    /// Через сколько часов без смены номера слать метрику «не менялся».
    /// null — default 24; 0 — не слать стабильность (смену номера всё равно трекаем).
    /// </summary>
    public int? PhoneUnchangedHours { get; set; }

    public double? LastCpuPercent { get; set; }
    public double? LastRamPercent { get; set; }
    public long? LastRamUsedMb { get; set; }
    public long? LastRamTotalMb { get; set; }
    public string? AdsPowerApiBaseUrl { get; set; }
    public string? AdsPowerApiKey { get; set; }
    public string? LastUpdateVersion { get; set; }
    public bool? LastUpdateSuccess { get; set; }
    public string? LastUpdateMessage { get; set; }
    public DateTime? LastUpdateAtUtc { get; set; }
    public string? IpAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public string? AgentVersion { get; set; }
    public string? PendingCommand { get; set; }
    public DateTime? PendingCommandAtUtc { get; set; }
    public string? ActivityPhase { get; set; }
    public string? ActivityMessage { get; set; }
    public Guid? ActivityAccountId { get; set; }
    public string? ActivityAccountName { get; set; }
    public string? ActivitySubProfileId { get; set; }
    public string? ActivitySubProfileName { get; set; }
    public DateTime? ActivityUpdatedAtUtc { get; set; }
    public DateTime? ActivityNextCycleAtUtc { get; set; }
    public string ActivityActiveAccountsJson { get; set; } = "[]";
    public Guid? ActiveCaptchaSessionId { get; set; }
    public string? ActiveCaptchaOperatorUserId { get; set; }
    public string? ActiveCaptchaOperatorDisplayName { get; set; }
    public DateTime? ActiveCaptchaSessionStartedAtUtc { get; set; }

    public OfficeEntity Office { get; set; } = null!;
    public ICollection<WorkerSnapshotEntity> Snapshots { get; set; } = [];
    public ICollection<WorkerAccountEntity> Accounts { get; set; } = [];
    public ICollection<WorkerEventEntity> Events { get; set; } = [];
}

public sealed class WorkerSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string StatsJson { get; set; } = "{}";
    public string BalancesJson { get; set; } = "[]";

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerAccountEntity
{
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AdsPowerProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsEnabledInPanel { get; set; }
    public int ActiveAdsCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public decimal TotalBalance { get; set; }
    public string SubProfilesJson { get; set; } = "[]";
    public DateTime? SubProfilesRefreshedAtUtc { get; set; }
    public DateTime? SubProfilesRefreshRequestedAtUtc { get; set; }
    public string SubProfilesDisabledIdsJson { get; set; } = "[]";
    /// <summary>Логин/телефон Avito (plaintext). Пароль — только в <see cref="AvitoPasswordProtected"/>.</summary>
    public string? AvitoLogin { get; set; }
    /// <summary>Пароль Avito, защищённый Data Protection.</summary>
    public string? AvitoPasswordProtected { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid? AccountId { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDismissed { get; set; }
    public DateTime? DismissedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerDiagnosticAttachmentEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid? AccountId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? PageUrl { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerLogEntryEntity
{
    public long Id { get; set; }
    public Guid WorkerId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    public bool IsTampered { get; set; }
    public DateTime IngestedAtUtc { get; set; }
    public string DedupHash { get; set; } = string.Empty;

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class PanelUserBitrixSettingsEntity
{
    public string UserId { get; set; } = string.Empty;
    public string WebhookUrlProtected { get; set; } = string.Empty;
    public string? PortalHost { get; set; }
    public string ValidationStatus { get; set; } = BitrixValidationStatuses.NotConfigured;
    public string? ValidationMessage { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
}

public sealed class CandidatePersonEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string City { get; set; } = string.Empty;
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public OfficeEntity Office { get; set; } = null!;
    public ICollection<CandidateResponseEntity> Responses { get; set; } = [];
    public ICollection<CandidatePhoneHistoryEntity> PhoneHistory { get; set; } = [];
}

public sealed class CandidatePhoneHistoryEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Guid? ResponseId { get; set; }
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; }

    public CandidatePersonEntity Person { get; set; } = null!;
    public CandidateResponseEntity? Response { get; set; }
}

public sealed class CandidateResponseEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Guid OfficeId { get; set; }
    public Guid? WorkerId { get; set; }
    public string WorkerName { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceResponseId { get; set; } = string.Empty;
    public string CardFingerprint { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string ChatMessagesJson { get; set; } = string.Empty;
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string AvitoSubProfileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsLocalDuplicate { get; set; }
    public bool IsBitrixDuplicate { get; set; }
    public string DuplicateSummary { get; set; } = string.Empty;
    public string BitrixEntityType { get; set; } = string.Empty;
    public string BitrixEntityId { get; set; } = string.Empty;
    public string BitrixContactId { get; set; } = string.Empty;
    public Guid? BitrixInstanceId { get; set; }
    public Guid? DuplicateBitrixInstanceId { get; set; }
    public string DistributionMode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    /// <summary>Дата отклика на Avito (из чата); при отсутствии — совпадает со сбором.</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>Момент сбора отклика воркером / записи в Орбиту.</summary>
    public DateTime CollectedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Метрика номера: <see cref="ResponsePhoneMetricKinds"/>.</summary>
    public string PhoneMetricKind { get; set; } = string.Empty;
    public string PreviousPhoneRaw { get; set; } = string.Empty;
    public string PreviousPhoneNormalized { get; set; } = string.Empty;
    public int? PhoneUnchangedHours { get; set; }
    public DateTime? PhoneChangedAtUtc { get; set; }

    public CandidatePersonEntity Person { get; set; } = null!;
    public OfficeEntity Office { get; set; } = null!;
    public WorkerEntity? Worker { get; set; }
    public BitrixInstanceEntity? BitrixInstance { get; set; }
    public BitrixInstanceEntity? DuplicateBitrixInstance { get; set; }
    public ICollection<ResponseBitrixDeliveryEntity> BitrixDeliveries { get; set; } = [];
}

public sealed class ResponseBitrixDeliveryEntity
{
    public Guid Id { get; set; }
    public Guid ResponseId { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string BitrixEntityId { get; set; } = string.Empty;
    public string BitrixEntityType { get; set; } = string.Empty;
    public string BitrixContactId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public CandidateResponseEntity Response { get; set; } = null!;
    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixInstanceEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string WebhookUrlProtected { get; set; } = string.Empty;
    public string? PortalHost { get; set; }
    public string ValidationStatus { get; set; } = BitrixValidationStatuses.NotConfigured;
    public string? ValidationMessage { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public string IntegrationSettingsJson { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int? LeadExportLimit { get; set; }
    public int LeadExportSessionCount { get; set; }
    public DateTime? LeadExportSessionStartedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }

    public OfficeEntity Office { get; set; } = null!;
}

public sealed class DistributionRouteEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public bool IsAutoDistributionEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }

    public OfficeEntity Office { get; set; } = null!;
    public ICollection<DistributionNodeEntity> Nodes { get; set; } = [];
}

public sealed class DistributionNodeEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public Guid? ParentNodeId { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public int SortOrder { get; set; }
    public double EditorPositionX { get; set; }
    public double EditorPositionY { get; set; }

    public DistributionRouteEntity Route { get; set; } = null!;
    public DistributionNodeEntity? ParentNode { get; set; }
    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
    public ICollection<DistributionNodeEntity> Children { get; set; } = [];
}

public sealed class DistributionRoundRobinStateEntity
{
    public long Id { get; set; }
    public Guid RouteId { get; set; }
    public Guid? ParentNodeId { get; set; }
    public int NextChildIndex { get; set; }

    public DistributionRouteEntity Route { get; set; } = null!;
}

public sealed class CaptchaSessionEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public Guid OfficeId { get; set; }
    public string OperatorUserId { get; set; } = string.Empty;
    public string OperatorDisplayName { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string CaptchaKind { get; set; } = string.Empty;
    public string? SubProfileId { get; set; }
    public string Status { get; set; } = CaptchaSessionStatuses.Pending;
    public int ViewportWidth { get; set; } = CaptchaViewportDefaults.Width;
    public int ViewportHeight { get; set; } = CaptchaViewportDefaults.Height;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureMessage { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class PanelAuditLogEntity
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
