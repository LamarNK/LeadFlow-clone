namespace Orbita.Contracts;

public sealed record CreateWorkerRequest(string DisplayName, Guid? OfficeId = null);

public sealed record CreateWorkerResponse(Guid WorkerId, string ApiKey, string DisplayName);

public sealed record WorkerSystemMetricsDto(
    double CpuPercent,
    double RamPercent,
    long RamUsedMb,
    long RamTotalMb);

public sealed record WorkerAccountConfigDto(
    Guid AccountId,
    string AdsPowerProfileId,
    string DisplayName,
    bool IsEnabled,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    DateTime? SubProfilesRefreshRequestedAtUtc = null,
    IReadOnlyList<string>? DisabledSubProfileIds = null,
    string? Status = null,
    string? LastErrorMessage = null,
    DateTime? LastMonitoringAtUtc = null,
    DateTime? LastAuthCheckAtUtc = null,
    string? SubProfilesJson = null,
    DateTime? SubProfilesRefreshedAtUtc = null,
    int ActiveAdsCount = 0,
    int BlockedCount = 0,
    int DraftsCount = 0,
    /// <summary>Логин/телефон Avito для автологина воркера (только worker config, не в телеметрии панели).</summary>
    string? AvitoLogin = null,
    /// <summary>Пароль Avito (plaintext только в защищённом worker config channel).</summary>
    string? AvitoPassword = null,
    /// <summary>AdsPower, Multilogin или Local. Пусто — AdsPower (обратная совместимость).</summary>
    string? ProfileProvider = null,
    string? MultiloginProfileId = null,
    string? MultiloginFolderId = null,
    /// <summary>Папка User Data обычного Chrome на машине воркера. Только для ProfileProvider=Local.</summary>
    string? LocalUserDataDir = null,
    /// <summary>Включён HTTP-прокси обычного Chrome. Только Local.</summary>
    bool LocalProxyEnabled = false,
    /// <summary>Адрес HTTP-прокси <c>host:port</c>. Без схемы и без userinfo.</summary>
    string? LocalProxyAddress = null,
    string? LocalProxyUsername = null,
    /// <summary>Пароль прокси. Только worker config channel, только Local и только когда прокси включён.</summary>
    string? LocalProxyPassword = null,
    /// <summary>Режим скорости и трафика: Normal, Economic, Aggressive, Custom. Только Local.</summary>
    string? LocalTrafficMode = null,
    bool LocalBlockMedia = false,
    bool LocalBlockAnalytics = false,
    bool LocalBlockImages = false,
    bool LocalBlockFonts = false,
    bool LocalBlockPrefetch = false,
    int LocalNavigationTimeoutSeconds = 60);

public sealed record UpdateWorkerSubProfileRequest(bool IsEnabledInPanel);

public static class WorkerCommands
{
    public const string Restart = "restart";
    public const string Pause = "pause";
}

public sealed record WorkerCommandRequest(string Command);

public sealed record WorkerUpdateOfferDto(
    string Version,
    string DownloadPath,
    string Sha256,
    long FileSize,
    string? ReleaseNotes);

public sealed record WorkerConfigDto(
    Guid WorkerId,
    int MaxConcurrentAccounts,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    IReadOnlyList<WorkerAccountConfigDto> Accounts,
    string? PendingCommand = null,
    WorkerUpdateOfferDto? UpdateOffer = null,
    WorkerPendingCaptchaSessionDto? PendingCaptchaSession = null,
    WorkerPendingBrowserMonitorSessionDto? PendingBrowserMonitorSession = null,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    int? ResponseFilterMaxAge = null,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    /// <summary>
    /// Через сколько часов без смены номера отправлять метрику «не менялся».
    /// null — default 24; 0 — не отправлять метрику стабильности (смену номера всё равно трекаем).
    /// </summary>
    int? PhoneUnchangedHours = null,
    /// <summary>JSON-набор профилей и субпрофилей, отклики из которых нужно подсвечивать.</summary>
    string? ResponseHighlightTargetsJson = null,
    /// <summary>ID группы AdsPower; null — синхронизировать все профили Local API.</summary>
    string? AdsPowerGroupId = null,
    /// <summary>Ключ RuCaptcha для автопрохождения GeeTest v4. Пусто — выкл.</summary>
    string? RuCaptchaApiKey = null,
    /// <summary>URL launcher Multilogin X (worker config channel).</summary>
    string? MultiloginLauncherUrl = null,
    /// <summary>Automation token Multilogin X. Только worker config, не телеметрия панели.</summary>
    string? MultiloginAutomationToken = null,
    /// <summary>URL cloud API Multilogin X (worker config channel).</summary>
    string? MultiloginCloudApiUrl = null,
    /// <summary>Путь к chrome.exe / Chromium на машине воркера. Пусто — автопоиск.</summary>
    string? LocalChromeExecutablePath = null,
    /// <summary>Запускать и синхронизировать AdsPower. По умолчанию включено.</summary>
    bool AdsPowerEnabled = true,
    /// <summary>Запускать и синхронизировать Multilogin. По умолчанию включено.</summary>
    bool MultiloginEnabled = true,
    /// <summary>Запускать аккаунты обычного Chrome. По умолчанию включено.</summary>
    bool LocalChromeEnabled = true,
    WorkerPendingBrowserProviderCheckDto? PendingProviderCheck = null,
    WorkerPendingBrowserProviderSyncDto? PendingProviderSync = null,
    WorkerPendingLocalChromeLoginDto? PendingLocalChromeLogin = null)
{
    public bool ShouldSyncAdsPowerCatalog => AdsPowerEnabled;

    public bool ShouldSyncMultiloginCatalog => MultiloginEnabled;

    public bool IsBrowserProviderEnabled(WorkerAccountConfigDto account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.IsNullOrWhiteSpace(account.MultiloginProfileId))
        {
            return MultiloginEnabled;
        }

        if (!string.IsNullOrWhiteSpace(account.LocalUserDataDir))
        {
            return LocalChromeEnabled;
        }

        return AdsPowerEnabled;
    }

    public ResponseCollectionFilters ResponseFilters =>
        ResponseCollectionFilters.NormalizeLegacy(
            ResponseFilterEnabled,
            ResponseFilterExcludeFemale,
            ResponseFilterMaxAge,
            ResponseFilterExcludeMale,
            ResponseFilterMaxAgeMale,
            ResponseFilterMaxAgeFemale,
            ResponseFilterMaxResponseAgeDays);

    public int EffectivePhoneUnchangedHours =>
        ResponsePhoneWatchRules.ResolveUnchangedHours(PhoneUnchangedHours);
}

public sealed record AdsPowerGroupDto(string GroupId, string GroupName);

public sealed record WorkerAccountSyncItemDto(
    string AdsPowerProfileId,
    string DisplayName,
    string? AdsPowerGroupId = null,
    string? AdsPowerGroupName = null,
    string? MultiloginProfileId = null,
    string? MultiloginFolderId = null,
    string? MultiloginProfileName = null);

public sealed record WorkerAccountSyncRequest(
    IReadOnlyList<WorkerAccountSyncItemDto> Accounts,
    IReadOnlyList<AdsPowerGroupDto>? Groups = null,
    bool Multilogin = false,
    bool ReplaceMultiloginCatalog = false);

public sealed record WorkerCandidateDto(
    Guid AccountId,
    string AccountName,
    string Source,
    string SourceResponseId,
    string CardFingerprint,
    string FullName,
    int? Age,
    string? Gender,
    string PhoneRaw,
    string City,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string AvitoSubProfileId,
    string RawText,
    string ChatMessagesJson,
    DateTime CreatedAt,
    string AvitoSubProfileName = "",
    DateTime CollectedAt = default,
    /// <summary>Метрика номера: <see cref="ResponsePhoneMetricKinds"/>.</summary>
    string PhoneMetricKind = "",
    string? PreviousPhoneRaw = null,
    string? PreviousPhoneNormalized = null,
    int? PhoneUnchangedHours = null,
    DateTime? PhoneChangedAtUtc = null,
    /// <summary>Тип скачанного аватара кандидата. URL Avito по сети не передаётся.</summary>
    string? AvatarContentType = null,
    /// <summary>Скачанный аватар кандидата в Base64; ограничен <see cref="CandidateResponseAvatar.MaxImageBytes"/>.</summary>
    string? AvatarImageBase64 = null,
    string? Citizenship = null);

public sealed record WorkerCandidateBatchRequest(
    IReadOnlyList<WorkerCandidateDto> Candidates);

public sealed record CandidateLookupProfileDto(
    string FullName,
    int? Age,
    string City,
    string PhoneNormalized,
    /// <summary>Дата отклика (чат); для неполного имени — окно ~неделя на API.</summary>
    DateTime? ResponseAtUtc = null);

public sealed record WorkerCandidateLookupRequest(
    Guid AccountId,
    string DuplicateScope,
    IReadOnlyList<string> SourceResponseIds,
    IReadOnlyList<string> PhoneNormalized,
    bool IncludeAllKnownPhones = false,
    string? AvitoSubProfileId = null,
    IReadOnlyList<string>? CardFingerprints = null,
    IReadOnlyList<CandidateLookupProfileDto>? Profiles = null,
    /// <summary>
    /// Вернуть метаданные совпавших SourceResponseId. Нужны для восстановления phone-watch
    /// из Orbita после переустановки или очистки локального состояния воркера.
    /// </summary>
    bool IncludeSourceResponseMetadata = false);

/// <summary>
/// Сохранённый в Orbita отклик, совпавший по SourceResponseId. Список возвращается свежими
/// первыми по времени добавления отклика в Orbita.
/// </summary>
public sealed record WorkerKnownSourceResponseDto(
    string SourceResponseId,
    DateTime CollectedAt,
    string PhoneRaw,
    string PhoneNormalized);

public sealed record WorkerCandidateLookupResponse(
    IReadOnlyList<string> ExistingSourceResponseIds,
    IReadOnlyList<string> ExistingPhones,
    IReadOnlyList<string> ExistingCardFingerprints,
    IReadOnlyList<int> MatchedProfileIndexes,
    IReadOnlyList<WorkerKnownSourceResponseDto>? ExistingSourceResponses = null);

public sealed record WorkerPendingChatMessageDto(
    Guid Id,
    string SourceResponseId,
    string Text,
    string? AvitoSubProfileId = null,
    DateTime QueuedAtUtc = default);

public sealed record WorkerOutboundChatAckRequest(IReadOnlyList<Guid> SentIds);
public sealed record WorkerOutboundChatClaimRequest(Guid MessageId);

public sealed record WorkerMonitoringStatsDto(
    double HistoricalHeatScore,
    DashboardStatsDto Stats);

public sealed record WorkerCandidateIngestionItemResultDto(
    Guid? Id,
    string SourceResponseId,
    string Status,
    string? ErrorMessage);

public sealed record WorkerCandidateIngestionResultDto(
    int Received,
    int Ingested,
    int SkippedDuplicates,
    int Errors,
    IReadOnlyList<WorkerCandidateIngestionItemResultDto> Items);

public sealed record UpdateWorkerSettingsRequest(
    int MaxConcurrentAccounts,
    string? AdsPowerApiBaseUrl = null,
    string? AdsPowerApiKey = null,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    int? ResponseFilterMaxAge = null,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    /// <summary>Часов без смены номера до метрики «не менялся». null=24, 0=выкл стабильность.</summary>
    int? PhoneUnchangedHours = null,
    /// <summary>Авто-отправка новых откликов в CRM офиса назначения воркера.</summary>
    bool? AutoDeliverToCrm = null,
    /// <summary>Авто-отправка новых откликов в Bitrix (схема офиса). Legacy-канал.</summary>
    bool? AutoDeliverToBitrix = null,
    /// <summary>JSON-набор профилей и субпрофилей, отклики из которых нужно подсвечивать.</summary>
    string? ResponseHighlightTargetsJson = null,
    /// <summary>ID группы AdsPower; пусто — все группы.</summary>
    string? AdsPowerGroupId = null,
    /// <summary>Ключ RuCaptcha для автопрохождения GeeTest v4. Пусто — выкл.</summary>
    string? RuCaptchaApiKey = null,
    string? MultiloginLauncherUrl = null,
    string? MultiloginCloudApiUrl = null,
    /// <summary>Пусто — не менять сохранённый token (поле не возвращается в HTML панели).</summary>
    string? MultiloginAutomationToken = null,
    /// <summary>Путь к chrome.exe / Chromium. Пусто — автопоиск на машине воркера.</summary>
    string? LocalChromeExecutablePath = null,
    bool AdsPowerEnabled = true,
    bool MultiloginEnabled = true,
    bool LocalChromeEnabled = true);

public sealed record UpdateWorkerAccountRequest(bool IsEnabledInPanel);

public static class WorkerBrowserProviderMessages
{
    public const string DisabledStatusLabel = "Провайдер выключен";
    public const string DisabledHint =
        "Провайдер выключен: аккаунты сохранены, но не синхронизируются и не запускаются";
    public const string CheckingMessage = "Проверка на воркере…";
    public const string NeedsToken = "Сначала укажите API Token и сохраните настройки.";
    public const string ProviderOff = "Провайдер выключен. Включите его, чтобы проверить подключение.";
    public const string UnknownProvider = "Неизвестный провайдер.";
    public const string CheckQueued = "Запрос отправлен воркеру.";
    public const string SyncQueued = "Синхронизация запущена на воркере.";
    public const string CheckAlreadyQueued = "Дождитесь текущей проверки на воркере.";
    public const string SyncAlreadyQueued = "Дождитесь текущей синхронизации на воркере.";
    public const string SaveSettingsFirst = "Сначала сохраните настройки.";
}

public static class WorkerBrowserProviderKinds
{
    public const string AdsPower = "AdsPower";
    public const string Multilogin = "Multilogin";
    public const string Local = "Local";

    public static string? Normalize(string? value) =>
        value?.Trim() switch
        {
            "AdsPower" or "adspower" or "ads" => AdsPower,
            "Multilogin" or "multilogin" or "mlx" => Multilogin,
            "Local" or "local" or "chrome" or "localChrome" => Local,
            _ => null
        };

    public static bool SupportsCatalogSync(string provider) =>
        string.Equals(provider, AdsPower, StringComparison.Ordinal)
        || string.Equals(provider, Multilogin, StringComparison.Ordinal);
}

public static class WorkerBrowserProviderStatus
{
    public const string Disabled = "disabled";
    public const string NeedsSetup = "needsSetup";
    public const string Unchecked = "unchecked";
    public const string Checking = "checking";
    public const string Connected = "connected";
    public const string Error = "error";

    public static string Resolve(
        bool enabled,
        bool needsSetup,
        bool checking,
        bool? lastSucceeded)
    {
        if (!enabled)
        {
            return Disabled;
        }

        if (checking)
        {
            return Checking;
        }

        if (needsSetup)
        {
            return NeedsSetup;
        }

        if (lastSucceeded is null)
        {
            return Unchecked;
        }

        return lastSucceeded.Value ? Connected : Error;
    }

    public static string Label(string status) => status switch
    {
        Disabled => "Выключен",
        NeedsSetup => "Требуется настройка",
        Checking => "Проверяется",
        Connected => "Подключён",
        Error => "Ошибка подключения",
        _ => "Не проверено"
    };
}

public sealed record WorkerBrowserProviderCheckDto(
    string Provider,
    string Status,
    string StatusLabel,
    string? Message = null,
    DateTime? CheckedAtUtc = null,
    int? ProfileCount = null,
    int? GroupCount = null,
    string? ResolvedExecutablePath = null,
    bool CanCheck = false,
    bool CanSync = false);

public sealed record WorkerPendingBrowserProviderCheckDto(string Provider, DateTime RequestedAtUtc);

public sealed record WorkerPendingBrowserProviderSyncDto(string Provider, DateTime RequestedAtUtc);

public sealed record RequestWorkerBrowserProviderCheckRequest(string Provider);

public sealed record RequestWorkerBrowserProviderSyncRequest(string Provider);

public sealed record ReportWorkerBrowserProviderCheckRequest(
    string Provider,
    bool Success,
    string? Message = null,
    int? ProfileCount = null,
    int? GroupCount = null,
    string? ResolvedExecutablePath = null,
    bool CompletesSync = false,
    IReadOnlyList<AdsPowerGroupDto>? Groups = null);

public sealed record CreateLocalWorkerAccountRequest(string DisplayName, string? LocalUserDataDir = null);

public sealed record UpdateLocalWorkerAccountRequest(string? DisplayName = null, string? LocalUserDataDir = null);

public sealed record WorkerPendingLocalChromeLoginDto(
    Guid SessionId,
    Guid WorkerId,
    Guid AccountId);

public sealed record CompleteLocalChromeLoginRequest(Guid SessionId);

/// <summary>
/// Обновление логина/пароля Avito для аккаунта.
/// <paramref name="Password"/> = null/пусто — не менять сохранённый пароль (если <paramref name="Clear"/> = false).
/// </summary>
public sealed record UpdateWorkerAccountCredentialsRequest(
    string? Login,
    string? Password = null,
    bool Clear = false);

public sealed record WorkerAccountCredentialsDto(
    Guid AccountId,
    string? Login,
    bool HasPassword);

/// <summary>
/// Обновление настроек профиля обычного Chrome: Avito + HTTP-прокси.
/// Пустой пароль — оставить текущий; <paramref name="ClearCredentials"/> / <paramref name="ClearProxyPassword"/> очищают секрет.
/// </summary>
public sealed record UpdateLocalWorkerAccountProfileRequest(
    string? Login = null,
    string? Password = null,
    bool ClearCredentials = false,
    bool? ProxyEnabled = null,
    string? ProxyAddress = null,
    string? ProxyUsername = null,
    string? ProxyPassword = null,
    bool ClearProxyPassword = false,
    string? TrafficMode = null,
    bool? BlockMedia = null,
    bool? BlockAnalytics = null,
    bool? BlockImages = null,
    bool? BlockFonts = null,
    bool? BlockPrefetch = null,
    int? NavigationTimeoutSeconds = null);

public sealed record LocalWorkerAccountProfileDto(
    Guid AccountId,
    string? Login,
    bool HasPassword,
    bool HasCredentials,
    bool ProxyEnabled,
    string? ProxyAddress,
    string? ProxyUsername,
    bool HasProxyPassword,
    string ProxyStatus,
    string BrowserStatus,
    bool CanOpenBrowser,
    string TrafficMode = LocalChromeTrafficRules.ModeNormal,
    bool BlockMedia = false,
    bool BlockAnalytics = false,
    bool BlockImages = false,
    bool BlockFonts = false,
    bool BlockPrefetch = false,
    int NavigationTimeoutSeconds = LocalChromeTrafficRules.DefaultTimeoutSeconds,
    string? TrafficLastSummary = null);

public sealed record BulkWorkersMonitoringResultDto(
    int UpdatedCount,
    int UnchangedCount,
    int TotalCount);
