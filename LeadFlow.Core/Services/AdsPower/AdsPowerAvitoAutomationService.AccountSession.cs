using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Avito.Session;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.LocalChrome;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.AdsPower;

public sealed partial class AdsPowerAvitoAutomationService
{
    // Этот лимит охватывает весь старт: Local API, CDP и прогрев вкладки.
    // Отдельного лимита CDP недостаточно: зависание до BeginSubProfile иначе
    // удерживает слот мониторинга бесконечно.
    private static readonly TimeSpan AccountSessionStartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BrowserStopTimeout = TimeSpan.FromSeconds(15);

    public async Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default) =>
        await OpenAccountSessionAsync(options, adsPowerUserId, reportStartupStage: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        Action<string, TimeSpan>? reportStartupStage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(AccountSessionStartupTimeout);
            var startupToken = startupCts.Token;
            using var trace = AdsPowerStartupTrace.Begin(adsPowerUserId, attempt).Activate();
            var startupTask = OpenAccountSessionOnceAsync(
                options,
                adsPowerUserId,
                attempt,
                reportStartupStage,
                trace,
                startupToken);

            try
            {
                return await startupTask.WaitAsync(startupToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
                when (!cancellationToken.IsCancellationRequested && startupCts.IsCancellationRequested)
            {
                // Некоторые вызовы PuppeteerSharp не реагируют на CancellationToken.
                // Освобождаем слот немедленно, а результат позднего старта обязательно
                // дочищаем в фоне, чтобы не оставить окно AdsPower открытым.
                var timeoutEx = new TimeoutException(
                    $"AdsPower: запуск сессии не завершился за {AccountSessionStartupTimeout.TotalMinutes:0} мин.",
                    ex);
                AdsPowerStartupDiagnostics.TryLog(
                    trace.RecordFailure(timeoutEx),
                    memberName: nameof(OpenAccountSessionAsync));
                _ = CleanupTimedOutSessionAsync(startupTask, options, adsPowerUserId);
                throw timeoutEx;
            }
            catch (Exception ex) when (IsRetryableAdsPowerStartupFailure(ex) && attempt < 2)
            {
                lastError = ex;
                AdsPowerStartupDiagnostics.TryLog(
                    trace.RecordFailure(ex),
                    memberName: nameof(OpenAccountSessionAsync));
                var retryProperties = new Dictionary<string, object?>
                {
                    ["step"] = "session_retry",
                    ["attempt"] = attempt,
                    ["adsPower.userId"] = adsPowerUserId,
                    ["error.type"] = ex.GetType().FullName
                };
                AdsPowerStartupDiagnostics.TryCopyIdentity(retryProperties);
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower account session: попытка {attempt} не открыла Avito ({AdsPowerStartupLogSanitizer.LimitText(ex.Message)}), перезапускаем браузер.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(OpenAccountSessionAsync),
                    properties: retryProperties);
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("AdsPower: не удалось открыть сессию аккаунта.");
    }

    public async Task<IAdsPowerAccountSession> OpenAccountSessionOnConnectedBrowserAsync(
        IBrowser browser,
        string sessionKey,
        Action<string, TimeSpan>? reportStartupStage = null,
        CancellationToken cancellationToken = default,
        string runtimeProvider = "Multilogin",
        LocalChromeTrafficPolicy? trafficPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var provider = string.IsNullOrWhiteSpace(runtimeProvider) ? "Multilogin" : runtimeProvider.Trim();

        var startupStopwatch = Stopwatch.StartNew();
        ReportStartupStage(reportStartupStage, 1, "поиск рабочей вкладки", startupStopwatch);
        var page = await AcquireAutomationPageAsync(
                browser,
                ProfileItemsPageUrl,
                nameof(OpenAccountSessionOnConnectedBrowserAsync),
                cancellationToken,
                waitForStartupNavigation: true)
            .ConfigureAwait(false);
        if (trafficPolicy is not null)
        {
            await trafficPolicy.AttachAsync(page, cancellationToken).ConfigureAwait(false);
        }

        ReportStartupStage(reportStartupStage, 1, "вкладка получена", startupStopwatch);
        ReportStartupStage(reportStartupStage, 1, "прогрев страницы Avito", startupStopwatch);
        page = await WarmUpSessionPageAsync(
                page,
                sessionKey,
                cancellationToken,
                runtimeProvider: provider,
                trafficPolicy: trafficPolicy)
            .ConfigureAwait(false);
        ReportStartupStage(reportStartupStage, 1, "страница Avito готова", startupStopwatch);
        return new AccountSession(this, browser, page, sessionKey, new GeeTestV4TaskOptions(), provider);
    }

    private async Task<IAdsPowerAccountSession> OpenAccountSessionOnceAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        int attempt,
        Action<string, TimeSpan>? reportStartupStage,
        AdsPowerStartupTrace trace,
        CancellationToken cancellationToken)
    {
        var started = false;
        IBrowser? browser = null;
        var startupStopwatch = Stopwatch.StartNew();
        try
        {
            ReportStartupStage(reportStartupStage, attempt, "ожидание очереди AdsPower browser/start", startupStopwatch, trace);
            var start = await adsPowerApiClient
                .StartBrowserAsync(options, adsPowerUserId, ProfileItemsPageUrl, cancellationToken)
                .ConfigureAwait(false);
            started = true;
            ReportStartupStage(reportStartupStage, attempt, "browser/start завершён", startupStopwatch, trace);

            if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
            {
                throw new InvalidOperationException(
                    "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
            }

            ReportStartupStage(reportStartupStage, attempt, "подключение CDP", startupStopwatch, trace);
            browser = await ConnectAdsPowerBrowserAsync(
                    new ConnectOptions
                    {
                        BrowserWSEndpoint = start.WebSocketDebuggerUrl,
                        DefaultViewport = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            ReportStartupStage(reportStartupStage, attempt, "CDP подключён", startupStopwatch, trace);

            ReportStartupStage(reportStartupStage, attempt, "чтение прокси профиля", startupStopwatch, trace);
            var captchaOptions = await ResolveCaptchaTaskOptionsAsync(options, adsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
            ReportStartupStage(reportStartupStage, attempt, "прокси профиля прочитан", startupStopwatch, trace);
            using var captchaScope = AvitoCaptchaTaskContext.Use(captchaOptions);

            ReportStartupStage(reportStartupStage, attempt, "поиск рабочей вкладки", startupStopwatch, trace);
            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileItemsPageUrl,
                    nameof(OpenAccountSessionAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);
            ReportStartupStage(reportStartupStage, attempt, "вкладка получена", startupStopwatch, trace);

            ReportStartupStage(reportStartupStage, attempt, "прогрев страницы Avito", startupStopwatch, trace);
            page = await WarmUpSessionPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);
            ReportStartupStage(reportStartupStage, attempt, "страница Avito готова", startupStopwatch, trace);

            var openedProperties = new Dictionary<string, object?>
            {
                ["step"] = "session_opened",
                ["attempt"] = attempt,
                ["adsPower.userId"] = adsPowerUserId,
                ["page.urlClass"] = ClassifyAutomationPageUrl(page.Url),
                ["captcha.proxyMode"] = captchaOptions.UsesSuppliedProxy ? "profile" : "proxyless"
            };
            AdsPowerStartupDiagnostics.TryCopyIdentity(openedProperties);
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower account session opened for user {adsPowerUserId}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(OpenAccountSessionAsync),
                properties: openedProperties);

            return new AccountSession(this, browser, page, adsPowerUserId, captchaOptions, "AdsPower");
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                AdsPowerStartupDiagnostics.TryLog(
                    trace.RecordFailure(ex),
                    memberName: nameof(OpenAccountSessionOnceAsync));
            }
            if (browser is not null)
            {
                try
                {
                    browser.Disconnect();
                }
                catch
                {
                    // ignore cleanup errors
                }
            }

            if (started)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(BrowserStopTimeout);
                    await adsPowerApiClient.StopBrowserAsync(options, adsPowerUserId, stopCts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // окно могли уже закрыть вручную
                }
            }

            throw;
        }
    }

    private static void ReportStartupStage(
        Action<string, TimeSpan>? reportStartupStage,
        int attempt,
        string stage,
        Stopwatch stopwatch,
        AdsPowerStartupTrace? trace = null)
    {
        try
        {
            trace?.SetStage(stage);
        }
        catch
        {
            // Диагностика не должна останавливать мониторинг.
        }

        try
        {
            reportStartupStage?.Invoke($"попытка {attempt}: {stage}", stopwatch.Elapsed);
        }
        catch
        {
            // Диагностика не должна останавливать мониторинг.
        }
    }

    private async Task CleanupTimedOutSessionAsync(
        Task<IAdsPowerAccountSession> startupTask,
        AdsPowerConnectionOptions options,
        string adsPowerUserId)
    {
        try
        {
            using var stopCts = new CancellationTokenSource(BrowserStopTimeout);
            await adsPowerApiClient.StopBrowserAsync(options, adsPowerUserId, stopCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Local API мог уже остановить окно после отмены стартовой задачи.
        }

        try
        {
            await using var lateSession = await startupTask.ConfigureAwait(false);
        }
        catch
        {
            // Ошибка позднего старта уже обработана основной попыткой.
        }
    }

    private async Task<IPage> WarmUpSessionPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        string runtimeProvider = "AdsPower",
        LocalChromeTrafficPolicy? trafficPolicy = null)
    {
        var traffic = trafficPolicy ?? LocalChromeTrafficPolicy.ForPage(page);
        async Task<IPage> KeepTrafficAsync(IPage next)
        {
            if (traffic is not null)
            {
                await traffic.AttachAsync(next, cancellationToken).ConfigureAwait(false);
            }

            return next;
        }

        await TryBringAutomationPageToFrontAsync(
                page,
                CdpPageDiscoveryTimeout,
                "BringToFront прогрева вкладки",
                cancellationToken)
            .ConfigureAwait(false);

        var currentUrl = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
        if (IsReusableStartupPlaceholderUrl(currentUrl) || !IsUsableWorkerPageUrl(currentUrl))
        {
            page = await KeepTrafficAsync(
                    await NavigateOffStartupPlaceholderAsync(
                            page,
                            ProfileItemsPageUrl,
                            nameof(WarmUpSessionPageAsync),
                            cancellationToken)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);
            currentUrl = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (!IsAvitoProfileAutomationTab(currentUrl))
        {
            await TryCdpPageNavigateAsync(page, ProfileItemsPageUrl, cancellationToken).ConfigureAwait(false);
            page = await KeepTrafficAsync(
                    await PollUntilAvitoPageAsync(page, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            currentUrl = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (IsReusableStartupPlaceholderUrl(currentUrl) || !IsUsableWorkerPageUrl(currentUrl))
        {
            throw new InvalidOperationException(
                $"AdsPower: после прогрева вкладка осталась на «{currentUrl}», Avito не открылся.");
        }

        // Вход должен происходить прямо после старта AdsPower. Раньше recovery
        // вызывался только позже, при переключении субпрофиля: браузер уже
        // показывал users-list/login-form, но до этого шага поток не доходил.
        var warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        var captchaSeen = CanTryClearCaptcha(warmupState)
                          || warmupState?.HasCaptcha == true
                          || warmupState?.PageKind == AvitoPageKind.Captcha;
        if (CanTryClearCaptcha(warmupState)
            && await TryClearGeeTestCaptchaAsync(page, cancellationToken).ConfigureAwait(false))
        {
            await HumanDelay.AroundAsync(MonitoringTiming.AutoLoginDashboardNavSettleMs, cancellationToken).ConfigureAwait(false);
            warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (warmupState?.IsTransientPageError == true)
        {
            await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(WarmUpSessionPageAsync))
                .ConfigureAwait(false);
            warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        }

        // После GeeTest Avito часто отдаёт гостя на /profile/pro/items без формы входа
        // в первом probe (Reload ещё не дорисовался). Не пропускаем автовход.
        if (warmupState?.HasLoginForm == true
            || warmupState?.PageKind == AvitoPageKind.Login
            || AvitoAutomationFailureFormatter.SuggestsLogin(warmupState)
            || (captchaSeen && AvitoAutomationFailureFormatter.ShouldAttemptAutoLoginAfterCaptcha(warmupState)))
        {
            _ = GlobalLogger.Instance.LogAsync(
                captchaSeen
                    ? "Avito session warmup: after captcha session is not logged in — starting auto-login."
                    : "Avito session warmup: login required — starting auto-login.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(WarmUpSessionPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "warmup_auto_login",
                    ["runtime.provider"] = runtimeProvider,
                    ["page.url"] = page.Url,
                    ["page.kind"] = warmupState?.PageKind.ToString(),
                    ["auth.hasLoginForm"] = warmupState?.HasLoginForm,
                    ["captcha.seen"] = captchaSeen
                });
            var recovered = await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false);
            warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (recovered && !IsOnActiveProfileItemsPage(page.Url))
            {
                await page.GoToAsync(ProfileItemsPageUrl, MonitoringNavigation(page, 90_000)).ConfigureAwait(false);
                warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            }
        }

        var switchModalBlockingItems =
            warmupState?.ProfileSwitchModalOpen == true
            || warmupState?.PageKind == AvitoPageKind.ProfileSwitchModal
            || AvitoSubProfileSwitchEffect.IsItemsSwitchTrapUrl(currentUrl)
            || AvitoSubProfileSwitchEffect.IsItemsSwitchTrapUrl(page.Url);

        if (switchModalBlockingItems)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Avito session warmup: profile-switch modal is open; skipping items shell wait.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(WarmUpSessionPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "warmup_skip_items_shell_switch_modal",
                    ["runtime.provider"] = runtimeProvider,
                    ["runtime.profileId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["switch.cardsCount"] = warmupState?.ProfileCardsCount,
                    ["switch.currentId"] = warmupState?.CurrentSubProfileId
                });
        }
        else if (IsOnActiveProfileItemsPage(page.Url)
            && warmupState?.HasCaptcha != true
            && warmupState?.PageKind != AvitoPageKind.Captcha)
        {
            await WaitForProfileItemsShellAsync(page, nameof(WarmUpSessionPageAsync), cancellationToken)
                .ConfigureAwait(false);
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower session warmup completed for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(WarmUpSessionPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "session_warmup_done",
                ["runtime.provider"] = runtimeProvider,
                ["runtime.profileId"] = adsPowerUserId,
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = page.Url
            });

        return page;
    }

    private async Task<SubProfileSwitchResult> SwitchSubProfileOnPageAsync(
        IPage page,
        string subProfileId,
        string adsPowerUserId,
        string runtimeProvider,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return new SubProfileSwitchResult(SubProfileSwitchStatus.Unknown, "empty-id");
        }

        await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);

        SubProfileSwitchResult last = new(SubProfileSwitchStatus.Unknown);
        for (var attempt = 1; attempt <= MonitoringTiming.SubProfileSwitchMaxAttempts; attempt++)
        {
            last = await TrySwitchSubProfileOnPageOnceAsync(
                    page,
                    subProfileId,
                    adsPowerUserId,
                    runtimeProvider,
                    cancellationToken,
                    orchestrator)
                .ConfigureAwait(false);
            if (last.Ok)
            {
                return last;
            }

            if (last.IsCaptcha)
            {
                var postFailState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (CanTryClearCaptcha(postFailState)
                    && await TryClearGeeTestCaptchaAsync(page, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                return last;
            }

            if (last.IsLogin)
            {
                return last;
            }

            if (attempt >= MonitoringTiming.SubProfileSwitchMaxAttempts)
            {
                break;
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"{runtimeProvider} profile-switch (session): attempt {attempt}/{MonitoringTiming.SubProfileSwitchMaxAttempts} failed for subProfile {subProfileId}, recovering before retry.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switch_retry",
                    ["attempt"] = attempt,
                    ["runtime.provider"] = runtimeProvider,
                    ["runtime.profileId"] = adsPowerUserId,
                    ["adsPower.userId"] = adsPowerUserId,
                    ["avito.subProfileId"] = subProfileId,
                    ["switch.status"] = last.Status.ToString(),
                    ["page.url"] = page.Url
                });

            await RecoverPageBeforeSubProfileSwitchRetryAsync(page, cancellationToken, attempt, runtimeProvider)
                .ConfigureAwait(false);
        }

        return last;
    }

    private async Task RecoverPageBeforeSubProfileSwitchRetryAsync(
        IPage page,
        CancellationToken cancellationToken,
        int attempt,
        string runtimeProvider)
    {
        await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);
        await DismissAvitoBlockingOverlaysAsync(page, cancellationToken).ConfigureAwait(false);
        await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
        await BounceToDashboardBeforeSwitchAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);
        await Task.Delay(attempt * 1200, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientAvitoError(AvitoPageState? pageState) =>
        pageState?.IsTransientPageError == true;

    /// <summary>
    /// Заглушка Avito «Ошибка / обновите страницу»: сначала клик «Обновить», затем Reload.
    /// Прокси AdsPower часто отвисает после пары обновлений.
    /// </summary>
    private async Task<bool> TryRecoverTransientAvitoErrorAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        var state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (!IsTransientAvitoError(state))
        {
            return true;
        }

        for (var attempt = 1; attempt <= MonitoringTiming.TransientErrorReloadMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clickedRefresh = false;
            if (attempt == 1)
            {
                clickedRefresh = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                        page,
                        AvitoPageStateScripts.BuildClickRefreshOnTransientErrorScript(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!clickedRefresh)
            {
                await TryReloadPageForTransientErrorAsync(page, cancellationToken).ConfigureAwait(false);
            }

            _ = GlobalLogger.Instance.LogAsync(
                clickedRefresh
                    ? $"AdsPower: Avito error page — clicked «Обновить» ({attempt}/{MonitoringTiming.TransientErrorReloadMaxAttempts})."
                    : $"AdsPower: Avito error page — reloaded tab ({attempt}/{MonitoringTiming.TransientErrorReloadMaxAttempts}), proxy may be stuck.",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "transient_error_reload",
                    ["attempt"] = attempt,
                    ["reload.clickedRefresh"] = clickedRefresh,
                    ["page.url"] = page.Url
                });

            await HumanDelay.AroundAsync(MonitoringTiming.TransientErrorReloadSettleMs, cancellationToken)
                .ConfigureAwait(false);

            state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (!IsTransientAvitoError(state))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower: Avito recovered after refresh of error page.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: callerMemberName,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "transient_error_recovered",
                        ["attempt"] = attempt,
                        ["page.url"] = page.Url
                    });
                return true;
            }
        }

        return false;
    }

    private static async Task TryReloadPageForTransientErrorAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.ReloadAsync(MonitoringTiming.TransientErrorReloadTimeoutMs).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            try
            {
                await page.ReloadAsync(MonitoringTiming.TransientErrorReloadTimeoutMs).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (retryEx is not OperationCanceledException)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower: reload of Avito error page failed: {retryEx.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(TryReloadPageForTransientErrorAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "transient_error_reload_failed",
                        ["page.url"] = page.Url,
                        ["error.type"] = retryEx.GetType().FullName,
                        ["error.first"] = ex.Message
                    });
            }
        }
    }

    private static bool CanTryClearCaptcha(AvitoPageState? pageState) =>
        pageState?.HasFirewallIp != true
        && (pageState?.HasCaptcha == true || pageState?.PageKind == AvitoPageKind.Captcha);

    private async Task<SubProfileSwitchResult> TrySwitchSubProfileOnPageOnceAsync(
        IPage page,
        string subProfileId,
        string adsPowerUserId,
        string runtimeProvider,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"{runtimeProvider} profile-switch click started (session): subProfile={subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TrySwitchSubProfileOnPageOnceAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["runtime.provider"] = runtimeProvider,
                ["runtime.profileId"] = adsPowerUserId,
                ["adsPower.userId"] = adsPowerUserId,
                ["avito.subProfileId"] = subProfileId,
                ["page.url"] = page.Url
            });

        if (!IsAvitoProfileAutomationTab(page.Url))
        {
            page = await WarmUpSessionPageAsync(
                    page,
                    "session",
                    cancellationToken,
                    runtimeProvider)
                .ConfigureAwait(false);
        }

        await TryBringAutomationPageToFrontAsync(
                page,
                CdpSwitchActionTimeout,
                "BringToFront вкладки Avito",
                cancellationToken)
            .ConfigureAwait(false);

        await DismissAvitoBlockingOverlaysAsync(page, cancellationToken).ConfigureAwait(false);

        var preSwitchState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (preSwitchState?.HasLoginForm == true || preSwitchState?.PageKind == AvitoPageKind.Login)
        {
            if (await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"{runtimeProvider} profile-switch (session): auto-login recovered before switch for subProfile {subProfileId}.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(SwitchSubProfileOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "login_recovered",
                        ["avito.subProfileId"] = subProfileId,
                        ["page.url"] = page.Url
                    });
            }
            else
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"{runtimeProvider} profile-switch (session): login page detected for subProfile {subProfileId}, skipping.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(SwitchSubProfileOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "login_page",
                        ["avito.subProfileId"] = subProfileId,
                        ["page.url"] = page.Url,
                        ["pageState"] = preSwitchState.DescribeForDiagnostics()
                    });
                return new SubProfileSwitchResult(SubProfileSwitchStatus.Login);
            }
        }

        if (preSwitchState?.HasCaptcha == true || preSwitchState?.PageKind == AvitoPageKind.Captcha)
        {
            if (CanTryClearCaptcha(preSwitchState)
                && await TryClearGeeTestCaptchaAsync(page, cancellationToken).ConfigureAwait(false))
            {
                preSwitchState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            }

            if (preSwitchState?.HasCaptcha == true || preSwitchState?.PageKind == AvitoPageKind.Captcha)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"{runtimeProvider} profile-switch (session): captcha/firewall detected for subProfile {subProfileId}, skipping.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(SwitchSubProfileOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = preSwitchState?.HasFirewallIp == true ? "firewall_ip" : "captcha",
                        ["avito.subProfileId"] = subProfileId,
                        ["page.url"] = page.Url,
                        ["pageState"] = preSwitchState?.DescribeForDiagnostics()
                    });
                return new SubProfileSwitchResult(
                    preSwitchState?.HasFirewallIp == true
                        ? SubProfileSwitchStatus.IpBlock
                        : SubProfileSwitchStatus.Captcha);
            }
        }

        if (IsOnCandidatesResponsesPage(page.Url))
        {
            await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
                .ConfigureAwait(false);
        }

        await EnsureSwitchModalAsync(
                page,
                cancellationToken,
                nameof(SwitchSubProfileOnPageAsync),
                runtimeProvider,
                adsPowerUserId)
            .ConfigureAwait(false);
        if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
                .ConfigureAwait(false))
        {
            await ThrowIfCaptchaOnPageAsync(page, cancellationToken).ConfigureAwait(false);
            await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"{runtimeProvider} profile-switch (session): modal not ready for subProfile {subProfileId}, skipping.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switch_modal_not_ready",
                    ["runtime.provider"] = runtimeProvider,
                    ["runtime.profileId"] = adsPowerUserId,
                    ["avito.subProfileId"] = subProfileId,
                    ["page.url"] = page.Url
                });
            return new SubProfileSwitchResult(SubProfileSwitchStatus.ModalNotReady);
        }

        if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId, cancellationToken).ConfigureAwait(false))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"{runtimeProvider} profile-switch (session): subProfile {subProfileId} already current.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "already_current",
                    ["runtime.provider"] = runtimeProvider,
                    ["runtime.profileId"] = adsPowerUserId,
                    ["adsPower.userId"] = adsPowerUserId,
                    ["avito.subProfileId"] = subProfileId
                });
            return SubProfileSwitchResult.Succeeded;
        }

        var switched = await TryClickSubProfileCardAndWaitCloseAsync(
                page,
                subProfileId,
                adsPowerUserId,
                cancellationToken,
                runtimeProvider)
            .ConfigureAwait(false);
        if (!switched)
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
            return new SubProfileSwitchResult(SubProfileSwitchStatus.ClickFailed);
        }

        return SubProfileSwitchResult.Succeeded;
    }

    private async Task<bool> VerifyActiveSubProfileOnPageAsync(
        IPage page,
        string subProfileId,
        string runtimeProvider,
        string runtimeProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return false;
        }

        var sw = Stopwatch.StartNew();
        if (IsOnCandidatesResponsesPage(page.Url))
        {
            await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
                .ConfigureAwait(false);
        }

        await EnsureSwitchModalAsync(
                page,
                cancellationToken,
                nameof(VerifyActiveSubProfileOnPageAsync),
                runtimeProvider,
                runtimeProfileId)
            .ConfigureAwait(false);
        if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
                .ConfigureAwait(false))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
            return false;
        }

        var maxWaitMs = MonitoringTiming.VerifySubProfileMaxWaitMs;
        var pollMs = MonitoringTiming.VerifySubProfilePollMs;

        var pollSw = Stopwatch.StartNew();
        while (pollSw.ElapsedMilliseconds < maxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId, cancellationToken).ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"{runtimeProvider} profile-switch verify OK for subProfile {subProfileId} ({sw.ElapsedMilliseconds} ms).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(VerifyActiveSubProfileOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "verify_ok",
                        ["runtime.provider"] = runtimeProvider,
                        ["runtime.profileId"] = runtimeProfileId,
                        ["avito.subProfileId"] = subProfileId,
                        ["verify.waitMs"] = sw.ElapsedMilliseconds
                    });
                return true;
            }

            await HumanDelay.AroundAsync(pollMs, cancellationToken).ConfigureAwait(false);
        }

        await DismissProfileSwitchModalAsync(page, cancellationToken, runtimeProvider).ConfigureAwait(false);
        _ = GlobalLogger.Instance.LogAsync(
            $"{runtimeProvider} profile-switch verify timed out for subProfile {subProfileId} ({sw.ElapsedMilliseconds} ms).",
            DeskLinkAuditLogLevel.Warning,
            memberName: nameof(VerifyActiveSubProfileOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "verify_timeout",
                ["runtime.provider"] = runtimeProvider,
                ["runtime.profileId"] = runtimeProfileId,
                ["avito.subProfileId"] = subProfileId,
                ["verify.waitMs"] = sw.ElapsedMilliseconds
            });
        return false;
    }

    private async Task<string> ExtractCandidatesJsonOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null,
        AvitoAccountPassBudget? passBudget = null)
    {
        const int maxRestartsAfterRecovery = 2;
        for (var attempt = 1; attempt <= maxRestartsAfterRecovery; attempt++)
        {
            try
            {
                return await ExtractCandidatesJsonOnPageOnceAsync(
                        page,
                        adsPowerUserId,
                        messengerEnrichmentHints,
                        cancellationToken,
                        orchestrator,
                        passBudget)
                    .ConfigureAwait(false);
            }
            catch (AvitoSessionRestartRequiredException ex)
            {
                if (attempt >= maxRestartsAfterRecovery)
                {
                    throw AdsPowerCdpGuard.Timeout(
                        "повторный сбор откликов после восстановления страницы",
                        TimeSpan.FromMinutes(1),
                        ex);
                }

                // Перезапуск не бесплатен: считаем его в бюджете прохода аккаунта,
                // чтобы цепочка «капча → решение → reload → капча» не крутилась
                // бесконечно на всех субпрофилях сразу. Бюджет действий при этом
                // сохраняется: потраченные клики не сбрасываются.
                if (passBudget is not null && !passBudget.TryRegisterSessionRestart())
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Avito responses: лимит перезапусков сценария после восстановления на проход аккаунта исчерпан " +
                        $"({passBudget.SessionRestartCap}); завершаем сбор откликов.",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(ExtractCandidatesJsonOnPageAsync),
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "candidates_restart_budget_exhausted",
                            ["recovery.generation"] = ex.RecoveryGeneration,
                            ["pass.budget"] = passBudget.Describe(),
                            ["page.url"] = page.Url
                        });
                    throw;
                }

                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito responses: страница восстановлена, начинаем проход заново ({attempt + 1}/{maxRestartsAfterRecovery}).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(ExtractCandidatesJsonOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "candidates_restart_after_recovery",
                        ["recovery.generation"] = ex.RecoveryGeneration,
                        ["pass.budget"] = passBudget?.Describe(),
                        ["page.url"] = page.Url
                    });
            }
        }

        throw new InvalidOperationException("Проход страницы откликов не завершился после восстановления страницы.");
    }

    private async Task<string> ExtractCandidatesJsonOnPageOnceAsync(
        IPage page,
        string adsPowerUserId,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null,
        AvitoAccountPassBudget? passBudget = null)
    {
        var pipelineSw = Stopwatch.StartNew();
        var waitSw = Stopwatch.StartNew();
        if (IsOnActiveProfileItemsPage(page.Url)
            && AvitoHumanVariation.RollPermille(MonitoringTiming.ItemsLingerChancePermille))
        {
            await HumanDelay.AfterItemsLingerAsync(cancellationToken).ConfigureAwait(false);
            await AvitoHumanNoise.MaybeDriftAsync(page, MonitoringTiming.HumanNoiseChancePermille, cancellationToken).ConfigureAwait(false);
        }

        var navigationSw = Stopwatch.StartNew();
        await EnsureOnCandidatesPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);
        navigationSw.Stop();

        // Оркестратор сессии уже работает (создан при первом сценарии) — используем его,
        // зафиксировав поколение на весь проход: любое восстановление перезапускает проход,
        // чтобы не продолжать по DOM-индексам, снятым до reload.
        var passRecoveryGeneration = orchestrator?.RecoveryGeneration ?? 0;
        var executeScript = (string script, CancellationToken ct) =>
            orchestrator is null
                ? EvaluateWithRetryAsync<string>(page, script, ct)
                : orchestrator.RunStepAsync(
                    passRecoveryGeneration,
                    token => EvaluateWithRetryAsync<string>(page, script, token),
                    ct);
        var captchaSolve = CreateCaptchaSolveCallback(page);
        Func<AvitoFirewallProbe.Detection, string?, CancellationToken, Task<bool>>? gatedCaptchaSolve =
            captchaSolve is null || orchestrator is null
                ? captchaSolve
                : async (detection, html, ct) =>
                {
                    var solved = await orchestrator.RunStepAsync(
                            passRecoveryGeneration,
                            token => captchaSolve(detection, html, token),
                            ct)
                        .ConfigureAwait(false);
                    if (solved)
                    {
                        // Старый probe-обработчик мог перезагрузить страницу: не продолжаем
                        // обход по индексам, которые были сняты до решения капчи.
                        throw new AvitoSessionRestartRequiredException(orchestrator.RecoveryGeneration + 1);
                    }

                    return false;
                };

        var finalSignature = await AvitoCandidatesPageWaiter
            .TryCaptureListSignatureAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            "Браузер CDP: страница кандидатов стабилизировалась (сессия).",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(ExtractCandidatesJsonOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "candidates_stable",
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = page.Url,
                ["candidates.baselineSignature"] = "<handled-in-ensure>",
                ["candidates.finalSignature"] = finalSignature ?? "<none>",
                ["candidates.waitMs"] = waitSw.ElapsedMilliseconds
            });

        var prepareSw = Stopwatch.StartNew();
        await AvitoCandidatesListPreparer.PrepareAsync(
            executeScript,
            BuildCandidateCollectionLogContext(adsPowerUserId, messengerEnrichmentHints),
            cancellationToken,
            async ct =>
            {
                try
                {
                    return await page.GetContentAsync().ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            },
            page.Url,
            BuildResolveExistingSourceResponseIdsCallback(messengerEnrichmentHints),
            BuildResolveExistingCardFingerprintsCallback(messengerEnrichmentHints),
            BuildResolveExistingPhonesCallback(messengerEnrichmentHints),
            BuildResolveExistingMatchedProfileIndicesCallback(messengerEnrichmentHints),
            messengerEnrichmentHints?.ResponseFilters,
            messengerEnrichmentHints?.IsOpenPhoneWatchAsync,
            skipDetailEnrich: true,
            gatedCaptchaSolve,
            messengerEnrichmentHints?.OpenPhoneWatches,
            BuildCandidatesPageActors(page, orchestrator, passRecoveryGeneration),
            passBudget).ConfigureAwait(false);
        prepareSw.Stop();

        var extractSw = Stopwatch.StartNew();
        var raw = await RunSessionStepAsync(
                orchestrator,
                passRecoveryGeneration,
                token => EvaluateWithRetryAsync<string>(page, ExtractionScript, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Браузер CDP: скрипт извлечения вернул пустой результат.");
        }
        extractSw.Stop();

        var messengerSw = Stopwatch.StartNew();
        raw = await TryEnrichCandidatesJsonMessengerUrlsAsync(
                page,
                raw,
                messengerEnrichmentHints,
                cancellationToken,
                orchestrator,
                passRecoveryGeneration,
                passBudget)
            .ConfigureAwait(false);
        messengerSw.Stop();
        pipelineSw.Stop();
        LogCandidatePipelineTiming(
            adsPowerUserId,
            messengerEnrichmentHints,
            navigationSw.ElapsedMilliseconds,
            prepareSw.ElapsedMilliseconds,
            extractSw.ElapsedMilliseconds,
            messengerSw.ElapsedMilliseconds,
            pipelineSw.ElapsedMilliseconds,
            nameof(ExtractCandidatesJsonOnPageAsync));

        return raw;
    }

    /// <summary>
    /// Оркестратор уровня CDP-сессии: один наблюдатель и один решатель капчи на все сценарии
    /// (отклики, объявления, кошелёк, пополнение, переключение профилей). Регистрирует
    /// существующие восстановители: GeeTest v4 (RuCaptcha), обновление страницы, автовход.
    /// </summary>
    internal AvitoSessionOrchestrator CreateSessionOrchestrator(IPage page)
    {
        var orchestrator = AvitoPageObstacleProbe.CreateOrchestrator(
            page,
            TimeSpan.FromMilliseconds(1000));
        orchestrator.RegisterHandler(AvitoPageObstacleKind.Captcha, (obstacle, ct) =>
            RecoverFromCaptchaWithSolverAsync(page, obstacle, ct));
        orchestrator.RegisterHandler(AvitoPageObstacleKind.TransientError, (_, ct) =>
            RecoverFromTransientErrorAsync(page, ct));
        orchestrator.RegisterHandler(AvitoPageObstacleKind.LoginRequired, async (_, ct) =>
            await TryRecoverAvitoLoginAsync(page, ct).ConfigureAwait(false)
                ? AvitoObstacleRecoveryResult.Success("Автовход выполнен.")
                : AvitoObstacleRecoveryResult.Failure("Автовход не удался."));
        orchestrator.Start();
        return orchestrator;
    }

    /// <summary>
    /// Обработчик капчи для оркестратора сессии: прогоняет существующий автопроход GeeTest v4
    /// (RuCaptcha) с контекстом текущего субпрофиля. Успех подтверждается повторной
    /// проверкой страницы самим оркестратором.
    /// </summary>
    private async Task<AvitoObstacleRecoveryResult> RecoverFromCaptchaWithSolverAsync(
        IPage page,
        AvitoPageObstacle obstacle,
        CancellationToken cancellationToken)
    {
        string? html = null;
        try
        {
            html = await AdsPowerCdpGuard.WaitAsync(
                    page.GetContentAsync(),
                    CdpEvaluateHangTimeout,
                    "чтение HTML для оркестратора капчи",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Солвер снимет HTML сам.
        }

        var solved = await TrySolveGeeTestAsync(
                page,
                html,
                obstacle.Url ?? page.Url,
                obstacle.CaptchaKind ?? "captcha",
                cancellationToken)
            .ConfigureAwait(false);
        if (!solved)
        {
            AvitoCaptchaTaskContext.NoteUnsolved();
            return AvitoObstacleRecoveryResult.Failure("GeeTest v4 не пройдена через RuCaptcha.");
        }

        return AvitoObstacleRecoveryResult.Success("GeeTest v4 пройдена через RuCaptcha.");
    }

    private async Task<AvitoObstacleRecoveryResult> RecoverFromTransientErrorAsync(
        IPage page,
        CancellationToken cancellationToken) =>
        await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(RecoverFromTransientErrorAsync))
            .ConfigureAwait(false)
            ? AvitoObstacleRecoveryResult.Success("Страница обновлена после ошибки Avito.")
            : AvitoObstacleRecoveryResult.Failure("Не удалось обновить страницу после ошибки Avito.");

    private async Task<AvitoMoneySidebar?> TryReadMoneySidebarOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (!IsOnActiveProfileItemsPage(page.Url))
        {
            if (IsOnCandidatesResponsesPage(page.Url))
            {
                return null;
            }

            try
            {
                await NavigateInSiteAsync(page, ProfileItemsPageUrl, 45_000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='osp-sidebar/tools/money']",
                    new WaitForSelectorOptions { Timeout = 12_000 })
                .ConfigureAwait(false);
        }
        catch
        {
            // Sidebar иногда отрисовывается позже карточек — всё равно пробуем распарсить HTML.
        }

        string html;
        try
        {
            if (orchestrator is not null)
            {
                // Не парсим баланс с страницы под капчей: дожидаемся восстановления
                // (или терминального исключения), затем читаем HTML.
                await orchestrator.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            }

            html = await page.GetContentAsync().ConfigureAwait(false);
        }
        catch (AvitoCaptchaDetectedException)
        {
            throw;
        }
        catch (AvitoLoginRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var money = AvitoBalanceParser.ParseMoneySidebar(html);
        if (money is null
            && (html.Contains("osp-sidebar/tools/money", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Аванс", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Кошел", StringComparison.OrdinalIgnoreCase)))
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower balance: sidebar present but money parse failed for user {adsPowerUserId}.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryReadMoneySidebarOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["html.hasMoneyMarker"] = html.Contains("osp-sidebar/tools/money", StringComparison.OrdinalIgnoreCase),
                    ["html.hasAdvanceText"] = html.Contains("Аванс", StringComparison.OrdinalIgnoreCase),
                    ["html.hasWalletText"] = html.Contains("Кошел", StringComparison.OrdinalIgnoreCase)
                });
        }

        return money;
    }

    private async Task<string> LoadWalletHistoryHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        try
        {
            try
            {
                await page.GoToAsync(
                        WalletHistoryPageUrl,
                        MonitoringNavigation(page, 45_000))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                await page.GoToAsync(
                        WalletHistoryPageUrl,
                        MonitoringNavigation(page, 45_000))
                    .ConfigureAwait(false);
            }

            await page.WaitForSelectorAsync(
                    "[data-marker='operation']",
                    new WaitForSelectorOptions { Timeout = 15_000 })
                .ConfigureAwait(false);
        }
        catch (WaitTaskTimeoutException)
        {
            // История может быть пустой или отрисоваться без операций — всё равно
            // возвращаем HTML для диагностики/парсинга.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var html = await page.GetContentAsync().ConfigureAwait(false) ?? string.Empty;
            // История кошелька раньше возвращала HTML капчи как «историю операций» — теперь
            // препятствие распознаётся и либо устраняется, либо честно прерывает сценарий.
            await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
            return html;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower wallet history: HTML read failed for user {adsPowerUserId}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LoadWalletHistoryHtmlOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url
                });
            return string.Empty;
        }
    }

    /// <summary>
    /// Ручное пополнение аванса Avito: переход на /account/advance, ввод суммы, выбор СБП,
    /// переход к оплате и снятие QR. Оплату не выполняет и не сообщает об оплате.
    /// Возвращает QR (base64 из data URL, иначе src) или санитизированную диагностику ошибки.
    /// </summary>
    private async Task<AvitoAdvanceTopUpResult> RunAdvanceTopUpOnPageAsync(
        IPage page,
        string adsPowerUserId,
        decimal amount,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<(bool Allowed, string? Error)>>? beforePayClickAsync = null,
        Func<string, CancellationToken, Task>? reportProgressAsync = null,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (amount <= 0m)
        {
            return AvitoAdvanceTopUpResult.Failed("Сумма пополнения должна быть больше нуля.");
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower advance top-up: start for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(RunAdvanceTopUpOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["topup.amount"] = amount
            });

        try
        {
            // 1) Переход на страницу пополнения аванса.
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    "Открываем страницу пополнения аванса…",
                    cancellationToken)
                .ConfigureAwait(false);
            await NavigateToAdvancePageAsync(page, cancellationToken, orchestrator).ConfigureAwait(false);

            // 2) Ввод суммы.
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    $"Вводим сумму {amount:0.##} ₽…",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await RunSessionStepAsync(orchestrator, ct => EnterAmountAsync(page, amount, ct), cancellationToken).ConfigureAwait(false))
            {
                return AvitoAdvanceTopUpResult.Failed(
                    "Не удалось ввести сумму пополнения: поле ввода не найдено.");
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);

            // 3) Подтверждение суммы.
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    "Подтверждаем сумму пополнения…",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await RunSessionStepAsync(orchestrator, ct => ClickSubmitAsync(page, ct), cancellationToken).ConfigureAwait(false))
            {
                return AvitoAdvanceTopUpResult.Failed(
                    "Не удалось подтвердить сумму пополнения: кнопка не найдена.");
            }

            // 4) Выбор СБП (обязательная валидация выбора).
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    "Выбираем оплату через СБП…",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await RunSessionStepAsync(orchestrator, ct => SelectSbpAsync(page, ct), cancellationToken).ConfigureAwait(false))
            {
                return AvitoAdvanceTopUpResult.Failed(
                    "Не удалось выбрать способ оплаты СБП: вариант не найден.");
            }

            // 4.5) Линеаризационный барьер: заявляем право на оплату (claim) после выбора СБП
            // и непосредственно перед кликом по оплате. Если claim не удался (сессия отменена/
            // истекла) — прерываем сценарий без клика.
            if (beforePayClickAsync is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool claimed;
                string? claimError = null;
                try
                {
                    (claimed, claimError) = await beforePayClickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    claimed = false;
                }

                if (!claimed)
                {
                    return AvitoAdvanceTopUpResult.Failed(
                        string.IsNullOrWhiteSpace(claimError)
                            ? "Не удалось подтвердить оплату перед QR."
                            : claimError);
                }
            }

            // 5) Переход к оплате.
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    "Переходим к оплате и ждём QR-код…",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await RunSessionStepAsync(orchestrator, ct => ClickPayAsync(page, ct), cancellationToken).ConfigureAwait(false))
            {
                return AvitoAdvanceTopUpResult.Failed(
                    "Не удалось перейти к оплате: кнопка оплаты не найдена.");
            }

            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower advance top-up: payment page click completed, starting QR capture.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(RunAdvanceTopUpOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "qr_capture_start",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url
                });

            // 6) Снятие QR из области подтверждения СБП.
            cancellationToken.ThrowIfCancellationRequested();
            await ReportTopUpProgressAsync(
                    reportProgressAsync,
                    "Снимаем QR-код СБП…",
                    cancellationToken)
                .ConfigureAwait(false);
            var qr = await CaptureQrAsync(page, cancellationToken).ConfigureAwait(false);
            if (qr is null)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower advance top-up: QR capture exhausted all attempts.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(RunAdvanceTopUpOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "qr_capture_failed",
                        ["adsPower.userId"] = adsPowerUserId,
                        ["page.url"] = page.Url
                    });
                return AvitoAdvanceTopUpResult.Failed(
                    "QR-код СБП не найден на странице подтверждения.");
            }

            var base64 = AvitoAdvanceTopUpScripts.ExtractBase64FromDataUrl(qr.DataUrl);
            if (!string.IsNullOrWhiteSpace(base64))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower advance top-up: QR captured for user {adsPowerUserId} (data URL).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(RunAdvanceTopUpOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "qr_ready",
                        ["adsPower.userId"] = adsPowerUserId,
                        ["topup.qrSource"] = "dataUrl"
                    });
                return AvitoAdvanceTopUpResult.QrReady(base64);
            }

            if (!string.IsNullOrWhiteSpace(qr.Src))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower advance top-up: QR captured for user {adsPowerUserId} (src URL).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(RunAdvanceTopUpOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "qr_ready",
                        ["adsPower.userId"] = adsPowerUserId,
                        ["topup.qrSource"] = "src"
                    });
                return AvitoAdvanceTopUpResult.QrReady(string.Empty, qr.Src);
            }

            return AvitoAdvanceTopUpResult.Failed(
                "QR-код СБП найден, но не удалось извлечь изображение.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AvitoCaptchaDetectedException)
        {
            return AvitoAdvanceTopUpResult.Failed(
                "На странице пополнения капча — нужна ручная проверка в браузере.");
        }
        catch (AvitoLoginRequiredException)
        {
            return AvitoAdvanceTopUpResult.Failed(
                "Требуется повторная авторизация в Avito — автовход не удался.");
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower advance top-up failed for user {adsPowerUserId}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(RunAdvanceTopUpOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "failed",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["error.type"] = ex.GetType().FullName
                });
            return AvitoAdvanceTopUpResult.Failed(ex.Message);
        }
    }

    private async Task NavigateToAdvancePageAsync(
        IPage page,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        var target = AvitoAdvanceTopUpScripts.AdvancePageUrl;
        if (!string.Equals(page.Url, target, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await page.GoToAsync(target, MonitoringNavigation(page, 60_000)).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                await page.GoToAsync(target, MonitoringNavigation(page, 60_000)).ConfigureAwait(false);
            }
        }

        await ThrowIfCaptchaOnPageAsync(page, cancellationToken, orchestrator).ConfigureAwait(false);

        // Ждём появления поля суммы (или формы входа — тогда автовход).
        try
        {
            await page.WaitForSelectorAsync(
                    AvitoAdvanceTopUpScripts.AmountInputSelector,
                    new WaitForSelectorOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (AvitoAutomationFailureFormatter.SuggestsLogin(state))
            {
                if (await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false))
                {
                    await page.GoToAsync(target, MonitoringNavigation(page, 60_000)).ConfigureAwait(false);
                    await page.WaitForSelectorAsync(
                            AvitoAdvanceTopUpScripts.AmountInputSelector,
                            new WaitForSelectorOptions { Timeout = 30_000 })
                        .ConfigureAwait(false);
                    return;
                }

                throw new AvitoLoginRequiredException(state?.Url, state?.Title);
            }

            throw new InvalidOperationException(
                $"AdsPower advance top-up: поле суммы не появилось на {page.Url}: {ex.Message}",
                ex);
        }
    }

    private async Task<bool> EnterAmountAsync(IPage page, decimal amount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                page,
                AvitoAdvanceTopUpScripts.BuildEnterAmountScript(amount),
                cancellationToken,
                TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
    }

    private async Task<bool> ClickSubmitAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clicked = await AvitoHumanPointer.TryClickSelectorAsync(
                page,
                AvitoAdvanceTopUpScripts.SubmitButtonSelector,
                cancellationToken)
            .ConfigureAwait(false);
        if (!clicked)
        {
            return false;
        }

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SelectSbpAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForSelectorAsync(
                    AvitoAdvanceTopUpScripts.PaymentPageReadySelector,
                    new WaitForSelectorOptions { Timeout = 20_000 })
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        var selected = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                page,
                AvitoAdvanceTopUpScripts.BuildSelectSbpScript(),
                cancellationToken,
                TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        if (!selected)
        {
            return false;
        }

        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickPayAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForSelectorAsync(
                    AvitoAdvanceTopUpScripts.PayButtonSelector,
                    new WaitForSelectorOptions { Timeout = 20_000 })
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        // Последняя возможная проверка отмены непосредственно перед кликом по оплате.
        // Между этой проверкой и диспетчеризацией клика нет намеренных задержек: сам клик
        // выполняется через AvitoHumanPointer, который ещё раз проверяет отмену перед ClickAsync.
        cancellationToken.ThrowIfCancellationRequested();

        var clicked = await AvitoHumanPointer.TryClickSelectorAsync(
                page,
                AvitoAdvanceTopUpScripts.PayButtonSelector,
                cancellationToken)
            .ConfigureAwait(false);
        if (!clicked)
        {
            return false;
        }

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<AvitoAdvanceTopUpScripts.QrCaptureResult?> CaptureQrAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await page.WaitForFunctionAsync(
                    AvitoAdvanceTopUpScripts.QrReadyWaitExpression,
                    new WaitForFunctionOptions { Timeout = 30_000, PollingInterval = 500 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Пробуем снять QR напрямую — скрипт сам проверит наличие.
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower advance top-up: QR ready wait did not match; falling back to direct capture.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(CaptureQrAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "qr_wait_failed",
                    ["page.url"] = page.Url,
                    ["exceptionType"] = ex.GetType().Name
                });
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var raw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoAdvanceTopUpScripts.BuildCaptureQrScript(),
                    cancellationToken,
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);

            var result = AvitoAdvanceTopUpScripts.TryParseQrCapture(raw);
            if (result is { Found: true })
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower advance top-up: QR capture succeeded on attempt {attempt + 1}/5.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(CaptureQrAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "qr_capture_success",
                        ["attempt"] = attempt + 1,
                        ["page.url"] = page.Url,
                        ["qr.reason"] = result.Reason,
                        ["qr.hasDataUrl"] = !string.IsNullOrWhiteSpace(result.DataUrl),
                        ["qr.hasSrc"] = !string.IsNullOrWhiteSpace(result.Src)
                    });
                return result;
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower advance top-up: QR capture attempt {attempt + 1}/5 did not find a usable QR.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(CaptureQrAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "qr_capture_miss",
                    ["attempt"] = attempt + 1,
                    ["page.url"] = page.Url,
                    ["qr.reason"] = result?.Reason ?? "invalid_script_result",
                    ["qr.rawResultPresent"] = !string.IsNullOrWhiteSpace(raw)
                });

            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task ReportTopUpProgressAsync(
        Func<string, CancellationToken, Task>? reportProgressAsync,
        string message,
        CancellationToken cancellationToken)
    {
        if (reportProgressAsync is null)
        {
            return;
        }

        try
        {
            await reportProgressAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Статус для UI не должен срывать сценарий пополнения.
        }
    }

    private async Task<string> LoadProfileItemsHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (!IsOnActiveProfileItemsPage(page.Url))
        {
            try
            {
                await NavigateInSiteAsync(page, ProfileItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            }
        }

        await WaitForProfileItemsShellAsync(page, nameof(LoadProfileItemsHtmlOnPageAsync), cancellationToken)
            .ConfigureAwait(false);
        await WaitForProfileItemsReadyAsync(page, nameof(LoadProfileItemsHtmlOnPageAsync), cancellationToken)
            .ConfigureAwait(false);
        await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

        var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("Браузер CDP: страница объявлений Avito вернула пустой HTML.");
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
        return html;
    }

    private async Task<string> LoadBlockedItemsHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (!IsOnRejectedTab(page.Url))
        {
            try
            {
                await NavigateInSiteAsync(page, ProfileBlockedItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            }
        }

        await WaitForBlockedItemsShellAsync(page, cancellationToken).ConfigureAwait(false);
        await WaitForBlockedItemsReadyAsync(page, cancellationToken).ConfigureAwait(false);
        await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

        var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("Браузер CDP: вкладка «С ошибками» вернула пустой HTML.");
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
        return html;
    }

    private async Task<string> LoadUnpublishedItemsHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (!page.Url.Contains("tabs%22%3A%22inactive", StringComparison.OrdinalIgnoreCase)
            && !page.Url.Contains("tabs=inactive", StringComparison.OrdinalIgnoreCase))
        {
            try { await NavigateInSiteAsync(page, ProfileUnpublishedItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsRecoverableNavigationError(ex)) { await Task.Delay(1400, cancellationToken).ConfigureAwait(false); }
        }

        await WaitForProfileItemsShellAsync(page, nameof(LoadUnpublishedItemsHtmlOnPageAsync), cancellationToken).ConfigureAwait(false);
        await WaitForProfileItemsReadyAsync(page, nameof(LoadUnpublishedItemsHtmlOnPageAsync), cancellationToken).ConfigureAwait(false);
        await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);
        var html = await EvaluateWithRetryAsync<string>(page, "(() => document.documentElement?.outerHTML || '')()", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidOperationException("Браузер CDP: вкладка «Неопубликованные» вернула пустой HTML.");
        await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
        return html;
    }

    private async Task<AvitoAdRenewalResult> RenewAdOnPageAsync(
        IPage page,
        string avitoItemId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        if (string.IsNullOrWhiteSpace(avitoItemId)
            || avitoItemId.Any(static ch => !char.IsAsciiDigit(ch)))
        {
            return AvitoAdRenewalResult.Failed(
                "invalid_item_id",
                "У объявления отсутствует корректный идентификатор Avito.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var unpublishedHtml = await LoadUnpublishedItemsHtmlOnPageAsync(page, "renewal", cancellationToken)
            .ConfigureAwait(false);
        var itemSnippet = AvitoParserService.ExtractItemSnippetHtml(unpublishedHtml, avitoItemId);
        if (string.IsNullOrWhiteSpace(itemSnippet))
        {
            return AvitoAdRenewalResult.AlreadyPublished(
                "Объявления уже нет среди неопубликованных. Возможно, оно было опубликовано ранее.");
        }

        var publishButton = await FindPublishButtonAsync(page, avitoItemId, cancellationToken).ConfigureAwait(false);
        if (publishButton is null)
        {
            return AvitoAdRenewalResult.Failed(
                "publish_action_not_available",
                "Avito не показывает действие «Опубликовать» для этого объявления.");
        }

        if (!await AvitoHumanPointer.TryClickHandleAsync(page, publishButton, cancellationToken).ConfigureAwait(false))
        {
            return AvitoAdRenewalResult.Failed(
                "publish_action_click_failed",
                "Не удалось нажать «Опубликовать» в карточке объявления.");
        }

        const string submitSelector = "button[data-marker='submit-button']";
        try
        {
            await page.WaitForSelectorAsync(
                    submitSelector,
                    new WaitForSelectorOptions { Timeout = 30_000, Visible = true })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await NavigateInSiteAsync(page, ProfileUnpublishedItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false);
            await WaitForProfileItemsReadyAsync(page, nameof(RenewAdOnPageAsync), cancellationToken).ConfigureAwait(false);
            var afterFirstClick = await page.GetContentAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(AvitoParserService.ExtractItemSnippetHtml(afterFirstClick, avitoItemId)))
            {
                return AvitoAdRenewalResult.Submitted(
                    "Объявление отправлено на публикацию. Avito обновляет его статус.");
            }

            return AvitoAdRenewalResult.Failed(
                "publication_form_not_opened",
                "После первого шага Avito не открыл страницу подтверждения публикации.");
        }

        await ThrowIfCaptchaOnPageAsync(page, cancellationToken, orchestrator).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await TryClickFirstVisibleAsync(page, submitSelector, cancellationToken).ConfigureAwait(false))
        {
            return AvitoAdRenewalResult.Failed(
                "publication_submit_failed",
                "Не удалось подтвердить публикацию на странице Avito.");
        }

        try
        {
            await page.WaitForFunctionAsync(
                    "() => !document.querySelector(\"button[data-marker='submit-button']\")",
                    new WaitForFunctionOptions { Timeout = 45_000, PollingInterval = 400 })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Некоторые версии Avito оставляют страницу формы до завершения фонового запроса.
            // Финальную проверку делаем по карточке на вкладке «Неопубликованные» ниже.
        }

        await ThrowIfCaptchaOnPageAsync(page, cancellationToken, orchestrator).ConfigureAwait(false);
        await NavigateInSiteAsync(page, ProfileUnpublishedItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false);
        await WaitForProfileItemsReadyAsync(page, nameof(RenewAdOnPageAsync), cancellationToken).ConfigureAwait(false);

        if (await FindPublishButtonAsync(page, avitoItemId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AvitoAdRenewalResult.Failed(
                "publication_not_confirmed",
                "Avito оставил объявление неопубликованным. Повторите попытку или откройте карточку вручную.");
        }

        return AvitoAdRenewalResult.Submitted(
            "Объявление отправлено на публикацию на 30 дней. Avito обновляет его статус.");
    }

    private static async Task<IElementHandle?> FindPublishButtonAsync(
        IPage page,
        string avitoItemId,
        CancellationToken cancellationToken)
    {
        var buttons = await page.QuerySelectorAllAsync("button[data-marker='publish-action']").ConfigureAwait(false);
        IElementHandle? hiddenMatch = null;
        foreach (var button in buttons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relatedItemId;
            try
            {
                relatedItemId = await button.EvaluateFunctionAsync<string>(
                        """
                        el => {
                          let node = el;
                          for (let depth = 0; node && depth < 16; depth++, node = node.parentElement) {
                            const href = node.querySelector?.('a[data-marker="view-link"]')?.getAttribute('href') || '';
                            const hrefMatch = href.match(/(\d+)(?:\/?(?:\?|#|$))/);
                            if (hrefMatch) return hrefMatch[1];
                            const marker = node.querySelector?.('[data-marker^="item/"]')?.getAttribute('data-marker') || '';
                            const markerMatch = marker.match(/^item\/(\d+)\//);
                            if (markerMatch) return markerMatch[1];
                          }
                          return '';
                        }
                        """)
                    .ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (string.Equals(relatedItemId, avitoItemId, StringComparison.Ordinal))
            {
                hiddenMatch ??= button;
                var box = await button.BoundingBoxAsync().ConfigureAwait(false);
                if (box is { Width: >= 1, Height: >= 1 })
                {
                    return button;
                }
            }
        }

        return hiddenMatch;
    }

    private static async Task<bool> TryClickFirstVisibleAsync(
        IPage page,
        string selector,
        CancellationToken cancellationToken)
    {
        var handles = await page.QuerySelectorAllAsync(selector).ConfigureAwait(false);
        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await AvitoHumanPointer.TryClickHandleAsync(page, handle, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<AvitoAdListCapture> CaptureActiveAdsListOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        try
        {
            if (!IsOnActiveProfileItemsPage(page.Url))
            {
                try
                {
                    await NavigateInSiteAsync(page, ProfileItemsPageUrl, 60_000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRecoverableNavigationError(ex))
                {
                    await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                }
            }

            await WaitForProfileItemsShellAsync(page, nameof(CaptureActiveAdsListOnPageAsync), cancellationToken)
                .ConfigureAwait(false);
            await EnsureActiveItemsTabAsync(page, cancellationToken).ConfigureAwait(false);
            await WaitForProfileItemsReadyAsync(page, nameof(CaptureActiveAdsListOnPageAsync), cancellationToken)
                .ConfigureAwait(false);
            await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

            var firstHtml = await EvaluateWithRetryAsync<string>(
                    page,
                    "(() => document.documentElement?.outerHTML || '')()",
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(firstHtml))
            {
                return new AvitoAdListCapture
                {
                    Success = false,
                    Complete = false,
                    FailureReason = "empty_active_items_html"
                };
            }

            await ThrowIfCaptchaAsync(page, firstHtml, cancellationToken, orchestrator).ConfigureAwait(false);
            var pages = new List<string> { firstHtml };
            var complete = await ScrollActiveAdsUntilSettledAsync(
                    page,
                    pages,
                    cancellationToken,
                    orchestrator)
                .ConfigureAwait(false);
            if (!await TryCaptureActiveAdsHtmlAsync(page, pages, cancellationToken, orchestrator).ConfigureAwait(false))
            {
                complete = false;
            }

            for (var pageIndex = 1; pageIndex < MonitoringTiming.AvitoAdsListMaxPages; pageIndex++)
            {
                if (orchestrator is not null)
                {
                    // Контрольная точка перед кликом пагинации: капча, найденная наблюдателем
                    // на предыдущей странице, к этому моменту уже устранена.
                    await orchestrator.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
                }

                var hasNext = await EvaluateWithRetryAsync<bool>(
                        page,
                        AvitoAdListPageScripts.HasNextPageScript,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!hasNext)
                {
                    break;
                }

                var clicked = false;
                foreach (var selector in AvitoAdListPageScripts.NextPageSelectors)
                {
                    if (await AvitoHumanPointer.TryClickSelectorAsync(page, selector, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        clicked = true;
                        break;
                    }
                }

                if (!clicked)
                {
                    clicked = await EvaluateWithRetryAsync<bool>(
                            page,
                            AvitoAdListPageScripts.ClickNextPageScript,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!clicked)
                {
                    complete = false;
                    break;
                }

                await WaitForProfileItemsShellAsync(page, nameof(CaptureActiveAdsListOnPageAsync), cancellationToken)
                    .ConfigureAwait(false);
                await WaitForProfileItemsReadyAsync(page, nameof(CaptureActiveAdsListOnPageAsync), cancellationToken)
                    .ConfigureAwait(false);
                await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

                if (!await ScrollActiveAdsUntilSettledAsync(page, pages, cancellationToken, orchestrator).ConfigureAwait(false))
                {
                    complete = false;
                }

                if (!await TryCaptureActiveAdsHtmlAsync(page, pages, cancellationToken, orchestrator).ConfigureAwait(false))
                {
                    complete = false;
                    break;
                }
            }

            var stillHasNext = await EvaluateWithRetryAsync<bool>(
                    page,
                    AvitoAdListPageScripts.HasNextPageScript,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stillHasNext)
            {
                complete = false;
            }

            return new AvitoAdListCapture
            {
                Success = true,
                Complete = complete,
                PageHtml = pages
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AvitoAdListCapture
            {
                Success = false,
                Complete = false,
                FailureReason = ex.Message
            };
        }
    }

    private async Task EnsureActiveItemsTabAsync(IPage page, CancellationToken cancellationToken)
    {
        const string clickActive = """
            (() => {
                const tab = document.querySelector('[data-marker="profile-items-tab/tab(active)"]');
                if (!tab) return false;
                const selected = tab.getAttribute('aria-selected');
                if (selected === 'true') return false;
                return true;
            })()
            """;

        var needsClick = await EvaluateWithRetryAsync<bool>(page, clickActive, cancellationToken).ConfigureAwait(false);
        if (!needsClick)
        {
            return;
        }

        // Trusted-клик по табу; JS click() — только если CDP-указатель не добрался.
        if (!await AvitoHumanPointer.TryClickSelectorAsync(
                page,
                "[data-marker='profile-items-tab/tab(active)']",
                cancellationToken).ConfigureAwait(false))
        {
            _ = await EvaluateWithRetryAsync<bool>(
                    page,
                    """
                    (() => {
                        const tab = document.querySelector('[data-marker="profile-items-tab/tab(active)"]');
                        if (!tab) return false;
                        tab.click();
                        return true;
                    })()
                    """,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await WaitForProfileItemsReadyAsync(page, nameof(EnsureActiveItemsTabAsync), cancellationToken)
            .ConfigureAwait(false);
        await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ScrollActiveAdsUntilSettledAsync(
        IPage page,
        List<string> pages,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        var initial = await ProbeActiveAdsScrollAsync(page, AvitoAdListPageScripts.ProbeScript, cancellationToken)
            .ConfigureAwait(false);
        if (initial.Count == 0)
        {
            return true;
        }

        var lastCount = initial.Count;
        var previousFirst = initial.FirstMarker;
        var stableRounds = 0;
        var last = initial;

        for (var round = 0; round < MonitoringTiming.AvitoAdsListMaxScrollRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wheelStep = await TryWheelScrollActiveAdsAsync(page, cancellationToken).ConfigureAwait(false);
            AvitoAdListScrollProbe step;
            if (wheelStep is not null)
            {
                step = wheelStep;
            }
            else
            {
                step = await ProbeActiveAdsScrollAsync(page, AvitoAdListPageScripts.ScrollStepScript, cancellationToken)
                    .ConfigureAwait(false);
            }

            await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
            await WaitForProfileItemsReadyAsync(page, nameof(ScrollActiveAdsUntilSettledAsync), cancellationToken)
                .ConfigureAwait(false);

            last = await ProbeActiveAdsScrollAsync(page, AvitoAdListPageScripts.ProbeScript, cancellationToken)
                .ConfigureAwait(false);
            if (last.FirstWindowMoved(previousFirst))
            {
                _ = await TryCaptureActiveAdsHtmlAsync(page, pages, cancellationToken, orchestrator).ConfigureAwait(false);
            }

            previousFirst = string.IsNullOrEmpty(last.FirstMarker) ? previousFirst : last.FirstMarker;
            if (last.Count > lastCount)
            {
                lastCount = last.Count;
                stableRounds = 0;
                continue;
            }

            if (last.Loader)
            {
                stableRounds = 0;
                continue;
            }

            if (last.AtEnd || step.ScrollIdle)
            {
                stableRounds++;
                if (stableRounds >= MonitoringTiming.AvitoAdsListStableScrollRounds)
                {
                    return true;
                }

                continue;
            }

            stableRounds = 0;
        }

        return last.AtEnd || last.Count == 0;
    }

    private async Task<AvitoAdListScrollProbe?> TryWheelScrollActiveAdsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            var geometryRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoAdListPageScripts.GeometryScript,
                    cancellationToken)
                .ConfigureAwait(false);
            var geometry = AvitoScrollStepProbeParser.TryParseGeometry(geometryRaw);
            if (geometry is null || geometry.ClientHeight <= 0)
            {
                return null;
            }

            // «Показать ещё» — trusted-кликом по селектору; текстовый фолбэк остаётся в JS-пути.
            var loadMoreClicked = false;
            if (await EvaluateWithRetryAsync<bool>(
                    page,
                    AvitoAdListPageScripts.HasLoadMoreScript,
                    cancellationToken).ConfigureAwait(false))
            {
                foreach (var selector in AvitoAdListPageScripts.LoadMoreSelectors)
                {
                    if (await AvitoHumanPointer.TryClickSelectorAsync(page, selector, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        loadMoreClicked = true;
                        break;
                    }
                }

                if (!loadMoreClicked)
                {
                    // Кнопка нашлась по тексту, но не по маркеру — остаёмся на JS-шаге.
                    return null;
                }
            }

            // The ads scroller is not the candidates scroller; wheel over the rect we just probed.
            using var rectDoc = JsonDocument.Parse(UnwrapMessengerJson(geometryRaw));
            var rect = rectDoc.RootElement;
            var ratio = 0.32 + Random.Shared.NextDouble() * 0.28;
            var delta = Math.Max((int)(geometry.ClientHeight * ratio), 180);
            if (!await AvitoHumanWheel.ScrollOverRectAsync(
                    page,
                    rect.GetProperty("x").GetDecimal(),
                    rect.GetProperty("y").GetDecimal(),
                    rect.GetProperty("width").GetDecimal(),
                    rect.GetProperty("height").GetDecimal(),
                    delta,
                    cancellationToken).ConfigureAwait(false))
            {
                return loadMoreClicked
                    ? (await ProbeActiveAdsScrollAsync(page, AvitoAdListPageScripts.ProbeScript, cancellationToken)
                        .ConfigureAwait(false)) with { LoadMoreClicked = true }
                    : null;
            }

            var afterRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoAdListPageScripts.GeometryScript,
                    cancellationToken)
                .ConfigureAwait(false);
            var after = AvitoScrollStepProbeParser.TryParseGeometry(afterRaw);
            var moved = after is not null && Math.Abs(after.ScrollTop - geometry.ScrollTop) > 2;

            var probe = await ProbeActiveAdsScrollAsync(page, AvitoAdListPageScripts.ProbeScript, cancellationToken)
                .ConfigureAwait(false);
            if (!moved && !loadMoreClicked && !probe.AtEnd)
            {
                return null;
            }

            return probe with { Moved = moved || loadMoreClicked, LoadMoreClicked = loadMoreClicked };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<AvitoAdListScrollProbe> ProbeActiveAdsScrollAsync(
        IPage page,
        string script,
        CancellationToken cancellationToken)
    {
        var raw = await EvaluateWithRetryAsync<string>(page, script, cancellationToken).ConfigureAwait(false);
        return AvitoAdListScrollProbeParser.Parse(raw);
    }

    private async Task<bool> TryCaptureActiveAdsHtmlAsync(
        IPage page,
        List<string> pages,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
        pages.Add(html);
        return true;
    }

    private async Task<string> LoadItemDetailHtmlOnPageAsync(
        IPage page,
        string url,
        CancellationToken cancellationToken,
        AvitoSessionOrchestrator? orchestrator = null)
    {
        var target = NormalizeDetailUrl(url);
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        try
        {
            await page.GoToAsync(target, MonitoringNavigation(page, 45_000)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='item-view/item-id'], [data-marker='item-view/title-info'], [data-marker='item-lifebar']",
                    new WaitForSelectorOptions { Timeout = 20_000 })
                .ConfigureAwait(false);
        }
        catch
        {
            // Карточка могла не дорисоваться — снимем HTML как есть и вернёмся к списку.
        }

        await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);
        var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken)
            .ConfigureAwait(false);

        // Раньше HTML капчи мог вернуться как «карточка объявления» и уехать в парсер.
        if (!string.IsNullOrWhiteSpace(html))
        {
            await ThrowIfCaptchaAsync(page, html, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        try
        {
            await NavigateInSiteAsync(page, ProfileItemsPageUrl, 45_000, cancellationToken).ConfigureAwait(false);
            await WaitForProfileItemsShellAsync(page, nameof(LoadItemDetailHtmlOnPageAsync), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(900, cancellationToken).ConfigureAwait(false);
        }

        return html ?? string.Empty;
    }

    private static string NormalizeDetailUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://www.avito.ru" + trimmed;
        }

        return trimmed;
    }

    private static async Task WaitForProfileItemsShellAsync(
        IPage page,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='sorting-control'], [data-marker^='item-snippet/']",
                    new WaitForSelectorOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Браузер: истекло ожидание основного контейнера страницы объявлений: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "shell_timeout",
                    ["page.url"] = page.Url
                });
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task WaitForProfileItemsReadyAsync(
        IPage page,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        const string itemsReadyExpression = """
            (() => {
                const root = document.querySelector('#personal-items-root-element') || document.body;
                const hasLoader = !!root.querySelector("[class*='styles-loader'], [class*='style-loader']");
                if (hasLoader) return false;
                const hasItems = !!document.querySelector("[data-marker^='item-snippet/']");
                if (hasItems) return true;
                const hasAddItemEmpty = !!root.querySelector("[data-marker='additem']");
                const hasEmptyStateImg = !!root.querySelector("img[src*='emptystate_personal_items']");
                if (hasAddItemEmpty || hasEmptyStateImg) return true;
                const text = (root.innerText || '').toLowerCase();
                const looksEmpty =
                    /активн[а-я]*\s+объявлен[а-я]*\s+нет/.test(text) ||
                    /нет\s+(активных\s+)?объявлен/.test(text) ||
                    /у\s+вас\s+нет\s+активных/.test(text) ||
                    /объявлен[а-я]*\s+не\s+найден/.test(text) ||
                    /пока\s+пусто/.test(text) ||
                    /можно\s+создать\s+новое/.test(text);
                return looksEmpty;
            })
            """;

        try
        {
            await page.WaitForFunctionAsync(
                    itemsReadyExpression,
                    new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 750 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Браузер: истекло ожидание элементов страницы объявлений, сохраняем текущее состояние: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "items_timeout",
                    ["page.url"] = page.Url
                });
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task WaitForBlockedItemsShellAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='profile-items-tab/tab(rejected)'], [data-marker^='item-snippet/']",
                    new WaitForSelectorOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Браузер: истекло ожидание контейнера заблокированных объявлений: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LoadBlockedItemsHtmlOnPageAsync));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task WaitForBlockedItemsReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        const string blockedReadyExpression = """
            (() => {
                const root = document.querySelector('#personal-items-root-element') || document.body;
                const hasLoader = !!root.querySelector("[class*='styles-loader'], [class*='style-loader']");
                if (hasLoader) return false;
                const hasItems = !!document.querySelector("[data-marker^='item-snippet/']");
                if (hasItems) return true;
                const hasAddItemEmpty = !!root.querySelector("[data-marker='additem']");
                const hasEmptyStateImg = !!root.querySelector("img[src*='emptystate_personal_items']");
                if (hasAddItemEmpty || hasEmptyStateImg) return true;
                const text = (root.innerText || '').toLowerCase();
                const looksEmpty =
                    /объявлен[а-я]*\s+с\s+ошибк/.test(text) ||
                    /нет\s+объявлен/.test(text) ||
                    /объявлен[а-я]*\s+нет/.test(text) ||
                    /пока\s+пусто/.test(text);
                return looksEmpty;
            })
            """;

        try
        {
            await page.WaitForFunctionAsync(
                    blockedReadyExpression,
                    new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 750 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Браузер: истекло ожидание заблокированных объявлений, сохраняем текущее состояние: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LoadBlockedItemsHtmlOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "items_timeout",
                    ["page.url"] = page.Url
                });
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class AccountSession(
        AdsPowerAvitoAutomationService owner,
        IBrowser browser,
        IPage page,
        string adsPowerUserId,
        GeeTestV4TaskOptions captchaOptions,
        string runtimeProvider) : IAdsPowerAccountSession
    {
        public string AdsPowerUserId { get; } = adsPowerUserId;

        public string RuntimeProvider { get; } = runtimeProvider;

        public string? CurrentPageUrl => page.Url;

        private AvitoSessionOrchestrator? sessionOrchestrator;

        /// <summary>
        /// Оркестратор на всю CDP-сессию: наблюдатель следит за страницей при любом сценарии
        /// (отклики, объявления, кошелёк, пополнение, переключение профилей). Создаётся лениво
        /// внутри контекста <see cref="AvitoCaptchaTaskContext"/> первого вызова — обработчики
        /// наследуют параметры субпрофиля.
        /// </summary>
        private AvitoSessionOrchestrator Orchestrator =>
            sessionOrchestrator ??= owner.CreateSessionOrchestrator(page);

        public Task<byte[]?> CapturePageScreenshotAsync(CancellationToken cancellationToken = default) =>
            BrowserDiagnosticsCapture.CapturePageScreenshotAsync(page, cancellationToken);

        public Task<byte[]?> CapturePageJpegScreenshotAsync(CancellationToken cancellationToken = default) =>
            BrowserDiagnosticsCapture.CaptureJpegFastAsync(page, cancellationToken: cancellationToken);

        public Task<BrowserMonitorScreencastCapture> CreateMonitorScreencastCaptureAsync(
            CancellationToken cancellationToken = default) =>
            BrowserMonitorScreencastCapture.StartAsync(page, cancellationToken);

        public async Task<SubProfileSwitchResult> SwitchSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.SwitchSubProfileOnPageAsync(page, subProfileId, AdsPowerUserId, RuntimeProvider, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.VerifyActiveSubProfileOnPageAsync(
                    page,
                    subProfileId,
                    RuntimeProvider,
                    AdsPowerUserId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> ExtractCandidatesJsonAsync(
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            CancellationToken cancellationToken = default,
            AvitoAccountPassBudget? passBudget = null)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner
                .ExtractCandidatesJsonOnPageAsync(page, AdsPowerUserId, messengerEnrichmentHints, cancellationToken, orchestrator, passBudget)
                .ConfigureAwait(false);
        }

        public async Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.LoadProfileItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.LoadBlockedItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<string> LoadUnpublishedItemsHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.LoadUnpublishedItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<AvitoAdRenewalResult> RenewAdAsync(
            string avitoItemId,
            CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.RenewAdOnPageAsync(page, avitoItemId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<AvitoAdListCapture> CaptureActiveAdsListAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.CaptureActiveAdsListOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<string> LoadItemDetailHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.LoadItemDetailHtmlOnPageAsync(page, url, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<AvitoMoneySidebar?> TryReadMoneySidebarAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.TryReadMoneySidebarOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator).ConfigureAwait(false);
        }

        public async Task<string> LoadWalletHistoryHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;
            return await owner.LoadWalletHistoryHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken, orchestrator)
                .ConfigureAwait(false);
        }

        public async Task<AvitoAdvanceTopUpResult> RunAdvanceTopUpAsync(
            decimal amount,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task<(bool Allowed, string? Error)>>? beforePayClickAsync = null,
            Func<string, CancellationToken, Task>? reportProgressAsync = null)
        {
            using var captchaScope = AvitoCaptchaTaskContext.Use(captchaOptions);
            var orchestrator = Orchestrator;

            // Восстановление страницы (reload после капчи) отменяет DOM-состояние сценария
            // пополнения — безопасно начать сценарий заново; claim-барьер защищает от двойной оплаты.
            const int maxRestartsAfterRecovery = 2;
            for (var attempt = 1; attempt <= maxRestartsAfterRecovery; attempt++)
            {
                try
                {
                    return await owner.RunAdvanceTopUpOnPageAsync(
                            page,
                            AdsPowerUserId,
                            amount,
                            cancellationToken,
                            beforePayClickAsync,
                            reportProgressAsync,
                            orchestrator)
                        .ConfigureAwait(false);
                }
                catch (AvitoSessionRestartRequiredException) when (attempt < maxRestartsAfterRecovery)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower advance top-up: страница восстановлена, перезапускаем сценарий ({attempt + 1}/{maxRestartsAfterRecovery}).",
                        DeskLinkAuditLogLevel.Info,
                        memberName: nameof(RunAdvanceTopUpAsync),
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "topup_restart_after_recovery",
                            ["adsPower.userId"] = AdsPowerUserId
                        });
                }
            }

            return AvitoAdvanceTopUpResult.Failed("Сценарий пополнения не завершился после восстановления страницы.");
        }

        public async Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.CaptureProfileSwitchHtmlInSessionAsync(page, AdsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<AvitoPageState?> GetPageStateAsync(CancellationToken cancellationToken = default) =>
            ProbePageStateAsync(page, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            var orchestrator = sessionOrchestrator;
            sessionOrchestrator = null;
            if (orchestrator is not null)
            {
                await orchestrator.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                browser.Disconnect();
            }
            catch
            {
                // Disconnect must never throw out of Dispose.
            }

            await Task.CompletedTask;
        }
    }
}
