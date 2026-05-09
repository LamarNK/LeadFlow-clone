using System.Diagnostics;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services.AdsPower;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Bitrix;
using LeadFlow.Services.Browser;
using System.Text.Json;

namespace LeadFlow.Services;

public sealed class MonitoringService(
    ISettingsService settingsService,
    IMonitoringRepository repository,
    IPhoneNormalizer phoneNormalizer,
    ICandidateParser candidateParser,
    IDuplicateService duplicateService,
    IBitrixClient bitrixClient,
    IAvitoResponseSource avitoResponseSource,
    AvitoDemoResponseSource avitoDemoResponseSource,
    AvitoParserService avitoParser,
    IBrowserSessionService browserSessionService,
    IBackgroundWebViewHostFactory backgroundWebViewHostFactory,
    IWebPageAutomationService automationService,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService) : IMonitoringService
{
    private const string ProfileItemsUrl = "https://www.avito.ru/profile/pro/items";
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private System.Threading.Timer? _countdownTimer;
    private System.Threading.Timer? _profileStatsTimer;
    private DateTime? _nextCheckTime;

    /// <summary>Пауза перед повтором основного цикла после необработанной ошибки (минуты).</summary>
    private const int LoopRecoveryPauseMinutes = 5;

    /// <summary>
    /// После N успешно запланированных пауз восстановления при (N+1)-м сбое подряд мониторинг останавливается. 0 — без лимита.
    /// </summary>
    private const int MaxLoopRecoveryFailuresBeforeStop = 10;
    private readonly Lock _activeAdsSync = new();
    private readonly Dictionary<Guid, IReadOnlyList<AvitoAdStatus>> _activeAdsByAccount = [];
    private int _consecutiveMonitoringLoopFailures;
    private int _consecutiveQuietMonitoringCycles;

    public event EventHandler<MonitoringStatus>? StatusChanged;
    public event EventHandler<string>? StatusMessageChanged;
    public event EventHandler<CandidateResponse>? ResponseProcessed;
    public event EventHandler<ProfileStatsUpdatedEventArgs>? ProfileStatsUpdated;
    public event EventHandler? NextCycleCheckTimeChanged;
    public event EventHandler<string>? MonitoringAutoStopped;

    public MonitoringStatus CurrentStatus { get; private set; } = MonitoringStatus.Waiting;
    public string CurrentStatusMessage { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime? NextCycleCheckAtUtc { get; private set; }

    public IReadOnlyList<AvitoAdStatus> GetActiveAdsSnapshot()
    {
        lock (_activeAdsSync)
        {
            return _activeAdsByAccount.Values
                .SelectMany(static ads => ads)
                .Select(CloneAd)
                .ToList();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsActive = true;
        _consecutiveQuietMonitoringCycles = 0;
        UpdateStatus(MonitoringStatus.Running, "Запуск мониторинга: загружаем настройки.");
        try
        {
            var settings = await settingsService.LoadAsync(_cts.Token);
            // Синхронизация при старте отключена: проверка дублей выполняется через API для каждого отклика
            // await SyncBitrixLeadsAsync(settings, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsActive = false;
            UpdateStatus(MonitoringStatus.Stopped, string.Empty);
            _cts.Dispose();
            _cts = null;
            return;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix pre-sync failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            IsActive = false;
            _cts.Dispose();
            _cts = null;
            UpdateStatus(MonitoringStatus.Error, $"Ошибка запуска мониторинга: {ex.Message}");
            return;
        }

        _ = GlobalLogger.Instance.LogAsync("Monitoring started.", DeskLinkAuditLogLevel.Info);

        try
        {
            UpdateStatus(MonitoringStatus.Running, "Загружаем активные объявления Авито…");
            await UpdateProfileStatsAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsActive = false;
            UpdateStatus(MonitoringStatus.Stopped, string.Empty);
            _cts.Dispose();
            _cts = null;
            return;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Парсинг активных объявлений при старте не завершён: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }

        StartProfileStatsTimer();
        try
        {
            await ArmNextProfileStatsTimerTickAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsActive = false;
            UpdateStatus(MonitoringStatus.Stopped, string.Empty);
            _cts.Dispose();
            _cts = null;
            return;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Не удалось запланировать следующее обновление объявлений: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }

        UpdateStatus(MonitoringStatus.Running, "Мониторинг запущен: начинаем обход активных аккаунтов Авито.");
        _loopTask = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        UpdateStatus(MonitoringStatus.Stopped, "Останавливаем мониторинг и завершаем текущие операции.");
        _cts.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask;
        }

        _countdownTimer?.Dispose();
        _profileStatsTimer?.Dispose();
        SetNextCheckTime(null);
        IsActive = false;
        UpdateStatus(MonitoringStatus.Stopped, string.Empty);
    }

    /// <summary>
    /// Таймер только для повторных прогонов парсинга объявлений (первый — при старте, до запуска цикла откликов).
    /// </summary>
    private void StartProfileStatsTimer()
    {
        _profileStatsTimer?.Dispose();

        _profileStatsTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var token = _cts?.Token ?? CancellationToken.None;
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await UpdateProfileStatsAsync(token);

                var nextDelay = await ScheduleNextProfileStatsUpdateAsync(token);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Следующая проверка объявлений запланирована через {(int)nextDelay.TotalMinutes} мин.",
                    DeskLinkAuditLogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                // остановка мониторинга
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync($"Periodic profile stats update failed: {ex.Message}", DeskLinkAuditLogLevel.Error);
                try
                {
                    var token = _cts?.Token ?? CancellationToken.None;
                    if (!token.IsCancellationRequested)
                    {
                        await ScheduleNextProfileStatsUpdateAsync(token);
                    }
                }
                catch
                {
                    // игнорируем сбой планирования
                }
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task ArmNextProfileStatsTimerTickAsync(CancellationToken ct)
    {
        var nextDelay = await ScheduleNextProfileStatsUpdateAsync(ct);
        _ = GlobalLogger.Instance.LogAsync(
            $"Автообновление объявлений: следующий цикл через {(int)nextDelay.TotalMinutes} мин. (интервал {MonitoringTiming.ActiveAdsRefreshIntervalMinutes} мин, задан в коде).",
            DeskLinkAuditLogLevel.Info);
    }

    private Task<TimeSpan> ScheduleNextProfileStatsUpdateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nextDelay = TimeSpan.FromMinutes(MonitoringTiming.ActiveAdsRefreshIntervalMinutes);
        _profileStatsTimer?.Change(nextDelay, Timeout.InfiniteTimeSpan);
        return Task.FromResult(nextDelay);
    }

    private async Task UpdateProfileStatsAsync(CancellationToken ct)
    {
        var settings = await settingsService.LoadAsync(ct);
        var enabledIds = settings.Avito.Accounts.Where(static a => a.IsEnabled).Select(static a => a.Id).ToHashSet();
        var accounts = (await repository.GetAccountsAsync(ct))
            .Where(a => enabledIds.Contains(a.Id))
            .ToList();

        foreach (var account in accounts)
        {
            try
            {
                var previousActiveAdsCount = account.ActiveAdsCount;
                var previousBlockedCount = account.BlockedCount;
                var previousDraftsCount = account.DraftsCount;

                Avito.ProfileResult profileData;
                if (account.ProfileProvider == AvitoProfileProvider.AdsPower)
                {
                    if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId) ||
                        string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl))
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            $"Пропускаем парсинг объявлений для AdsPower-аккаунта {account.DisplayName}: не заданы user_id или base URL Local API.",
                            DeskLinkAuditLogLevel.Warning,
                            properties: new Dictionary<string, object?>
                            {
                                ["accountId"] = account.Id,
                                ["accountName"] = account.DisplayName,
                                ["adsPower.hasUserId"] = !string.IsNullOrWhiteSpace(account.AdsPowerProfileId),
                                ["adsPower.hasBaseUrl"] = !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl)
                            });
                        continue;
                    }

                    var options = new AdsPowerConnectionOptions(
                        account.AdsPowerApiBaseUrl,
                        string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

                    profileData = await CollectAdsPowerProfileStatsAsync(account, options, ct).ConfigureAwait(false);
                }
                else
                {
                    var html = await LoadProfilePageHtmlAsync(account, ct);
                    profileData = avitoParser.ParseProfilePage(html, account.Id);
                }

                UpdateActiveAdsSnapshot(account.Id, profileData.ActiveAds);
                await GlobalLogger.Instance.LogAsync(
                    () => $"Парсинг активных объявлений завершён для аккаунта {account.DisplayName} ({account.ProfileProvider}): на вкладке «Активные»={profileData.ActiveCount}, вакансий (раздел /rabota/)={profileData.ActiveAds.Count}, blocked={profileData.BlockedCount}, drafts={profileData.DraftsCount}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["profileProvider"] = account.ProfileProvider.ToString(),
                        ["tabActiveAdsCount"] = profileData.ActiveCount,
                        ["vacancyActiveAdsCount"] = profileData.ActiveAds.Count,
                        ["blockedAdsCount"] = profileData.BlockedCount,
                        ["draftsCount"] = profileData.DraftsCount
                    });

                if (profileData.ActiveAds.Count > 0)
                {
                    await GlobalLogger.Instance.LogAsync(
                        () => $"Активные объявления аккаунта {account.DisplayName}: {string.Join(" | ", profileData.ActiveAds.Select(ad => $"{ad.Id}:{ad.Title} [{ad.Views}/{ad.Contacts}]"))}",
                        DeskLinkAuditLogLevel.Debug,
                        properties: new Dictionary<string, object?>
                        {
                            ["accountId"] = account.Id,
                            ["accountName"] = account.DisplayName,
                            ["profileProvider"] = account.ProfileProvider.ToString(),
                            ["ads"] = profileData.ActiveAds.Select(ad => new
                            {
                                ad.Id,
                                ad.Title,
                                ad.City,
                                ad.Views,
                                ad.Contacts
                            }).ToArray()
                        });
                }
                else
                {
                    await GlobalLogger.Instance.LogAsync(
                        () => $"Во время парсинга активных объявлений для аккаунта {account.DisplayName} активные объявления не найдены.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["accountId"] = account.Id,
                            ["accountName"] = account.DisplayName,
                            ["profileProvider"] = account.ProfileProvider.ToString(),
                            ["tabActiveAdsCount"] = profileData.ActiveCount,
                            ["vacancyActiveAdsCount"] = profileData.ActiveAds.Count,
                            ["blockedAdsCount"] = profileData.BlockedCount,
                            ["draftsCount"] = profileData.DraftsCount
                        });
                }

                // Обновляем аккаунт в БД (только вакансии; товары на вкладке «Активные» не учитываем)
                account.ActiveAdsCount = profileData.ActiveAds.Count;
                account.BlockedCount = profileData.BlockedCount;
                account.DraftsCount = profileData.DraftsCount;
                account.AdsStatsUpdatedAt = DateTime.UtcNow;

                await repository.SaveAccountAsync(account, ct);

                // Уведомляем ViewModel об обновлении
                ProfileStatsUpdated?.Invoke(this, new ProfileStatsUpdatedEventArgs
                {
                    Account = account,
                    PreviousActiveAdsCount = previousActiveAdsCount,
                    PreviousBlockedCount = previousBlockedCount,
                    PreviousDraftsCount = previousDraftsCount
                });
            }
            catch (Exception ex)
            {
                UpdateActiveAdsSnapshot(account.Id, []);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ошибка обновления статистики объявлений для аккаунта {account.DisplayName} ({account.ProfileProvider}): {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["profileProvider"] = account.ProfileProvider.ToString(),
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var settings = await settingsService.LoadAsync(cancellationToken);
                    var accounts = settings.Avito.Accounts.Where(x => x.IsEnabled).ToList();
                    UpdateStatus(
                        MonitoringStatus.Running,
                        accounts.Count == 0
                            ? "Активных аккаунтов Авито нет: откройте настройки и включите хотя бы один аккаунт."
                            : $"Начинаем новый цикл мониторинга: активных аккаунтов {accounts.Count}.");

                    var cycleSw = Stopwatch.StartNew();
                    var newResponsesThisCycle = 0;
                    var accountsPolled = 0;
                    var hadUndischargedBacklog = false;
                    foreach (var account in accounts)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var (newCount, polled, backlog) = await ProcessAccountAsync(account, settings, cancellationToken);
                        if (polled)
                        {
                            accountsPolled++;
                            newResponsesThisCycle += newCount;
                            hadUndischargedBacklog |= backlog;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(MonitoringTiming.DelayBetweenAccountsSeconds), cancellationToken);
                    }

                    cycleSw.Stop();
                    _ = GlobalLogger.Instance.LogAsync(
                        $"[monitoring] Цикл обхода аккаунтов завершён за {cycleSw.Elapsed.TotalSeconds:F1} с (аккаунтов: {accounts.Count}).",
                        DeskLinkAuditLogLevel.Info);

                    _consecutiveMonitoringLoopFailures = 0;

                    if (newResponsesThisCycle > 0 || hadUndischargedBacklog)
                    {
                        _consecutiveQuietMonitoringCycles = 0;
                    }
                    else
                    {
                        _consecutiveQuietMonitoringCycles++;
                    }

                    var historicalHeat = await repository.GetHistoricalResponseIngestHeatScoreAsync(DateTime.UtcNow, cancellationToken);
                    var delay = MonitoringCycleDelay.GetDelayAfterCycle(
                        newResponsesThisCycle,
                        accountsPolled,
                        _consecutiveQuietMonitoringCycles,
                        hadUndischargedBacklog,
                        historicalHeat);
                    _ = GlobalLogger.Instance.LogAsync(
                        $"[monitoring] Следующий цикл через {delay.TotalMinutes:F1} мин (новых: {newResponsesThisCycle}, опрошено аккаунтов: {accountsPolled}, тихих циклов подряд: {_consecutiveQuietMonitoringCycles}, очередь откликов: {hadUndischargedBacklog}, истор. «жара» слота: {historicalHeat:F2}).",
                        DeskLinkAuditLogLevel.Info);
                    SetNextCheckTime(DateTime.UtcNow + delay);
                    var waitHint = newResponsesThisCycle > 0
                        ? $"Найдено новых откликов: {newResponsesThisCycle}. Следующая проверка {FormatDelay(delay)}."
                        : $"Новых откликов нет. Следующая проверка {FormatDelay(delay)}.";
                    UpdateStatus(MonitoringStatus.Waiting, $"Цикл завершён. {waitHint}");
                    StartCountdownTimer(delay);
                    await Task.Delay(delay, cancellationToken);
                    _countdownTimer?.Dispose();
                    SetNextCheckTime(null);
                    UpdateStatus(MonitoringStatus.Running, "Пауза завершена: запускаем следующий цикл мониторинга.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMonitoringLoopAsync(ex, cancellationToken))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync("Monitoring stopped by cancellation.", DeskLinkAuditLogLevel.Info);
        }
    }

    internal async Task<bool> TryRecoverMonitoringLoopAsync(Exception ex, CancellationToken cancellationToken)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"Monitoring loop failed.{Environment.NewLine}{ex}",
            DeskLinkAuditLogLevel.Error);

        _countdownTimer?.Dispose();
        SetNextCheckTime(null);

        _consecutiveMonitoringLoopFailures++;

        var maxAttempts = MaxLoopRecoveryFailuresBeforeStop;
        if (maxAttempts > 0 && _consecutiveMonitoringLoopFailures > maxAttempts)
        {
            IsActive = false;
            var fatalMessage =
                $"Мониторинг остановлен: превышен лимит {maxAttempts} сбоев цикла подряд. Последняя ошибка: {ex.Message}";
            UpdateStatus(MonitoringStatus.Error, fatalMessage);
            MonitoringAutoStopped?.Invoke(this, fatalMessage);
            return false;
        }

        var pauseMinutes = Math.Clamp(LoopRecoveryPauseMinutes, 1, 120);
        var pause = TimeSpan.FromMinutes(pauseMinutes);

        UpdateStatus(
            MonitoringStatus.Recovering,
            $"Сбой цикла: {ex.Message}. Повтор через {pauseMinutes} мин.");

        try
        {
            await Task.Delay(pause, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return true;
    }

    /// <returns>Новые откликов с Авито, был ли опрос источника, есть ли необработанный «хвост» сверх лимита за цикл.</returns>
    internal async Task<(int NewResponsesDetected, bool PolledSource, bool HasUndischargedBacklog)> ProcessAccountAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        if (account.Status is AvitoAccountStatus.RequiresLogin or AvitoAccountStatus.RequiresManualAction or AvitoAccountStatus.Paused)
        {
            UpdateStatus(
                account.Status == AvitoAccountStatus.RequiresLogin ? MonitoringStatus.RequiresAuthorization : MonitoringStatus.RequiresManualAction,
                $"Аккаунт \"{account.DisplayName}\" пропущен. Текущий статус: {account.Status}.");
            _ = GlobalLogger.Instance.LogAsync(
                $"Account {account.DisplayName} skipped because of status {account.Status}.",
                DeskLinkAuditLogLevel.Warning);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Аккаунт пропущен",
                Details = $"Статус: {account.Status}"
            }, cancellationToken);
            return (0, false, false);
        }

        account.Status = AvitoAccountStatus.Monitoring;
        account.LastMonitoringAt = DateTime.UtcNow;
        UpdateStatus(MonitoringStatus.Running, $"Проверяем аккаунт Авито \"{account.DisplayName}\": открываем список откликов.");
        _ = GlobalLogger.Instance.LogAsync(
            $"Processing account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Info);
        await repository.SaveAccountAsync(account, cancellationToken);

        var accountSw = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<CandidateResponse> responses;
            if (settings.DemoModeEnabled)
            {
                responses = await avitoDemoResponseSource.GetBatchAsync(account, MonitoringTiming.MaxResponsesPerAccountPerCycle, cancellationToken);
            }
            else
            {
                responses = await CollectResponsesAcrossSubProfilesAsync(account, settings, cancellationToken).ConfigureAwait(false);
            }
            var maxPerCycle = MonitoringTiming.MaxResponsesPerAccountPerCycle;
            if (responses.Count > maxPerCycle)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"[monitoring] Аккаунт \"{account.DisplayName}\": новых откликов {responses.Count}, в этом цикле обрабатываем {maxPerCycle}; остальные подтянутся в следующих проверках.",
                    DeskLinkAuditLogLevel.Info);
                UpdateStatus(
                    MonitoringStatus.Running,
                    $"Аккаунт \"{account.DisplayName}\": найдено новых откликов {responses.Count}, в этом цикле обрабатываем {maxPerCycle}. Остальные — в следующих проверках.");
            }
            else
            {
                UpdateStatus(MonitoringStatus.Running, $"Аккаунт \"{account.DisplayName}\" проверен: найдено новых откликов {responses.Count}.");
            }

            foreach (var response in responses.Take(maxPerCycle))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await ProcessResponseAsync(response, settings, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(MonitoringTiming.DelayBetweenResponsesSeconds), cancellationToken);
            }

            if (account.Status == AvitoAccountStatus.Monitoring)
            {
                account.Status = AvitoAccountStatus.Authorized;
            }

            await repository.SaveAccountAsync(account, cancellationToken);

            var backlog = responses.Count > maxPerCycle;
            return (responses.Count, true, backlog);
        }
        finally
        {
            accountSw.Stop();
            _ = GlobalLogger.Instance.LogAsync(
                $"[monitoring] Аккаунт \"{account.DisplayName}\": этап проверки и обработки откликов за {accountSw.Elapsed.TotalSeconds:F1} с.",
                DeskLinkAuditLogLevel.Info);
        }
    }

    internal async Task ProcessResponseAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        UpdateStatus(MonitoringStatus.Running, $"Обрабатываем отклик \"{response.FullName}\" ({response.PhoneRaw}): готовим данные кандидата.");
        var names = candidateParser.ParseName(response.FullName);
        response.FirstName = names.FirstName;
        response.LastName = names.LastName;
        response.MiddleName = names.MiddleName;
        response.PhoneNormalized = phoneNormalizer.Normalize(response.PhoneRaw);
        response.Status = ResponseStatus.InProgress;

        _ = GlobalLogger.Instance.LogAsync(
            $"New response detected for account {response.AccountId}: {response.FullName}.",
            DeskLinkAuditLogLevel.Info);
        await repository.SaveCandidateAsync(response, cancellationToken);
        await repository.AddLogAsync(new ProcessingLogItem
        {
            CandidateResponseId = response.Id,
            AccountId = response.AccountId,
            Level = "Info",
            Message = "Найден новый отклик",
            Details = response.FullName
        }, cancellationToken);

        var recoverStuckInProgress = false;
        try
        {
            UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" ({response.PhoneNormalized}): проверяем дубли в локальной базе и Bitrix24.");
            var duplicate = await duplicateService.CheckAsync(response, settings, cancellationToken);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Info",
                Message = "Проверка дублей выполнена",
                Details = duplicate.Summary
            }, cancellationToken);

            if (duplicate.IsDuplicate)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Duplicate detected for response {response.Id}.",
                    DeskLinkAuditLogLevel.Info);
                response.Status = ResponseStatus.Duplicate;
                response.ProcessedAt = DateTime.UtcNow;
                await repository.SaveCandidateAsync(response, cancellationToken);
                UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" помечен как дубль. Новая сделка в Bitrix24 не создаётся.");
                ResponseProcessed?.Invoke(this, response);
                return;
            }

            if (duplicate.ShouldDeferBitrixSend)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Bitrix duplicate check unavailable for response {response.Id}; CRM send deferred.",
                    DeskLinkAuditLogLevel.Warning);
                response.Status = ResponseStatus.ActionRequired;
                response.ProcessedAt = DateTime.UtcNow;
                response.ErrorMessage = duplicate.Summary;
                await repository.SaveCandidateAsync(response, cancellationToken);
                await repository.AddLogAsync(new ProcessingLogItem
                {
                    CandidateResponseId = response.Id,
                    AccountId = response.AccountId,
                    Level = "Warning",
                    Message = "Отправка в Bitrix24 отложена",
                    Details = duplicate.BitrixCheckUnavailableReason ?? duplicate.Summary
                }, cancellationToken);
                UpdateStatus(
                    MonitoringStatus.Running,
                    $"Отклик \"{response.FullName}\": проверка дублей в Bitrix24 недоступна. Отправку в CRM можно повторить вручную из мониторинга.");
                ResponseProcessed?.Invoke(this, response);
                return;
            }

            const string sendDisabledMessage = "Тестовый режим: отправка в Bitrix24 временно отключена.";
            UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" уникален, но отправка в Bitrix24 отключена.");
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix send skipped for response {response.Id}: temporary disabled mode.",
                DeskLinkAuditLogLevel.Warning);
            response.Status = ResponseStatus.ActionRequired;
            response.ProcessedAt = DateTime.UtcNow;
            response.ErrorMessage = sendDisabledMessage;
            await repository.SaveCandidateAsync(response, cancellationToken);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Warning",
                Message = "Отправка в Bitrix24 отключена",
                Details = sendDisabledMessage
            }, cancellationToken);

            ResponseProcessed?.Invoke(this, response);
        }
        catch (OperationCanceledException)
        {
            response.Status = ResponseStatus.ActionRequired;
            response.ErrorMessage = "Обработка прервана (отмена). Повторите отправку в Bitrix24 вручную при необходимости.";
            response.ProcessedAt = DateTime.UtcNow;
            try
            {
                await repository.SaveCandidateAsync(response, CancellationToken.None);
                await repository.AddLogAsync(new ProcessingLogItem
                {
                    CandidateResponseId = response.Id,
                    AccountId = response.AccountId,
                    Level = "Warning",
                    Message = "Обработка отклика прервана",
                    Details = response.ErrorMessage
                }, CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Failed to persist cancelled response {response.Id}: {saveEx.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }

            ResponseProcessed?.Invoke(this, response);
            throw;
        }
        catch (Exception ex)
        {
            if (response.Status == ResponseStatus.Sent && !string.IsNullOrWhiteSpace(response.BitrixEntityId))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Post-CRM failure for response {response.Id} (Bitrix deal {response.BitrixEntityId}).{Environment.NewLine}{ex}",
                    DeskLinkAuditLogLevel.Warning);
                try
                {
                    await repository.SaveCandidateAsync(response, CancellationToken.None);
                    await repository.AddLogAsync(new ProcessingLogItem
                    {
                        CandidateResponseId = response.Id,
                        AccountId = response.AccountId,
                        Level = "Warning",
                        Message = "Сделка в Bitrix24 создана, ошибка после сохранения",
                        Details = ex.Message
                    }, CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Failed to persist Sent state after post-CRM error for response {response.Id}: {saveEx.Message}",
                        DeskLinkAuditLogLevel.Error);
                }

                ResponseProcessed?.Invoke(this, response);
            }
            else if (response.Status == ResponseStatus.InProgress)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Response processing failed for {response.Id}.{Environment.NewLine}{ex}",
                    DeskLinkAuditLogLevel.Error);
                response.Status = ResponseStatus.Error;
                response.ErrorMessage = ex.Message;
                response.ProcessedAt = DateTime.UtcNow;
                try
                {
                    await repository.SaveCandidateAsync(response, CancellationToken.None);
                    await repository.AddLogAsync(new ProcessingLogItem
                    {
                        CandidateResponseId = response.Id,
                        AccountId = response.AccountId,
                        Level = "Error",
                        Message = "Ошибка обработки отклика",
                        Details = ex.Message
                    }, CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Failed to persist error state for response {response.Id}: {saveEx.Message}",
                        DeskLinkAuditLogLevel.Error);
                }

                ResponseProcessed?.Invoke(this, response);
            }
            else
            {
                throw;
            }
        }
        finally
        {
            if (response.Status == ResponseStatus.InProgress)
            {
                response.Status = ResponseStatus.ActionRequired;
                response.ErrorMessage = string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "Обработка остановлена нештатно — используйте ручную отправку в Bitrix24."
                    : response.ErrorMessage;
                response.ProcessedAt = DateTime.UtcNow;
                recoverStuckInProgress = true;
            }
        }

        if (recoverStuckInProgress)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Response {response.Id} left InProgress; marked ActionRequired.",
                DeskLinkAuditLogLevel.Warning);
            try
            {
                await repository.SaveCandidateAsync(response, CancellationToken.None);
                await repository.AddLogAsync(new ProcessingLogItem
                {
                    CandidateResponseId = response.Id,
                    AccountId = response.AccountId,
                    Level = "Warning",
                    Message = "Статус восстановлен после сбоя",
                    Details = response.ErrorMessage
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Failed to finalize stuck InProgress response {response.Id}: {ex.Message}",
                    DeskLinkAuditLogLevel.Error);
            }

            ResponseProcessed?.Invoke(this, response);
        }
    }

    private async Task SyncBitrixLeadsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        UpdateStatus(MonitoringStatus.Running, "Синхронизация с Bitrix24: загружаем существующие сделки перед стартом мониторинга.");
        var bitrixLeads = await bitrixClient.GetExistingLeadsAsync(settings, cancellationToken);
        if (bitrixLeads.Count == 0)
        {
            UpdateStatus(MonitoringStatus.Running, "Синхронизация с Bitrix24 завершена: новых сделок для импорта не найдено.");
            return;
        }

        var existingIds = await repository.GetExistingBitrixEntityIdsAsync(
            bitrixLeads.Select(x => x.BitrixEntityId),
            cancellationToken);

        var imported = 0;
        foreach (var lead in bitrixLeads)
        {
            if (existingIds.Contains(lead.BitrixEntityId))
            {
                continue;
            }

            lead.PhoneNormalized = phoneNormalizer.Normalize(lead.PhoneRaw);
            await repository.SaveCandidateAsync(lead, cancellationToken);
            imported++;
        }

        await repository.AddLogAsync(new ProcessingLogItem
        {
            Level = "Info",
            Message = "Синхронизация Bitrix24 выполнена",
            Details = $"Импортировано сделок: {imported}"
        }, cancellationToken);
        UpdateStatus(MonitoringStatus.Running, $"Синхронизация с Bitrix24 завершена: импортировано сделок {imported}.");
    }

    internal void SetNextCheckTime(DateTime? utc)
    {
        if (_nextCheckTime == utc)
        {
            return;
        }

        _nextCheckTime = utc;
        NextCycleCheckAtUtc = utc;
        NextCycleCheckTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus(MonitoringStatus status, string message)
    {
        CurrentStatus = status;
        CurrentStatusMessage = message;
        StatusChanged?.Invoke(this, status);
        StatusMessageChanged?.Invoke(this, message);
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay.TotalMinutes >= 1)
        {
            return $"{(int)delay.TotalMinutes} мин. {delay.Seconds} сек.";
        }

        return $"{delay.Seconds} сек.";
    }

    private void StartCountdownTimer(TimeSpan initialDelay)
    {
        _countdownTimer?.Dispose();
        _countdownTimer = new System.Threading.Timer(_ =>
        {
            if (CurrentStatus == MonitoringStatus.Waiting && _nextCheckTime.HasValue)
            {
                var remaining = _nextCheckTime.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }
                var newMessage = $"Цикл завершён. Ждём следующую проверку {FormatDelay(remaining)}.";
                if (CurrentStatusMessage != newMessage)
                {
                    CurrentStatusMessage = newMessage;
                    // Всегда пытаемся обновить через UI-поток
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        dispatcher.Invoke(() => StatusMessageChanged?.Invoke(this, CurrentStatusMessage));
                    }
                    else
                    {
                        StatusMessageChanged?.Invoke(this, CurrentStatusMessage);
                    }
                }
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Собирает новые отклики для аккаунта; для AdsPower-аккаунта с несколькими суб-профилями
    /// поочерёдно переключается в каждый и склеивает результаты (с защитой от дублей по ключу источника).
    /// </summary>
    private async Task<IReadOnlyList<CandidateResponse>> CollectResponsesAcrossSubProfilesAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var subProfiles = account.ProfileProvider == AvitoProfileProvider.AdsPower
            ? account.SubProfiles
            : Array.Empty<AvitoSubProfile>();

        if (subProfiles.Count == 0)
        {
            return await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId) || string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl))
        {
            return await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken).ConfigureAwait(false);
        }

        var options = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        var aggregated = new List<CandidateResponse>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < subProfiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sub = subProfiles[i];

            try
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito Pro переключаем суб-профиль {i + 1}/{subProfiles.Count} (отклики) для {account.DisplayName}: {sub.Name} (id={sub.Id}).",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["subProfile.index"] = i + 1,
                        ["subProfile.total"] = subProfiles.Count
                    });

                await adsPowerAvitoAutomationService
                    .SwitchActiveProfileAsync(options, account.AdsPowerProfileId!, sub.Id, cancellationToken)
                    .ConfigureAwait(false);

                var batch = await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken).ConfigureAwait(false);
                foreach (var response in batch)
                {
                    var key = string.IsNullOrWhiteSpace(response.SourceResponseId)
                        ? $"{response.PhoneNormalized}|{response.FullName}|{response.Vacancy}"
                        : response.SourceResponseId;
                    if (seenKeys.Add(key))
                    {
                        aggregated.Add(response);
                    }
                }
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Не удалось обработать суб-профиль «{sub.Name}» (id={sub.Id}, отклики) аккаунта {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }

        return aggregated;
    }

    /// <summary>
    /// Парсит объявления для AdsPower-аккаунта. Если у аккаунта в Avito Pro несколько суб-профилей —
    /// поочерёдно переключаемся в каждый и суммируем счётчики/списки. Если суб-профилей нет —
    /// один проход по текущему активному профилю в браузере (старое поведение).
    /// </summary>
    private async Task<Avito.ProfileResult> CollectAdsPowerProfileStatsAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var subProfiles = account.SubProfiles;

        if (subProfiles.Count == 0)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Парсинг объявлений (AdsPower) запускается для аккаунта {account.DisplayName} без суб-профилей.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["accountId"] = account.Id,
                    ["accountName"] = account.DisplayName,
                    ["adsPower.userId"] = account.AdsPowerProfileId,
                    ["adsPower.baseUrl"] = options.BaseUrl
                });

            var html = await adsPowerAvitoAutomationService
                .LoadProfileItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(false);

            return avitoParser.ParseProfilePage(html, account.Id);
        }

        var aggregate = new Avito.ProfileResult();

        for (var i = 0; i < subProfiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sub = subProfiles[i];

            try
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito Pro переключаем суб-профиль {i + 1}/{subProfiles.Count} для {account.DisplayName}: {sub.Name} (id={sub.Id}).",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["subProfile.index"] = i + 1,
                        ["subProfile.total"] = subProfiles.Count
                    });

                await adsPowerAvitoAutomationService
                    .SwitchActiveProfileAsync(options, account.AdsPowerProfileId!, sub.Id, cancellationToken)
                    .ConfigureAwait(false);

                var html = await adsPowerAvitoAutomationService
                    .LoadProfileItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
                    .ConfigureAwait(false);

                var part = avitoParser.ParseProfilePage(html, account.Id);
                aggregate.ActiveCount += part.ActiveCount;
                aggregate.BlockedCount += part.BlockedCount;
                aggregate.DraftsCount += part.DraftsCount;
                aggregate.ActiveAds.AddRange(part.ActiveAds);

                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito Pro суб-профиль «{sub.Name}»: вакансий {part.ActiveAds.Count}, активные={part.ActiveCount}, blocked={part.BlockedCount}, drafts={part.DraftsCount}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["vacancyCount"] = part.ActiveAds.Count,
                        ["tabActiveAdsCount"] = part.ActiveCount,
                        ["blockedAdsCount"] = part.BlockedCount,
                        ["draftsCount"] = part.DraftsCount
                    });
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Не удалось обработать суб-профиль «{sub.Name}» (id={sub.Id}) аккаунта {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }

        return aggregate;
    }

    private async Task<string> LoadProfilePageHtmlAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        var session = await browserSessionService.CreateSessionAsync(account, cancellationToken);
        await using var host = await backgroundWebViewHostFactory.CreateAsync(cancellationToken);
        await host.AttachAsync(session, cancellationToken);
        await automationService.NavigateAsync(session, ProfileItemsUrl, cancellationToken);
        await WaitForProfilePageAsync(session, cancellationToken);
        var rawHtml = await automationService.ExecuteScriptAsync(
            session,
            "(() => document.documentElement.outerHTML ?? '')();",
            cancellationToken);
        var html = JsonSerializer.Deserialize<string>(rawHtml);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("Не удалось получить HTML страницы профиля Авито.");
        }

        return html;
    }

    private async Task WaitForProfilePageAsync(BrowserAccountSession session, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawState = await automationService.ExecuteScriptAsync(
                session,
                "(() => ({ readyState: document.readyState, bodyLength: (document.body?.innerText ?? '').trim().length }))();",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(rawState))
            {
                using var json = JsonDocument.Parse(rawState);
                var root = json.RootElement;
                var readyState = root.TryGetProperty("readyState", out var readyStateProp) ? readyStateProp.GetString() : null;
                var bodyLength = root.TryGetProperty("bodyLength", out var bodyLengthProp) ? bodyLengthProp.GetInt32() : 0;

                if (string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase) && bodyLength > 150)
                {
                    return;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException("Страница профиля Авито не успела загрузиться.");
    }

    private void UpdateActiveAdsSnapshot(Guid accountId, IReadOnlyList<AvitoAdStatus> ads)
    {
        lock (_activeAdsSync)
        {
            _activeAdsByAccount[accountId] = ads.Select(CloneAd).ToList();
        }
    }

    private static AvitoAdStatus CloneAd(AvitoAdStatus ad) => new()
    {
        AccountId = ad.AccountId,
        Id = ad.Id,
        Title = ad.Title,
        City = ad.City,
        Salary = ad.Salary,
        Views = ad.Views,
        Contacts = ad.Contacts,
        Favorites = ad.Favorites,
        Status = ad.Status,
        DeleteDate = ad.DeleteDate,
        DaysOnAvito = ad.DaysOnAvito
    };
}
