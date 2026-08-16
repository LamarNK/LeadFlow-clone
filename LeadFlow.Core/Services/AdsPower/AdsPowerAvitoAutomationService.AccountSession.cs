using System.Diagnostics;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.AdsPower;

public sealed partial class AdsPowerAvitoAutomationService
{
    public async Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await OpenAccountSessionOnceAsync(options, adsPowerUserId, attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryableAdsPowerStartupFailure(ex) && attempt < 2)
            {
                lastError = ex;
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower account session: попытка {attempt} не открыла Avito ({ex.Message}), перезапускаем браузер.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(OpenAccountSessionAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "session_retry",
                        ["attempt"] = attempt,
                        ["adsPower.userId"] = adsPowerUserId,
                        ["error.type"] = ex.GetType().FullName
                    });
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("AdsPower: не удалось открыть сессию аккаунта.");
    }

    private async Task<IAdsPowerAccountSession> OpenAccountSessionOnceAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var started = false;
        IBrowser? browser = null;
        try
        {
            var start = await adsPowerApiClient
                .StartBrowserAsync(options, adsPowerUserId, ProfileItemsPageUrl, cancellationToken)
                .ConfigureAwait(false);
            started = true;

            if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
            {
                throw new InvalidOperationException(
                    "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
            }

            browser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserWSEndpoint = start.WebSocketDebuggerUrl,
                DefaultViewport = null
            }).ConfigureAwait(false);

            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileItemsPageUrl,
                    nameof(OpenAccountSessionAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);

            page = await WarmUpSessionPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower account session opened for user {adsPowerUserId}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(OpenAccountSessionAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "session_opened",
                    ["attempt"] = attempt,
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url
                });

            return new AccountSession(this, browser, page, options, adsPowerUserId);
        }
        catch
        {
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
                    await adsPowerApiClient.StopBrowserAsync(options, adsPowerUserId, cancellationToken)
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

    private async Task<IPage> WarmUpSessionPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.BringToFrontAsync().ConfigureAwait(false);
        }
        catch
        {
            // не критично
        }

        var currentUrl = await ReadPageUrlAsync(page).ConfigureAwait(false);
        if (IsReusableStartupPlaceholderUrl(currentUrl) || !IsUsableWorkerPageUrl(currentUrl))
        {
            page = await NavigateOffStartupPlaceholderAsync(
                    page,
                    ProfileItemsPageUrl,
                    nameof(WarmUpSessionPageAsync),
                    cancellationToken)
                .ConfigureAwait(false);
            currentUrl = await ReadPageUrlAsync(page).ConfigureAwait(false);
        }

        if (!IsAvitoProfileAutomationTab(currentUrl))
        {
            await TryCdpPageNavigateAsync(page, ProfileItemsPageUrl, cancellationToken).ConfigureAwait(false);
            page = await PollUntilAvitoPageAsync(page, cancellationToken).ConfigureAwait(false);
            currentUrl = await ReadPageUrlAsync(page).ConfigureAwait(false);
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

        if (IsOnActiveProfileItemsPage(page.Url))
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

    private async Task<bool> SwitchSubProfileOnPageAsync(
        IPage page,
        string subProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return false;
        }

        for (var attempt = 1; attempt <= MonitoringTiming.SubProfileSwitchMaxAttempts; attempt++)
        {
            if (await TrySwitchSubProfileOnPageOnceAsync(page, subProfileId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }

            var postFailState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (postFailState?.HasLoginForm == true
                || postFailState?.PageKind == AvitoPageKind.Login
                || postFailState?.HasCaptcha == true
                || postFailState?.PageKind == AvitoPageKind.Captcha)
            {
                return false;
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
                    ["avito.subProfileId"] = subProfileId,
                    ["page.url"] = page.Url
                });

            await RecoverPageBeforeSubProfileSwitchRetryAsync(page, cancellationToken, attempt)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task RecoverPageBeforeSubProfileSwitchRetryAsync(
        IPage page,
        CancellationToken cancellationToken,
        int attempt)
    {
        await DismissAvitoBlockingOverlaysAsync(page, cancellationToken).ConfigureAwait(false);
        await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
        await BounceToDashboardBeforeSwitchAsync(page, cancellationToken, nameof(SwitchSubProfileOnPageAsync))
            .ConfigureAwait(false);
        await Task.Delay(attempt * 1200, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySwitchSubProfileOnPageOnceAsync(
        IPage page,
        string subProfileId,
        CancellationToken cancellationToken)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch click started (session): subProfile={subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TrySwitchSubProfileOnPageOnceAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["avito.subProfileId"] = subProfileId,
                ["page.url"] = page.Url
            });

        if (!IsAvitoProfileAutomationTab(page.Url))
        {
            page = await WarmUpSessionPageAsync(page, "session", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await page.BringToFrontAsync().ConfigureAwait(false);
        }
        catch
        {
            // не критично
        }

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
                return false;
            }
        }

        if (preSwitchState?.HasCaptcha == true || preSwitchState?.PageKind == AvitoPageKind.Captcha)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch (session): captcha/firewall detected for subProfile {subProfileId}, skipping.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = preSwitchState.HasFirewallIp ? "firewall_ip" : "captcha",
                    ["avito.subProfileId"] = subProfileId,
                    ["page.url"] = page.Url,
                    ["pageState"] = preSwitchState.DescribeForDiagnostics()
                });
            return false;
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
            return false;
        }

        if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId).ConfigureAwait(false))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch (session): subProfile {subProfileId} already current.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(SwitchSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "already_current",
                    ["avito.subProfileId"] = subProfileId
                });
            return true;
        }

        var switched = await TryClickSubProfileCardAndWaitCloseAsync(page, subProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!switched)
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
        }

        return switched;
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

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId).ConfigureAwait(false))
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
            messengerEnrichmentHints?.IsOpenPhoneWatchAsync).ConfigureAwait(false);

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
        AdsPowerConnectionOptions options,
        string adsPowerUserId) : IAdsPowerAccountSession
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

        public Task<bool> SwitchSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default) =>
            owner.SwitchSubProfileOnPageAsync(page, subProfileId, cancellationToken);

        public Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default) =>
            owner.VerifyActiveSubProfileOnPageAsync(page, subProfileId, cancellationToken);

        public Task<string> ExtractCandidatesJsonAsync(
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            CancellationToken cancellationToken = default) =>
            owner.ExtractCandidatesJsonOnPageAsync(page, AdsPowerUserId, messengerEnrichmentHints, cancellationToken);

        public Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            owner.LoadProfileItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken);

        public Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            owner.LoadBlockedItemsHtmlOnPageAsync(page, AdsPowerUserId, cancellationToken);

        public Task<AvitoMoneySidebar?> TryReadMoneySidebarAsync(CancellationToken cancellationToken = default) =>
            owner.TryReadMoneySidebarOnPageAsync(page, AdsPowerUserId, cancellationToken);

        public Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default) =>
            owner.CaptureProfileSwitchHtmlInSessionAsync(page, AdsPowerUserId, cancellationToken);

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
