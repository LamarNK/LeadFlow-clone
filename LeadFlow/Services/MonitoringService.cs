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
    private readonly Dictionary<Guid, IReadOnlyList<AvitoAdStatus>> _blockedAdsByAccount = [];
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

    /// <summary>
    /// Снимок заблокированных/«с ошибками» вакансий (вкладка <c>tab(rejected)</c>) по всем аккаунтам.
    /// Заполняется во время единого прохода вместе с активными объявлениями.
    /// </summary>
    public IReadOnlyList<AvitoAdStatus> GetBlockedAdsSnapshot()
    {
        lock (_activeAdsSync)
        {
            return _blockedAdsByAccount.Values
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
                if (account.ProfileProvider == AvitoProfileProvider.AdsPower)
                {
                    // Для AdsPower-аккаунтов объявления и отклики собираются единым проходом
                    // в RunAsync → StreamProcessAccountResponsesAsync (один switch на суб-профиль),
                    // чтобы не делать двойное переключение между вкладками. Здесь не дублируем.
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower-аккаунт {account.DisplayName}: парсинг объявлений идёт в общем цикле мониторинга, отдельный пробег пропускаем.",
                        DeskLinkAuditLogLevel.Debug,
                        properties: new Dictionary<string, object?>
                        {
                            ["accountId"] = account.Id,
                            ["accountName"] = account.DisplayName,
                            ["profileProvider"] = account.ProfileProvider.ToString()
                        });
                    continue;
                }

                var prev = new StatsPreviousCounts(account.ActiveAdsCount, account.BlockedCount, account.DraftsCount);

                var html = await LoadProfilePageHtmlAsync(account, ct);
                var profileData = avitoParser.ParseProfilePage(html, account.Id);
                await ApplyStatsSnapshotAsync(account, profileData, prev, ct).ConfigureAwait(false);

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
            }
            catch (AvitoCaptchaDetectedException captchaEx)
            {
                UpdateActiveAdsSnapshot(account.Id, []);
                await HandleCaptchaForAccountAsync(account, captchaEx, ct).ConfigureAwait(false);
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
            var maxPerCycle = MonitoringTiming.MaxResponsesPerAccountPerCycle;
            var (detectedTotal, backlog) = await StreamProcessAccountResponsesAsync(account, settings, maxPerCycle, cancellationToken).ConfigureAwait(false);

            if (account.Status == AvitoAccountStatus.Monitoring)
            {
                account.Status = AvitoAccountStatus.Authorized;
            }

            await repository.SaveAccountAsync(account, cancellationToken);

            return (detectedTotal, true, backlog);
        }
        catch (AvitoCaptchaDetectedException captchaEx)
        {
            // Avito показал firewall/капчу: переводим аккаунт в RequiresManualAction
            // и не возвращаемся к нему до ручного действия пользователя в этом цикле.
            await HandleCaptchaForAccountAsync(account, captchaEx, cancellationToken).ConfigureAwait(false);
            return (0, true, false);
        }
        finally
        {
            accountSw.Stop();
            _ = GlobalLogger.Instance.LogAsync(
                $"[monitoring] Аккаунт \"{account.DisplayName}\": этап проверки и обработки откликов за {accountSw.Elapsed.TotalSeconds:F1} с.",
                DeskLinkAuditLogLevel.Info);
        }
    }

    /// <summary>
    /// Применяет к аккаунту факт «Avito показал капчу/firewall»: статус, сообщение, лог + событие в UI.
    /// Используется и из верхнего уровня, и из вспомогательных путей (UpdateProfileStatsAsync), чтобы поведение
    /// для всех точек входа было одинаковым.
    /// </summary>
    private async Task HandleCaptchaForAccountAsync(
        AvitoAccount account,
        AvitoCaptchaDetectedException captchaEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresManualAction;
        account.LastErrorMessage = $"Avito показал капчу/блок IP ({captchaEx.Kind}). Откройте браузер и пройдите проверку.";

        try
        {
            await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Avito показал капчу/firewall",
                Details = $"{captchaEx.Kind} :: {captchaEx.Url ?? "<unknown url>"}"
            }, ct).ConfigureAwait(false);
        }
        catch (Exception persistEx)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Не удалось сохранить статус капчи для аккаунта {account.DisplayName}: {persistEx.Message}",
                DeskLinkAuditLogLevel.Error);
        }

        UpdateStatus(
            MonitoringStatus.RequiresManualAction,
            $"Аккаунт \"{account.DisplayName}\": Avito показал капчу/блок IP ({captchaEx.Kind}). Пройдите проверку в браузере, чтобы продолжить.");

        _ = GlobalLogger.Instance.LogAsync(
            $"Avito captcha/firewall detected for account {account.DisplayName} (kind={captchaEx.Kind}, url={captchaEx.Url ?? "<unknown>"}).",
            DeskLinkAuditLogLevel.Warning,
            properties: new Dictionary<string, object?>
            {
                ["accountId"] = account.Id,
                ["accountName"] = account.DisplayName,
                ["captcha.kind"] = captchaEx.Kind,
                ["captcha.url"] = captchaEx.Url
            });
    }

    /// <summary>
    /// Стримовая обработка: отклики каждого суб-профиля немедленно идут в <see cref="ProcessResponseAsync"/>
    /// (до перехода к следующему суб-профилю). Это убирает большой буфер «сначала собираем всё, потом обрабатываем»
    /// и сокращает задержку до отправки в Bitrix.
    /// </summary>
    /// <returns>(всего обнаружено новых откликов, превышен ли бюджет цикла).</returns>
    private async Task<(int Detected, bool Backlog)> StreamProcessAccountResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        int maxPerCycle,
        CancellationToken cancellationToken)
    {
        var processedInCycle = 0;
        var detectedTotal = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var budgetExhausted = false;

        // Локальная функция: получает пачку, дедуплицирует в рамках цикла, шлёт в обработку немедленно.
        async Task<int> ProcessBatchInlineAsync(IReadOnlyList<CandidateResponse> batch, string sourceLabel)
        {
            if (batch.Count == 0)
            {
                return 0;
            }

            var freshCount = 0;
            foreach (var response in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var key = string.IsNullOrWhiteSpace(response.SourceResponseId)
                    ? $"{response.PhoneNormalized}|{response.FullName}|{response.Vacancy}"
                    : response.SourceResponseId;

                if (!seenKeys.Add(key))
                {
                    continue;
                }

                freshCount++;
                detectedTotal++;

                if (processedInCycle >= maxPerCycle)
                {
                    budgetExhausted = true;
                    continue;
                }

                UpdateStatus(
                    MonitoringStatus.Running,
                    $"Аккаунт \"{account.DisplayName}\" ({sourceLabel}): обрабатываем отклик {processedInCycle + 1}/{maxPerCycle}.");

                await ProcessResponseAsync(response, settings, cancellationToken).ConfigureAwait(false);
                processedInCycle++;

                if (processedInCycle < maxPerCycle && !cancellationToken.IsCancellationRequested)
                {
                    // Рандомная пауза имитирует «человек прочитал отклик и переключается на следующий».
                    await HumanDelay.BetweenResponsesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return freshCount;
        }

        if (settings.DemoModeEnabled)
        {
            var demo = await avitoDemoResponseSource.GetBatchAsync(account, maxPerCycle, cancellationToken).ConfigureAwait(false);
            await ProcessBatchInlineAsync(demo, "demo").ConfigureAwait(false);
            return (detectedTotal, budgetExhausted || detectedTotal > maxPerCycle);
        }

        var subProfiles = account.ProfileProvider == AvitoProfileProvider.AdsPower
            ? account.SubProfiles
            : Array.Empty<AvitoSubProfile>();

        var hasAdsPowerCreds =
            !string.IsNullOrWhiteSpace(account.AdsPowerProfileId) &&
            !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

        if (subProfiles.Count == 0 || !hasAdsPowerCreds)
        {
            // Для AdsPower-аккаунтов без суб-профилей всё равно надо обновлять статистику объявлений
            // (раньше это делал _profileStatsTimer, теперь он скипает AdsPower, чтобы не было двойных переходов).
            if (hasAdsPowerCreds && IsAdsStatsStale(account))
            {
                try
                {
                    var optionsForStats = new AdsPowerConnectionOptions(
                        account.AdsPowerApiBaseUrl!,
                        string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

                    var prev = new StatsPreviousCounts(account.ActiveAdsCount, account.BlockedCount, account.DraftsCount);
                    var part = await CollectProfileItemsAsync(account, optionsForStats, cancellationToken).ConfigureAwait(false);
                    await ApplyStatsSnapshotAsync(account, part, prev, cancellationToken).ConfigureAwait(false);

                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower-аккаунт {account.DisplayName} (без суб-профилей): объявления обновлены инлайн ({part.ActiveAds.Count} активных, {part.BlockedAds.Count} заблокированных).",
                        DeskLinkAuditLogLevel.Info,
                        properties: new Dictionary<string, object?>
                        {
                            ["accountId"] = account.Id,
                            ["accountName"] = account.DisplayName,
                            ["activeAds"] = part.ActiveAds.Count,
                            ["blockedAds"] = part.BlockedAds.Count
                        });
                }
                catch (AvitoCaptchaDetectedException)
                {
                    // Капча — пробрасываем, чтобы ProcessAccountAsync поставил RequiresManualAction.
                    throw;
                }
                catch (Exception ex)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower-аккаунт {account.DisplayName} (без суб-профилей): не удалось собрать объявления инлайн: {ex.Message}",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["accountId"] = account.Id,
                            ["accountName"] = account.DisplayName,
                            ["error.type"] = ex.GetType().FullName
                        });
                }
            }

            UpdateStatus(MonitoringStatus.Running, $"Аккаунт \"{account.DisplayName}\": запрашиваем новые отклики.");
            var responses = await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken).ConfigureAwait(false);

            if (responses.Count > maxPerCycle)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"[monitoring] Аккаунт \"{account.DisplayName}\": новых откликов {responses.Count}, в этом цикле обрабатываем {maxPerCycle}; остальные подтянутся в следующих проверках.",
                    DeskLinkAuditLogLevel.Info);
            }
            else
            {
                UpdateStatus(MonitoringStatus.Running, $"Аккаунт \"{account.DisplayName}\" проверен: найдено новых откликов {responses.Count}.");
            }

            await ProcessBatchInlineAsync(responses, account.DisplayName).ConfigureAwait(false);
            return (detectedTotal, budgetExhausted || responses.Count > maxPerCycle);
        }

        var options = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        // Раз в ActiveAdsRefreshIntervalMinutes на этом же переключении тянем ещё и объявления
        // (active + rejected вкладки). Это убирает отдельный «двойной» switch на тот же суб-профиль —
        // один заход = и активные, и заблокированные, и отклики.
        var collectStats = IsAdsStatsStale(account);
        var statsAggregate = collectStats ? new Avito.ProfileResult() : null;
        var statsPrev = collectStats
            ? new StatsPreviousCounts(account.ActiveAdsCount, account.BlockedCount, account.DraftsCount)
            : null;

        if (collectStats)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Аккаунт \"{account.DisplayName}\": статистика объявлений устарела — соберём вместе с откликами в этом цикле.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["accountId"] = account.Id,
                    ["accountName"] = account.DisplayName,
                    ["adsStatsUpdatedAt"] = account.AdsStatsUpdatedAt?.ToString("O") ?? "<null>",
                    ["refreshIntervalMinutes"] = MonitoringTiming.ActiveAdsRefreshIntervalMinutes
                });
        }

        for (var i = 0; i < subProfiles.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested || budgetExhausted)
            {
                break;
            }

            var sub = subProfiles[i];
            var subLabel = $"{sub.Name} {i + 1}/{subProfiles.Count}";

            try
            {
                UpdateStatus(
                    MonitoringStatus.Running,
                    $"Аккаунт \"{account.DisplayName}\": переключаемся на суб-профиль «{sub.Name}» ({i + 1}/{subProfiles.Count}).");

                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito Pro переключаем суб-профиль {i + 1}/{subProfiles.Count} (отклики{(collectStats ? "+объявления" : string.Empty)}, стрим) для {account.DisplayName}: {sub.Name} (id={sub.Id}).",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["subProfile.index"] = i + 1,
                        ["subProfile.total"] = subProfiles.Count,
                        ["collectStats"] = collectStats
                    });

                await adsPowerAvitoAutomationService
                    .SwitchActiveProfileAsync(options, account.AdsPowerProfileId!, sub.Id, cancellationToken)
                    .ConfigureAwait(false);

                // 1️⃣ Объявления — только если кэш статистики устарел (раз в ActiveAdsRefreshIntervalMinutes).
                if (collectStats && statsAggregate is not null && statsPrev is not null)
                {
                    try
                    {
                        UpdateStatus(
                            MonitoringStatus.Running,
                            $"Аккаунт \"{account.DisplayName}\": собираем объявления — суб-профиль «{sub.Name}» ({i + 1}/{subProfiles.Count}).");

                        var part = await CollectProfileItemsAsync(account, options, cancellationToken).ConfigureAwait(false);
                        statsAggregate.ActiveCount += part.ActiveCount;
                        statsAggregate.BlockedCount += part.BlockedCount;
                        statsAggregate.DraftsCount += part.DraftsCount;
                        statsAggregate.ActiveAds.AddRange(part.ActiveAds);
                        statsAggregate.BlockedAds.AddRange(part.BlockedAds);

                        _ = GlobalLogger.Instance.LogAsync(
                            $"Суб-профиль «{sub.Name}» (объявления): активных {part.ActiveAds.Count}, заблокированных {part.BlockedAds.Count}, drafts={part.DraftsCount}.",
                            DeskLinkAuditLogLevel.Info,
                            properties: new Dictionary<string, object?>
                            {
                                ["accountId"] = account.Id,
                                ["accountName"] = account.DisplayName,
                                ["subProfile.id"] = sub.Id,
                                ["subProfile.name"] = sub.Name,
                                ["activeAds"] = part.ActiveAds.Count,
                                ["blockedAds"] = part.BlockedAds.Count
                            });

                        await ApplyStatsSnapshotAsync(account, statsAggregate, statsPrev, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AvitoCaptchaDetectedException)
                    {
                        // Не глушим: пробрасываем в верхний catch, который завершит обход аккаунта.
                        throw;
                    }
                    catch (Exception statsEx)
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            $"Сбор объявлений суб-профиля «{sub.Name}» аккаунта {account.DisplayName} не удался: {statsEx.Message}",
                            DeskLinkAuditLogLevel.Warning,
                            properties: new Dictionary<string, object?>
                            {
                                ["accountId"] = account.Id,
                                ["accountName"] = account.DisplayName,
                                ["subProfile.id"] = sub.Id,
                                ["subProfile.name"] = sub.Name,
                                ["error.type"] = statsEx.GetType().FullName
                            });
                    }
                }

                // 2️⃣ Отклики — всегда. Берём с того же суб-профиля, на который только что переключились.
                var batch = await avitoResponseSource
                    .GetNewResponsesAsync(account, settings, cancellationToken)
                    .ConfigureAwait(false);

                var freshInBatch = await ProcessBatchInlineAsync(batch, subLabel).ConfigureAwait(false);

                _ = GlobalLogger.Instance.LogAsync(
                    $"Суб-профиль «{sub.Name}» аккаунта {account.DisplayName}: получено откликов {batch.Count}, новых после дедупа {freshInBatch}, обработано в цикле {processedInCycle}/{maxPerCycle}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["subProfile.id"] = sub.Id,
                        ["subProfile.name"] = sub.Name,
                        ["batchTotal"] = batch.Count,
                        ["freshInBatch"] = freshInBatch,
                        ["processedInCycle"] = processedInCycle,
                        ["maxPerCycle"] = maxPerCycle
                    });
            }
            catch (AvitoCaptchaDetectedException)
            {
                // Капча/firewall — выбрасываем наверх, остальные суб-профили этого аккаунта не трогаем.
                throw;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Не удалось обработать суб-профиль «{sub.Name}» (id={sub.Id}, отклики, стрим) аккаунта {account.DisplayName}: {ex.Message}",
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

            // Рандомная пауза между суб-профилями: «человек посмотрел один кабинет, переключился на следующий».
            // Не делаем после последнего, чтобы не задерживать общий цикл мониторинга зря.
            if (i < subProfiles.Count - 1 && !cancellationToken.IsCancellationRequested && !budgetExhausted)
            {
                await HumanDelay.BetweenSubProfilesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (detectedTotal, budgetExhausted);
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
    /// Загружает обе вкладки кабинета («Активные» и «С ошибками») на текущем суб-профиле AdsPower.
    /// Если на вкладке «Активные» счётчик rejected = 0, по rejected не ходим — экономим переход.
    /// </summary>
    private async Task<Avito.ProfileResult> CollectProfileItemsAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var activeHtml = await adsPowerAvitoAutomationService
            .LoadProfileItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
            .ConfigureAwait(false);

        var part = avitoParser.ParseProfilePage(activeHtml, account.Id);

        if (part.BlockedCount > 0)
        {
            try
            {
                var blockedHtml = await adsPowerAvitoAutomationService
                    .LoadBlockedItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
                    .ConfigureAwait(false);

                var blocked = avitoParser.ParseBlockedTabPage(blockedHtml, account.Id);
                part.BlockedAds.AddRange(blocked);
            }
            catch (AvitoCaptchaDetectedException)
            {
                // Капча на вкладке «С ошибками» — пробрасываем наверх.
                throw;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Не удалось загрузить вкладку «С ошибками» для аккаунта {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["accountId"] = account.Id,
                        ["accountName"] = account.DisplayName,
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }

        return part;
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

        var captchaKind = AvitoCaptchaDetector.Classify(html);
        if (captchaKind is not null)
        {
            throw new AvitoCaptchaDetectedException(captchaKind, ProfileItemsUrl, html);
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

    private void UpdateBlockedAdsSnapshot(Guid accountId, IReadOnlyList<AvitoAdStatus> ads)
    {
        lock (_activeAdsSync)
        {
            _blockedAdsByAccount[accountId] = ads.Select(CloneAd).ToList();
        }
    }

    /// <summary>
    /// Истёк ли «возраст» закэшированной статистики объявлений: если да — на ближайшем переключении
    /// в суб-профиль соберём active+rejected вкладки. Иначе тратим переключение только на отклики.
    /// </summary>
    private static bool IsAdsStatsStale(AvitoAccount account)
    {
        var last = account.AdsStatsUpdatedAt;
        if (last is null) return true;
        var age = DateTime.UtcNow - last.Value;
        return age >= TimeSpan.FromMinutes(MonitoringTiming.ActiveAdsRefreshIntervalMinutes);
    }

    /// <summary>
    /// Атомарно применяет агрегированный snapshot к аккаунту: snapshot в памяти, запись в БД, событие в UI.
    /// Используется и при отдельном сборе по таймеру, и в едином проходе с откликами.
    /// </summary>
    private async Task ApplyStatsSnapshotAsync(
        AvitoAccount account,
        Avito.ProfileResult snapshot,
        StatsPreviousCounts prev,
        CancellationToken ct)
    {
        UpdateActiveAdsSnapshot(account.Id, snapshot.ActiveAds);
        UpdateBlockedAdsSnapshot(account.Id, snapshot.BlockedAds);
        account.ActiveAdsCount = snapshot.ActiveAds.Count;
        // Если вкладку «С ошибками» не открывали — берём счётчик из вкладки активных (он там тоже виден).
        // Если открыли — точное число распарсенных карточек.
        account.BlockedCount = snapshot.BlockedAds.Count > 0 ? snapshot.BlockedAds.Count : snapshot.BlockedCount;
        account.DraftsCount = snapshot.DraftsCount;
        account.AdsStatsUpdatedAt = DateTime.UtcNow;
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);

        ProfileStatsUpdated?.Invoke(this, new ProfileStatsUpdatedEventArgs
        {
            Account = account,
            PreviousActiveAdsCount = prev.Active,
            PreviousBlockedCount = prev.Blocked,
            PreviousDraftsCount = prev.Drafts
        });

        // На следующем сабпрофиле prev-значения должны равняться только что-сохранённому состоянию,
        // иначе UI считает «приращением» весь накопленный список.
        prev.Active = account.ActiveAdsCount;
        prev.Blocked = account.BlockedCount;
        prev.Drafts = account.DraftsCount;
    }

    /// <summary>Mutable-контейнер с предыдущими счётчиками для инкрементальных snapshot-ов в одном цикле.</summary>
    private sealed class StatsPreviousCounts(int active, int blocked, int drafts)
    {
        public int Active { get; set; } = active;
        public int Blocked { get; set; } = blocked;
        public int Drafts { get; set; } = drafts;
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
