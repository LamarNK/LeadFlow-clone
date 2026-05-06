using System.Diagnostics;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
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
    IWebPageAutomationService automationService) : IMonitoringService
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
            $"Автообновление объявлений: следующий цикл через {(int)nextDelay.TotalMinutes} мин. (интервал из настроек MonitoringSafety.ActiveAdsRefreshIntervalMinutes).",
            DeskLinkAuditLogLevel.Info);
    }

    private async Task<TimeSpan> ScheduleNextProfileStatsUpdateAsync(CancellationToken ct)
    {
        var settings = await settingsService.LoadAsync(ct);
        var minutes = Math.Clamp(settings.MonitoringSafety.ActiveAdsRefreshIntervalMinutes, 5, 240);
        var nextDelay = TimeSpan.FromMinutes(minutes);
        _profileStatsTimer?.Change(nextDelay, Timeout.InfiniteTimeSpan);
        return nextDelay;
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

                var html = await LoadProfilePageHtmlAsync(account, ct);
                var profileData = avitoParser.ParseProfilePage(html, account.Id);
                UpdateActiveAdsSnapshot(account.Id, profileData.ActiveAds);
                await GlobalLogger.Instance.LogAsync(
                    () => $"Парсинг активных объявлений завершён для аккаунта {account.DisplayName}: на вкладке «Активные»={profileData.ActiveCount}, вакансий (раздел /rabota/)={profileData.ActiveAds.Count}, blocked={profileData.BlockedCount}, drafts={profileData.DraftsCount}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
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
                _ = GlobalLogger.Instance.LogAsync($"Ошибка обновления статистики объявлений для аккаунта {account.DisplayName}: {ex.Message}", DeskLinkAuditLogLevel.Warning);
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
                    foreach (var account in accounts)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        await ProcessAccountAsync(account, settings, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(settings.MonitoringSafety.DelayBetweenAccountsSeconds), cancellationToken);
                    }

                    cycleSw.Stop();
                    _ = GlobalLogger.Instance.LogAsync(
                        $"[monitoring] Цикл обхода аккаунтов завершён за {cycleSw.Elapsed.TotalSeconds:F1} с (аккаунтов: {accounts.Count}).",
                        DeskLinkAuditLogLevel.Info);

                    _consecutiveMonitoringLoopFailures = 0;

                    var delay = MonitoringCycleDelay.GetRandomDelay(settings.MonitoringSafety);
                    SetNextCheckTime(DateTime.UtcNow + delay);
                    UpdateStatus(MonitoringStatus.Waiting, $"Цикл завершён. Ждём следующую проверку {FormatDelay(delay)}.");
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

    internal async Task ProcessAccountAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
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
            return;
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
            var responses = settings.DemoModeEnabled
                ? await avitoDemoResponseSource.GetBatchAsync(account, Math.Max(1, settings.MonitoringSafety.MaxResponsesPerCycle / 2), cancellationToken)
                : await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken);
            var maxPerCycle = settings.MonitoringSafety.MaxResponsesPerCycle;
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
                await Task.Delay(TimeSpan.FromSeconds(settings.MonitoringSafety.DelayBetweenResponsesSeconds), cancellationToken);
            }

            if (account.Status == AvitoAccountStatus.Monitoring)
            {
                account.Status = AvitoAccountStatus.Authorized;
            }

            await repository.SaveAccountAsync(account, cancellationToken);
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

            UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" уникален: отправляем сделку в Bitrix24.");
            var lead = await bitrixClient.CreateLeadAsync(response, settings, cancellationToken);
            response.ProcessedAt = DateTime.UtcNow;
            if (lead.IsSuccess)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Bitrix deal created for response {response.Id}: {lead.EntityId}.",
                    DeskLinkAuditLogLevel.Info);
                response.Status = ResponseStatus.Sent;
                response.BitrixEntityId = lead.EntityId;
                if (!string.IsNullOrWhiteSpace(lead.ContactId))
                {
                    response.BitrixContactId = lead.ContactId;
                }

                await repository.AddLogAsync(new ProcessingLogItem
                {
                    CandidateResponseId = response.Id,
                    AccountId = response.AccountId,
                    Level = "Info",
                    Message = "Сделка создана в Bitrix24",
                    Details = lead.EntityId
                }, cancellationToken);
                UpdateStatus(MonitoringStatus.Running, $"Сделка по отклику \"{response.FullName}\" успешно создана в Bitrix24. ID: {lead.EntityId}.");
            }
            else
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Bitrix deal creation failed for response {response.Id}: {lead.Error}.",
                    DeskLinkAuditLogLevel.Error);
                if (!string.IsNullOrWhiteSpace(lead.ContactId))
                {
                    response.BitrixContactId = lead.ContactId;
                    response.Status = ResponseStatus.ActionRequired;
                    response.ErrorMessage = lead.Error;
                    await repository.AddLogAsync(new ProcessingLogItem
                    {
                        CandidateResponseId = response.Id,
                        AccountId = response.AccountId,
                        Level = "Warning",
                        Message = "Контакт в Bitrix24 без сделки — требуется действие",
                        Details = lead.Error
                    }, cancellationToken);
                    UpdateStatus(
                        MonitoringStatus.Running,
                        $"Отклик \"{response.FullName}\": контакт Bitrix24 создан, сделка не создана. Исправьте в портале или нажмите «Отправить в Bitrix24» для повторной попытки.");
                }
                else
                {
                    response.Status = ResponseStatus.Error;
                    response.ErrorMessage = lead.Error;
                    await repository.AddLogAsync(new ProcessingLogItem
                    {
                        CandidateResponseId = response.Id,
                        AccountId = response.AccountId,
                        Level = "Error",
                        Message = "Ошибка Bitrix24",
                        Details = lead.Error
                    }, cancellationToken);
                    UpdateStatus(MonitoringStatus.Error, $"Ошибка при создании сделки по отклику \"{response.FullName}\": {lead.Error}");
                }
            }

            await repository.SaveCandidateAsync(response, cancellationToken);
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
            if (response.Status == ResponseStatus.InProgress)
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
