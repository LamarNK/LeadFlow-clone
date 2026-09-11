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
    /// <summary>
    /// Офис принимает отклики в CRM: операторы любого офиса могут отправлять сюда карточки.
    /// </summary>
    public bool CrmEnabled { get; set; }

    /// <summary>Требовать комментарий при смене этапа CRM.</summary>
    public bool CrmRequireStageComment { get; set; }

    /// <summary>Включить персональные напоминания по срокам CRM-задач для этого офиса.</summary>
    public bool CrmDeadlineNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Момент последнего включения напоминаний. Задачи, не изменявшиеся после него,
    /// не рассылаются автоматически, чтобы первый запуск не создал лавину старых уведомлений.
    /// </summary>
    public DateTime? CrmDeadlineNotificationsEnabledAtUtc { get; set; }

    /// <summary>JSON-массив имён этапов воронки офиса. Null/пусто = <see cref="CrmStages.Default"/>.</summary>
    public string? CrmStagesJson { get; set; }
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
    /// <summary>Увеличивается при изменении роли, офиса или прав доступа.</summary>
    public long AccessVersion { get; set; }
    public string? FullName { get; set; }
    public Guid? OfficeId { get; set; }
    /// <summary>UTC-момент последней активности пользователя в веб-панели.</summary>
    public DateTime? LastSeenAtUtc { get; set; }
    public int CrmCapacity { get; set; } = 300;
    public bool CrmShiftActive { get; set; }

    /// <summary>
    /// UTC-момент старта текущей CRM-смены. Null, если смена не активна
    /// или legacy-запись до появления поля (такие смены автозакрываются).
    /// </summary>
    public DateTime? CrmShiftStartedAtUtc { get; set; }

    public DateTime? CrmLastAutoAssignmentAtUtc { get; set; }

    public OfficeEntity? Office { get; set; }
}

/// <summary>
/// Час, в котором пользователь панели был онлайн (UTC, усечённый до часа).
/// Одна строка на пару пользователь+час — для графика пиковой активности.
/// </summary>
public sealed class PanelUserPresenceHourEntity
{
    public string UserId { get; set; } = string.Empty;
    public DateTime HourUtc { get; set; }
}

/// <summary>
/// История CRM-смен менеджера (для аналитики: длительность, забытый стоп и т.п.).
/// Открытая смена: <see cref="EndedAtUtc"/> == null.
/// </summary>
public sealed class CrmManagerShiftEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string ManagerUserId { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Null, пока смена активна.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary><see cref="Orbita.Contracts.CrmShiftEndReasons"/>; null, пока открыта.</summary>
    public string? EndReason { get; set; }

    /// <summary>Кто завершил (userId). Null при автозакрытии / legacy.</summary>
    public string? EndedByUserId { get; set; }

    public OfficeEntity? Office { get; set; }
}

/// <summary>
/// One five-minute collection/distribution session per office and Moscow day.
/// The row also stores the lead round-robin cursor for later arrivals.
/// </summary>
public sealed class CrmDailyDistributionSessionEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public DateOnly LocalDate { get; set; }
    public DateTime FirstShiftStartedAtUtc { get; set; }
    public DateTime DistributeAfterUtc { get; set; }
    public DateTime? DistributedAtUtc { get; set; }
    public string? ManagerRosterJson { get; set; }
    public string? LastLeadManagerUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Number of cards from a distribution pool received by a manager during the
/// business day. Lead counters continue to be used for all later arrivals.
/// </summary>
public sealed class CrmDailyDistributionCounterEntity
{
    public Guid OfficeId { get; set; }
    public DateOnly LocalDate { get; set; }
    public string Pool { get; set; } = CrmDailyDistribution.LeadPool;
    public string ManagerUserId { get; set; } = string.Empty;
    public int AssignedCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkerEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }

    /// <summary>
    /// Optional marker (legacy). Access control is office-based for operators/managers.
    /// </summary>
    public string? OwnerUserId { get; set; }

    /// <summary>Auto-deliver new unique responses into office CRM (cards + manager assign).</summary>
    public bool AutoDeliverToCrm { get; set; }

    /// <summary>Auto-deliver new unique responses into Bitrix via office distribution route.</summary>
    public bool AutoDeliverToBitrix { get; set; } = true;

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
    /// <summary>
    /// Пауза рабочего цикла мониторинга/парсинга откликов. Не влияет на доступ воркера к API.
    /// </summary>
    public bool IsMonitoringPaused { get; set; }

    /// <summary>
    /// Идентификатор активной «аренды» паузы, установленной сессией пополнения.
    /// null — аренды нет (пауза ручная или отсутствует). Авторитетен для восстановления паузы.
    /// </summary>
    public Guid? TopUpPauseLeaseId { get; set; }

    /// <summary>
    /// Версия аренды паузы. Инкрементируется при любой ручной смене паузы, чтобы устаревшая
    /// сессия не могла восстановить паузу после того, как оператор вручную её изменил.
    /// </summary>
    public long TopUpPauseLeaseVersion { get; set; }

    /// <summary>
    /// Состояние паузы до того, как сессия пополнения поставила аренду. Используется для
    /// восстановления при освобождении аренды.
    /// </summary>
    public bool TopUpPauseBaselinePaused { get; set; }

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

    /// <summary>JSON-набор профилей и субпрофилей, отклики из которых нужно подсвечивать.</summary>
    public string? ResponseHighlightTargetsJson { get; set; }

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
    /// Окно наблюдения (часов) после первой отправки номера в Орбиту.
    /// null — default 120 (5 суток); 0 — не наблюдать после первой отправки.
    /// </summary>
    public int? PhoneUnchangedHours { get; set; }

    public double? LastCpuPercent { get; set; }
    public double? LastRamPercent { get; set; }
    public long? LastRamUsedMb { get; set; }
    public long? LastRamTotalMb { get; set; }
    public string? AdsPowerApiBaseUrl { get; set; }
    public string? AdsPowerApiKey { get; set; }
    /// <summary>Ключ RuCaptcha для автопрохождения GeeTest v4 на Avito.</summary>
    public string? RuCaptchaApiKey { get; set; }
    /// <summary>ID группы AdsPower; null — синхронизировать все профили.</summary>
    public string? AdsPowerGroupId { get; set; }
    public string? AdsPowerGroupName { get; set; }
    /// <summary>JSON-список групп AdsPower, последний раз полученный с воркера.</summary>
    public string? AdsPowerGroupsJson { get; set; }
    /// <summary>URL локального launcher Multilogin X.</summary>
    public string? MultiloginLauncherUrl { get; set; }
    /// <summary>URL cloud API Multilogin X.</summary>
    public string? MultiloginCloudApiUrl { get; set; }
    /// <summary>Automation token Multilogin X. Не отдаётся в телеметрию панели.</summary>
    public string? MultiloginAutomationToken { get; set; }
    /// <summary>Путь к chrome.exe / Chromium на машине воркера. Пусто — автопоиск.</summary>
    public string? LocalChromeExecutablePath { get; set; }
    /// <summary>Запускать AdsPower и синхронизировать каталог. Выключение не удаляет аккаунты.</summary>
    public bool AdsPowerEnabled { get; set; } = true;
    /// <summary>Запускать Multilogin и синхронизировать каталог. Выключение не удаляет аккаунты.</summary>
    public bool MultiloginEnabled { get; set; } = true;
    /// <summary>Запускать аккаунты обычного Chrome. Выключение не удаляет аккаунты и папки.</summary>
    public bool LocalChromeEnabled { get; set; } = true;
    /// <summary>Ожидающая проверка AdsPower/Multilogin/Local на воркере.</summary>
    public string? PendingBrowserProviderCheck { get; set; }
    public DateTime? PendingBrowserProviderCheckAtUtc { get; set; }
    /// <summary>Ожидающая немедленная синхронизация каталога AdsPower/Multilogin.</summary>
    public string? PendingBrowserProviderSync { get; set; }
    public DateTime? PendingBrowserProviderSyncAtUtc { get; set; }
    /// <summary>Последние результаты проверки подключения. Без секретов.</summary>
    public string? BrowserProviderChecksJson { get; set; }
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

public sealed class WorkerSettingsTemplateEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NameNormalized { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int MaxConcurrentAccounts { get; set; } = 1;
    public bool ResponseFilterEnabled { get; set; }
    public bool ResponseFilterExcludeFemale { get; set; }
    public bool ResponseFilterExcludeMale { get; set; }
    public int? ResponseFilterMaxAgeMale { get; set; }
    public int? ResponseFilterMaxAgeFemale { get; set; }
    public int? ResponseFilterMaxAgeDays { get; set; }
    public bool ResponseHighlightEnabled { get; set; }
    public string? ResponseHighlightAgeBuckets { get; set; }
    public bool AutoScheduleEnabled { get; set; }
    public string? AutoScheduleDays { get; set; }
    public string? AutoScheduleFromLocalTime { get; set; }
    public string? AutoScheduleToLocalTime { get; set; }
    public bool MessengerAutoReplyEnabled { get; set; }
    public string? MessengerAutoReplyMessage { get; set; }
    public int? PhoneUnchangedHours { get; set; }
    public bool AutoDeliverToCrm { get; set; }
    public bool AutoDeliverToBitrix { get; set; } = true;
    public bool AdsPowerEnabled { get; set; } = true;
    public bool MultiloginEnabled { get; set; } = true;
    public bool LocalChromeEnabled { get; set; } = true;
    public OfficeEntity Office { get; set; } = null!;
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
    public string? AdsPowerGroupId { get; set; }
    public string? AdsPowerGroupName { get; set; }
    public string? MultiloginProfileId { get; set; }
    public string? MultiloginProfileName { get; set; }
    public string? MultiloginFolderId { get; set; }
    /// <summary>Папка User Data обычного Chrome на машине воркера.</summary>
    public string? LocalUserDataDir { get; set; }
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
    /// <summary>HTTP-прокси обычного Chrome. AdsPower/Multilogin не используют эти поля.</summary>
    public bool LocalProxyEnabled { get; set; }
    public string? LocalProxyAddress { get; set; }
    public string? LocalProxyUsername { get; set; }
    /// <summary>Пароль прокси, защищённый Data Protection. Не возвращается в API.</summary>
    public string? LocalProxyPasswordProtected { get; set; }
    /// <summary>Режим скорости и трафика обычного Chrome. AdsPower/Multilogin не используют.</summary>
    public string LocalTrafficMode { get; set; } = "Normal";
    public bool LocalBlockMedia { get; set; }
    public bool LocalBlockAnalytics { get; set; }
    public bool LocalBlockImages { get; set; }
    public bool LocalBlockFonts { get; set; }
    public bool LocalBlockPrefetch { get; set; }
    public int LocalNavigationTimeoutSeconds { get; set; } = 60;
    public int? LocalTrafficLastNavigationMs { get; set; }
    public int LocalTrafficBlockedMedia { get; set; }
    public int LocalTrafficBlockedImages { get; set; }
    public int LocalTrafficBlockedFonts { get; set; }
    public int LocalTrafficBlockedAnalytics { get; set; }
    public int LocalTrafficBlockedPrefetch { get; set; }
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

/// <summary>Один проход аккаунта (цикл субпрофилей) — типизированный журнал вместо разбора логов.</summary>
public sealed class MonitoringCycleRunEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string Status { get; set; } = MonitoringCycleRunStatuses.Running;
    public DateTime IngestedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
    public ICollection<MonitoringSubProfileRunEntity> SubProfileRuns { get; set; } = [];
}

/// <summary>Проход одного субпрофиля внутри <see cref="MonitoringCycleRunEntity"/>.</summary>
public sealed class MonitoringSubProfileRunEntity
{
    public Guid Id { get; set; }
    public Guid CycleRunId { get; set; }
    public string SubProfileId { get; set; } = string.Empty;
    public string SubProfileName { get; set; } = string.Empty;
    public int Position { get; set; }
    public int Total { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Outcome { get; set; } = MonitoringSubProfileRunOutcomes.Started;
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public int FoundCount { get; set; }
    public int PublishedCount { get; set; }
    public int DeferredCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    /// <summary>Новые отклики, впервые собранные в этом проходе (без повторных публикаций).</summary>
    public int CollectedCount { get; set; }
    public int WatchRefreshedCount { get; set; }
    public int PhoneChangedCount { get; set; }
    public int CaptchaCount { get; set; }
    public int CaptchaSolvedCount { get; set; }
    /// <summary>В ходе прохода воркер запустил восстановление авторизации Avito.</summary>
    public bool LoginAttempted { get; set; }
    /// <summary>Запущенное восстановление авторизации завершилось успехом.</summary>
    public bool LoginSucceeded { get; set; }

    public MonitoringCycleRunEntity CycleRun { get; set; } = null!;
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

    /// <summary>Optional CRM office; null = collection pool (global person match).</summary>
    public Guid? OfficeId { get; set; }

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

    public OfficeEntity? Office { get; set; }
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

public sealed class CandidatePhoneWatchEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FullNameKey { get; set; } = string.Empty;
    public Guid PersonId { get; set; }
    public Guid? CanonicalResponseId { get; set; }
    public string PublishedSourceResponseId { get; set; } = string.Empty;
    public string CurrentPhoneRaw { get; set; } = string.Empty;
    public string CurrentPhoneNormalized { get; set; } = string.Empty;
    public string LastPublishedPhoneNormalized { get; set; } = string.Empty;
    public DateTime PhoneFirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime WatchStartedUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string State { get; set; } = CandidatePhoneWatchStates.Open;
    public string MessengerUrl { get; set; } = string.Empty;
    public string ChatMessagesJson { get; set; } = string.Empty;
    public string ChatFingerprint { get; set; } = string.Empty;
    public string ProfileFingerprint { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
    public CandidatePersonEntity Person { get; set; } = null!;
    public CandidateResponseEntity? CanonicalResponse { get; set; }
}

public static class CandidatePhoneWatchStates
{
    public const string Open = "Open";
    public const string Changed = "Changed";
    public const string Expired = "Expired";
}

public sealed class CandidateResponseEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }

    /// <summary>
    /// CRM / delivery office. Null while response stays in the collection pool
    /// (before successful send to CRM or Bitrix office).
    /// </summary>
    public Guid? OfficeId { get; set; }

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
    public string Citizenship { get; set; } = string.Empty;
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string? AvatarContentType { get; set; }
    public byte[]? AvatarImage { get; set; }
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

    /// <summary>
    /// Список полей, которые оператор правил вручную (<see cref="Orbita.Contracts.ResponseOperatorLocks"/>).
    /// Повторный ingest с Avito эти поля не затирает.
    /// </summary>
    public string OperatorLockedFields { get; set; } = string.Empty;

    public CandidatePersonEntity Person { get; set; } = null!;
    public OfficeEntity? Office { get; set; }
    public WorkerEntity? Worker { get; set; }
    public BitrixInstanceEntity? BitrixInstance { get; set; }
    public BitrixInstanceEntity? DuplicateBitrixInstance { get; set; }
    public ICollection<ResponseBitrixDeliveryEntity> BitrixDeliveries { get; set; } = [];
    public ICollection<ResponseCrmDeliveryEntity> CrmDeliveries { get; set; } = [];
}

/// <summary>CRM channel delivery attempt for a collected response.</summary>
public sealed class ResponseCrmDeliveryEntity
{
    public Guid Id { get; set; }
    public Guid ResponseId { get; set; }
    public Guid OfficeId { get; set; }
    public Guid? CardId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public CandidateResponseEntity Response { get; set; } = null!;
    public OfficeEntity Office { get; set; } = null!;
    public CrmCandidateCardEntity? Card { get; set; }
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
    /// <summary>Soft-delete: hidden from settings/selection, kept for delivery history.</summary>
    public DateTime? DeletedAtUtc { get; set; }
    public int? LeadExportLimit { get; set; }
    public int LeadExportSessionCount { get; set; }
    public DateTime? LeadExportSessionStartedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }

    public OfficeEntity Office { get; set; } = null!;

    public bool IsDeleted => DeletedAtUtc is not null;
}

public sealed class BitrixWorkforceConfigurationEntity
{
    public Guid BitrixInstanceId { get; set; }
    public string OperationMode { get; set; } = BitrixWorkforceDistribution.DisabledMode;
    public int DealCategoryId { get; set; }
    public string TimeZoneId { get; set; } = "Europe/Moscow";
    public int MorningWindowStartMinutes { get; set; } = 480;
    public int MorningWindowEndMinutes { get; set; } = 660;
    public int LateJoinReserveMinutes { get; set; } = 120;
    public decimal SingleManagerInitialReleasePercent { get; set; } = 50m;
    public int RetryDelaySeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 20;
    public bool PreserveManualNewOwner { get; set; } = true;
    public bool SyncContactOwner { get; set; } = true;
    public bool FillOnlyEmptyAvitoFields { get; set; } = true;
    public bool WriterRulesConfirmed { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceStageRuleEntity
{
    public Guid Id { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string SourceStageId { get; set; } = string.Empty;
    public string TargetStageId { get; set; } = string.Empty;
    public bool UsesMorningWindow { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceManagerEntity
{
    public Guid Id { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public long BitrixUserId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceEventCredentialEntity
{
    public Guid BitrixInstanceId { get; set; }
    public Guid PublicId { get; set; }
    public string ApplicationTokenHash { get; set; } = string.Empty;
    public string? ExpectedMemberId { get; set; }
    public DateTime ConfiguredAtUtc { get; set; }
    public DateTime? LastAcceptedAtUtc { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixDealEventInboxEntity
{
    public long Id { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public long DealId { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public string State { get; set; } = BitrixWorkforceInboxStates.Pending;
    public int AttemptCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LockOwner { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceCursorEntity
{
    public Guid BitrixInstanceId { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string OperationMode { get; set; } = BitrixWorkforceDistribution.WriterMode;
    public long? LastAssignedBitrixUserId { get; set; }
    public DateTime? LastAssignedAtUtc { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceDealStateEntity
{
    public Guid BitrixInstanceId { get; set; }
    public long DealId { get; set; }
    public string? ActiveScenario { get; set; }
    public DateTime? ActiveScenarioShadowHandledAtUtc { get; set; }
    public DateTime? ActiveScenarioWriterHandledAtUtc { get; set; }
    public string? LastObservedStageId { get; set; }
    public string? LastAppliedStageId { get; set; }
    public long? LastAppliedResponsibleId { get; set; }
    public Guid? LastAssignmentId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceMorningStateEntity
{
    public Guid BitrixInstanceId { get; set; }
    public DateOnly LocalDate { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string OperationMode { get; set; } = BitrixWorkforceDistribution.WriterMode;
    public long? FirstManagerId { get; set; }
    public DateTime? FirstManagerSeenAtUtc { get; set; }
    public DateTime? ReserveUntilUtc { get; set; }
    public int InitialReleaseLimit { get; set; }
    public int InitialReleasedCount { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
}

public sealed class BitrixWorkforceAssignmentEntity
{
    public Guid Id { get; set; }
    public long InboxId { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public long DealId { get; set; }
    public long? ContactId { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string OperationMode { get; set; } = string.Empty;
    public string? FromStageId { get; set; }
    public string? ToStageId { get; set; }
    public long? PreviousResponsibleId { get; set; }
    public long? SelectedResponsibleId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConfigurationRevisionAtUtc { get; set; }
    public DateTime? DealAppliedAtUtc { get; set; }
    public DateTime? ContactsAppliedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string? Error { get; set; }

    public BitrixInstanceEntity BitrixInstance { get; set; } = null!;
    public BitrixDealEventInboxEntity Inbox { get; set; } = null!;
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

/// <summary>
/// Сессия ручного пополнения баланса одного аккаунта воркера. Запрошена оператором,
/// исполняется воркером в отдельной фазе автоматизации; банковскую оплату Орбита не видит,
/// поэтому оператор отмечает paid либо закрывает/отменяет сессию. Иначе истекает по TTL.
/// </summary>
public sealed class TopUpSessionEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string SubProfileId { get; set; } = string.Empty;
    public string SubProfileName { get; set; } = string.Empty;
    public Guid OfficeId { get; set; }
    public string OperatorUserId { get; set; } = string.Empty;
    public string OperatorDisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = TopUpSessionStatuses.Requested;
    public decimal CurrentBalance { get; set; }
    public decimal TargetBalance { get; set; }
    public decimal RequestedAmount { get; set; }
    public int DailyResponseCount { get; set; }

    /// <summary>
    /// Снимок версии аренды паузы воркера, зафиксированный атомарно при создании сессии.
    /// Используется при claim оплаты: если текущая версия воркера отличается — пауза была
    /// изменена вручную после создания сессии, и оплата должна быть отклонена.
    /// </summary>
    public long ExpectedPauseLeaseVersion { get; set; }

    /// <summary>
    /// Признак, что сессия приобрела аренду паузы при создании (воркер был не на паузе).
    /// Если true — при claim оплаты требуется, чтобы текущий TopUpPauseLeaseId == Id сессии.
    /// </summary>
    public bool OwnsPauseLease { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? PaymentClaimedAtUtc { get; set; }
    public DateTime? QrReadyAtUtc { get; set; }
    public DateTime? AwaitingBalanceAtUtc { get; set; }
    public DateTime? BalanceConfirmedAtUtc { get; set; }
    public decimal? BalanceAfter { get; set; }
    public string? QrImageBase64 { get; set; }
    public string? QrImageUrl { get; set; }
    public string? FailureMessage { get; set; }

    /// <summary>Текущий шаг сценария для UI оператора (не влияет на автомат статусов).</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Версия строки (PostgreSQL xmin) для оптимистичной блокировки при обновлении статуса.
    /// </summary>
    public uint RowVersion { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class CrmCandidateCardEntity
{
    public Guid Id { get; set; }
    public Guid ResponseId { get; set; }
    public Guid OfficeId { get; set; }
    public string Stage { get; set; } = CrmStages.Lead;
    public string? ManagerUserId { get; set; }
    public string? InitialManagerUserId { get; set; }
    public DateTime? InitialAssignedAtUtc { get; set; }
    public Guid? InitialAssignedOfficeId { get; set; }
    public bool IsInActiveLoad { get; set; } = true;
    /// <summary>When the card actually entered Orbita CRM; source creation time may be older.</summary>
    public DateTime? EnteredCrmAtUtc { get; set; }
    public Guid? EntryOfficeId { get; set; }
    public string? EntryStage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime StageChangedAtUtc { get; set; }
    public DateTime? LastContactAtUtc { get; set; }
    public DateTime? NextActionAtUtc { get; set; }
    public bool IsClosed { get; set; }
    public string? CloseReason { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? SuccessContractMissingReason { get; set; }
    public CandidateResponseEntity Response { get; set; } = null!;
}

public sealed class CrmCandidateNoteEntity
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string AuthorUserId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class CrmTaskEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public Guid? CardId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string AssigneeUserId { get; set; } = string.Empty;
    public string CreatorUserId { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public DateTime? DueAtUtc { get; set; }
    public string Importance { get; set; } = CrmTaskImportances.Medium;
    public string TaskType { get; set; } = CrmTaskTypes.Unspecified;
    public string Status { get; set; } = CrmTaskStatuses.Open;
    public Guid ReminderVersion { get; set; } = Guid.NewGuid();
    public DateTime ReminderVersionChangedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class CrmTaskNotificationEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public Guid TaskId { get; set; }
    public Guid ReminderVersion { get; set; }
    public string RecipientUserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTime DueAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime? DismissedAtUtc { get; set; }
}

public sealed class CrmTaskCommentEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string AuthorUserId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class CrmTaskAttachmentEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string UploadedByUserId { get; set; } = string.Empty;
    public string UploadedByName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CrmSuccessDocumentEntity
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string UploadedByUserId { get; set; } = string.Empty;
    public string UploadedByName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CrmCandidateHistoryEntity
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    /// <summary>Structured target of assignment events; null for other history actions.</summary>
    public string? TargetUserId { get; set; }
    /// <summary>Immutable event context. Null on legacy history: do not infer from today's owner.</summary>
    public Guid? OfficeId { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? PreviousUserId { get; set; }
    public string? StageAtEvent { get; set; }
    public string? PreviousCloseReason { get; set; }
    public bool ContextInferred { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Provider callback configuration for an office telephony integration.</summary>
public sealed class CrmTelephonyWebhookEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public Guid PublicId { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public string? ProviderClientId { get; set; }
    public string? ProviderAccessTokenProtected { get; set; }
    public string? SipAccountProtected { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One provider cabinet connected to an office. A cabinet may contain many public
/// numbers and SIP accounts and is synchronized independently from other cabinets.
/// </summary>
public sealed class CrmTelephonyProviderAccountEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ExternalAccountId { get; set; }
    public string? AccessTokenProtected { get; set; }
    public string OwnedNumbersJson { get; set; } = "[]";
    public Guid PublicId { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime SyncFromUtc { get; set; }
    public DateTime? SyncCursorUtc { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }
    public string SyncStatus { get; set; } = "pending";
    public string? LastSyncError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Maps a cabinet-side SIP identity or extension to an Orbita user.</summary>
public sealed class CrmTelephonyProviderAccountBindingEntity
{
    public Guid Id { get; set; }
    public Guid ProviderAccountId { get; set; }
    public string ProviderUserKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Maps a provider-side SIP identity (for example extension 201) to an Orbita user.</summary>
public sealed class CrmTelephonyUserBindingEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserKey { get; set; } = string.Empty;
    public string OutboundProvider { get; set; } = CrmTelephonyOutboundProviders.Default;
    public string? WebRtcAuthorizationUsername { get; set; }
    public string? WebRtcPasswordProtected { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A completed provider call attached to a CRM card by normalized client phone.</summary>
public sealed class CrmCallEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public Guid? CardId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public Guid? ProviderAccountId { get; set; }
    public string ExternalCallId { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string CallerPhone { get; set; } = string.Empty;
    public string CalledPhone { get; set; } = string.Empty;
    public string ClientPhoneNormalized { get; set; } = string.Empty;
    public string? ProviderUserKey { get; set; }
    public string? ManagerUserId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public int DurationSeconds { get; set; }
    public string Status { get; set; } = CrmCallStatuses.Unknown;
    public string? Disposition { get; set; }
    public string? DialStatus { get; set; }
    public int? HangupCause { get; set; }
    public string? RecordingUrl { get; set; }
    public string? RecordingStoragePath { get; set; }
    public string? RecordingContentType { get; set; }
    public string? RecordingFileName { get; set; }
    public int RecordingFetchAttempts { get; set; }
    public DateTime? NextRecordingFetchAtUtc { get; set; }
    public int RecordingArchiveAttempts { get; set; }
    public DateTime? NextRecordingArchiveAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Provider-independent transcription and quality analysis for a stored CRM call.</summary>
public sealed class CrmCallAiInsightEntity
{
    public Guid CallId { get; set; }
    public string Status { get; set; } = CrmCallAiStatuses.Pending;
    public string? TranscriptText { get; set; }
    public string? SegmentsJson { get; set; }
    public string? AnalysisJson { get; set; }
    public string? AnalysisRawText { get; set; }
    public double? Score { get; set; }
    public string PromptVersion { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? TranscribedAtUtc { get; set; }
    public DateTime? AnalyzedAtUtc { get; set; }
    public CrmCallEntity Call { get; set; } = null!;
}

/// <summary>Additional active contact phones for a candidate person (beyond primary on response).</summary>
public sealed class CandidateContactPhoneEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }
    public CandidatePersonEntity Person { get; set; } = null!;
}

/// <summary>Сообщение менеджера в чат Avito: сначала в очереди, затем отправлено воркером.</summary>
public sealed class CrmOutboundChatMessageEntity
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public Guid ResponseId { get; set; }
    public string AuthorUserId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = CrmOutboundChatStatuses.Planned;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? DeliveryClaimedByWorkerId { get; set; }
    public DateTime? DeliveryClaimedAtUtc { get; set; }
}

/// <summary>Per-user CRM chat read watermark for a card.</summary>
public sealed class CrmCardChatReadEntity
{
    public Guid CardId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime LastReadAtUtc { get; set; }
    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>Non-task desk alerts (phone change, etc.).</summary>
public sealed class CrmDeskAlertEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string RecipientUserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid? CardId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
