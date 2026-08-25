using System.Diagnostics;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Core.Services.Captcha;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        var startupStopwatch = Stopwatch.StartNew();
        ReportStartupStage(reportStartupStage, 1, "поиск рабочей вкладки", startupStopwatch);
        var page = await AcquireAutomationPageAsync(
                browser,
                ProfileItemsPageUrl,
                nameof(OpenAccountSessionOnConnectedBrowserAsync),
                cancellationToken,
                waitForStartupNavigation: true)
            .ConfigureAwait(false);
        ReportStartupStage(reportStartupStage, 1, "вкладка получена", startupStopwatch);
        ReportStartupStage(reportStartupStage, 1, "прогрев страницы Avito", startupStopwatch);
        page = await WarmUpSessionPageAsync(page, sessionKey, cancellationToken).ConfigureAwait(false);
        ReportStartupStage(reportStartupStage, 1, "страница Avito готова", startupStopwatch);
        return new AccountSession(this, browser, page, sessionKey, new GeeTestV4TaskOptions());
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

            return new AccountSession(this, browser, page, adsPowerUserId, captchaOptions);
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
        CancellationToken cancellationToken)
    {
        await TryBringAutomationPageToFrontAsync(
                page,
                CdpPageDiscoveryTimeout,
                "BringToFront прогрева вкладки",
                cancellationToken)
            .ConfigureAwait(false);

        var currentUrl = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
        if (IsReusableStartupPlaceholderUrl(currentUrl) || !IsUsableWorkerPageUrl(currentUrl))
        {
            page = await NavigateOffStartupPlaceholderAsync(
                    page,
                    ProfileItemsPageUrl,
                    nameof(WarmUpSessionPageAsync),
                    cancellationToken)
                .ConfigureAwait(false);
            currentUrl = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (!IsAvitoProfileAutomationTab(currentUrl))
        {
            await TryCdpPageNavigateAsync(page, ProfileItemsPageUrl, cancellationToken).ConfigureAwait(false);
            page = await PollUntilAvitoPageAsync(page, cancellationToken).ConfigureAwait(false);
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
        if (CanTryClearCaptcha(warmupState)
            && await TryClearGeeTestCaptchaAsync(page, cancellationToken).ConfigureAwait(false))
        {
            warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (warmupState?.IsTransientPageError == true)
        {
            await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(WarmUpSessionPageAsync))
                .ConfigureAwait(false);
            warmupState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        }

        if (warmupState?.HasLoginForm == true
            || warmupState?.PageKind == AvitoPageKind.Login
            || AvitoAutomationFailureFormatter.SuggestsLogin(warmupState))
        {
            var recovered = await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false);
            if (recovered && !IsOnActiveProfileItemsPage(page.Url))
            {
                await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                {
                    Timeout = 90_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
            }
        }

        if (IsOnActiveProfileItemsPage(page.Url)
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
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = page.Url
            });

        return page;
    }

    private async Task<SubProfileSwitchResult> SwitchSubProfileOnPageAsync(
        IPage page,
        string subProfileId,
        string adsPowerUserId,
        CancellationToken cancellationToken)
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
            last = await TrySwitchSubProfileOnPageOnceAsync(page, subProfileId, adsPowerUserId, cancellationToken)
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
                $"AdsPower profile-switch (session): attempt {attempt}/{MonitoringTiming.SubProfileSwitchMaxAttempts} failed for subProfile {subProfileId}, recovering before retry.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switch_retry",
                    ["attempt"] = attempt,
                    ["adsPower.userId"] = adsPowerUserId,
                    ["avito.subProfileId"] = subProfileId,
                    ["switch.status"] = last.Status.ToString(),
                    ["page.url"] = page.Url
                });

            await RecoverPageBeforeSubProfileSwitchRetryAsync(page, cancellationToken, attempt)
                .ConfigureAwait(false);
        }

        return last;
    }

    private async Task RecoverPageBeforeSubProfileSwitchRetryAsync(
        IPage page,
        CancellationToken cancellationToken,
        int attempt)
    {
        await TryRecoverTransientAvitoErrorAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);
        await DismissAvitoBlockingOverlaysAsync(page, cancellationToken).ConfigureAwait(false);
        await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
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

            await Task.Delay(MonitoringTiming.TransientErrorReloadSettleMs, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch click started (session): subProfile={subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TrySwitchSubProfileOnPageOnceAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["avito.subProfileId"] = subProfileId,
                ["page.url"] = page.Url
            });

        if (!IsAvitoProfileAutomationTab(page.Url))
        {
            page = await WarmUpSessionPageAsync(page, "session", cancellationToken).ConfigureAwait(false);
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
                    $"AdsPower profile-switch (session): auto-login recovered before switch for subProfile {subProfileId}.",
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
                    $"AdsPower profile-switch (session): login page detected for subProfile {subProfileId}, skipping.",
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
                    $"AdsPower profile-switch (session): captcha/firewall detected for subProfile {subProfileId}, skipping.",
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

        await EnsureSwitchModalAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);
        if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
                .ConfigureAwait(false))
        {
            await ThrowIfCaptchaOnPageAsync(page, cancellationToken).ConfigureAwait(false);
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch (session): modal not ready for subProfile {subProfileId}, skipping.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switch_modal_not_ready",
                    ["avito.subProfileId"] = subProfileId,
                    ["page.url"] = page.Url
                });
            return new SubProfileSwitchResult(SubProfileSwitchStatus.ModalNotReady);
        }

        if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId, cancellationToken).ConfigureAwait(false))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch (session): subProfile {subProfileId} already current.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "already_current",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["avito.subProfileId"] = subProfileId
                });
            return SubProfileSwitchResult.Succeeded;
        }

        var switched = await TryClickSubProfileCardAndWaitCloseAsync(page, subProfileId, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (!switched)
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return new SubProfileSwitchResult(SubProfileSwitchStatus.ClickFailed);
        }

        return SubProfileSwitchResult.Succeeded;
    }

    private async Task<bool> VerifyActiveSubProfileOnPageAsync(
        IPage page,
        string subProfileId,
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

        await EnsureSwitchModalAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
            .ConfigureAwait(false);
        if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
                .ConfigureAwait(false))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var maxWaitMs = MonitoringTiming.VerifySubProfileMaxWaitMs;
        var pollMs = MonitoringTiming.VerifySubProfilePollMs;

        for (var elapsed = 0; elapsed < maxWaitMs; elapsed += pollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId, cancellationToken).ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch verify OK for subProfile {subProfileId} ({sw.ElapsedMilliseconds} ms).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(VerifyActiveSubProfileOnPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "verify_ok",
                        ["avito.subProfileId"] = subProfileId,
                        ["verify.waitMs"] = sw.ElapsedMilliseconds
                    });
                return true;
            }

            await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
        }

        await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch verify timed out for subProfile {subProfileId} ({sw.ElapsedMilliseconds} ms).",
            DeskLinkAuditLogLevel.Warning,
            memberName: nameof(VerifyActiveSubProfileOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "verify_timeout",
                ["avito.subProfileId"] = subProfileId,
                ["verify.waitMs"] = sw.ElapsedMilliseconds
            });
        return false;
    }

    private async Task<string> ExtractCandidatesJsonOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints,
        CancellationToken cancellationToken)
    {
        var executeScript = (string script, CancellationToken ct) =>
            EvaluateWithRetryAsync<string>(page, script, ct);

        var waitSw = Stopwatch.StartNew();
        if (IsOnActiveProfileItemsPage(page.Url)
            && AvitoHumanVariation.RollPermille(MonitoringTiming.ItemsLingerChancePermille))
        {
            await HumanDelay.AfterItemsLingerAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureOnCandidatesPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);

        var finalSignature = await AvitoCandidatesPageWaiter
            .TryCaptureListSignatureAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            "AdsPower CDP candidates page stable (session).",
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

        await AvitoCandidatesListPreparer.PrepareAsync(
            executeScript,
            $"AdsPower:{adsPowerUserId}",
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
            CreateCaptchaSolveCallback(page)).ConfigureAwait(false);

        var raw = await EvaluateWithRetryAsync<string>(page, ExtractionScript, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("AdsPower CDP: скрипт извлечения вернул пустой результат.");
        }

        raw = await TryEnrichCandidatesJsonMessengerUrlsAsync(page, raw, messengerEnrichmentHints, cancellationToken)
            .ConfigureAwait(false);

        return raw;
    }

    private async Task<AvitoMoneySidebar?> TryReadMoneySidebarOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        if (!IsOnActiveProfileItemsPage(page.Url))
        {
            if (IsOnCandidatesResponsesPage(page.Url))
            {
                return null;
            }

            try
            {
                await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                {
                    Timeout = 45_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
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
            html = await page.GetContentAsync().ConfigureAwait(false);
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

    private async Task<string> LoadProfileItemsHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        if (!IsOnActiveProfileItemsPage(page.Url))
        {
            try
            {
                await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                {
                    Timeout = 60_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
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
            throw new InvalidOperationException("AdsPower CDP: страница объявлений Avito вернула пустой HTML.");
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);
        return html;
    }

    private async Task<string> LoadBlockedItemsHtmlOnPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        if (!IsOnRejectedTab(page.Url))
        {
            try
            {
                await page.GoToAsync(ProfileBlockedItemsPageUrl, new NavigationOptions
                {
                    Timeout = 60_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
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
            throw new InvalidOperationException("AdsPower CDP: вкладка «С ошибками» вернула пустой HTML.");
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);
        return html;
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
                $"AdsPower profile-items: shell selector wait timed out: {ex.Message}",
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
                $"AdsPower profile-items: items wait timed out, capturing whatever is on the page: {ex.Message}",
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
                $"AdsPower blocked-items: shell wait timed out: {ex.Message}",
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
                $"AdsPower blocked-items: items wait timed out, capturing whatever is on the page: {ex.Message}",
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
        GeeTestV4TaskOptions captchaOptions) : IAdsPowerAccountSession
    {
        public string AdsPowerUserId { get; } = adsPowerUserId;

        public string? CurrentPageUrl => page.Url;

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
            return await owner.SwitchSubProfileOnPageAsync(page, subProfileId, AdsPowerUserId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.VerifyActiveSubProfileOnPageAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> ExtractCandidatesJsonAsync(
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner
                .ExtractCandidatesJsonOnPageAsync(page, AdsPowerUserId, messengerEnrichmentHints, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.LoadProfileItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.LoadBlockedItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AvitoMoneySidebar?> TryReadMoneySidebarAsync(CancellationToken cancellationToken = default)
        {
            using var _ = AvitoCaptchaTaskContext.Use(captchaOptions);
            return await owner.TryReadMoneySidebarOnPageAsync(page, AdsPowerUserId, cancellationToken).ConfigureAwait(false);
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
