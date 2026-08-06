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

        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var browser = await Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        }).ConfigureAwait(false);

        // AdsPower при старте поднимает несколько вкладок — даём браузеру подключиться, затем одна свежая вкладка.
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

        IPage page;
        try
        {
            page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileItemsPageUrl,
                    nameof(OpenAccountSessionAsync),
                    cancellationToken)
                .ConfigureAwait(false);

            page = await WarmUpSessionPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                browser.Disconnect();
            }
            catch
            {
                // ignore cleanup errors
            }

            throw;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower account session opened for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(OpenAccountSessionAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "session_opened",
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = page.Url
            });

        return new AccountSession(this, browser, page, options, adsPowerUserId);
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

        if (!IsAvitoProfileAutomationTab(page.Url))
        {
            try
            {
                await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                {
                    Timeout = 90_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                {
                    Timeout = 90_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
            }
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
                if (await VerifyActiveSubProfileOnPageAsync(page, subProfileId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
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

        var switched = await TryClickSubProfileCardAsync(page, subProfileId, cancellationToken)
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

        if (IsOnCandidatesResponsesPage(page.Url))
        {
            await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
                .ConfigureAwait(false);
        }

        await EnsureSwitchModalAsync(page, cancellationToken, nameof(VerifyActiveSubProfileOnPageAsync))
            .ConfigureAwait(false);
        var result = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                AvitoInteractionWaiter.Target.SubProfileSwitch,
                new AvitoInteractionWaiter.Options(
                    MonitoringTiming.VerifySubProfileMaxWaitMs,
                    MonitoringTiming.VerifySubProfilePollMs,
                    subProfileId,
                    "subprofile_switch"),
                cancellationToken)
            .ConfigureAwait(false);

        await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
        if (result.Status == AvitoInteractionWaiter.Status.Ready)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch verify OK for subProfile {subProfileId} ({result.WaitMs} ms).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(VerifyActiveSubProfileOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "verify_ok",
                    ["avito.subProfileId"] = subProfileId,
                    ["verify.waitMs"] = result.WaitMs,
                    ["avito.currentSubProfileId"] = result.CurrentSubProfileId
                });
            return true;
        }

        if (result.Status is AvitoInteractionWaiter.Status.LoginRequired or AvitoInteractionWaiter.Status.CaptchaOrFirewall)
        {
            ThrowForInteractionFailure(result);
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch verify timed out for subProfile {subProfileId} ({result.WaitMs} ms).",
            DeskLinkAuditLogLevel.Warning,
            memberName: nameof(VerifyActiveSubProfileOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "verify_timeout",
                ["avito.subProfileId"] = subProfileId,
                ["verify.waitMs"] = result.WaitMs,
                ["ready.reason"] = result.Reason,
                ["avito.currentSubProfileId"] = result.CurrentSubProfileId
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

        _ = GlobalLogger.Instance.LogAsync(
            "AdsPower CDP candidates page action-ready (session).",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(ExtractCandidatesJsonOnPageAsync),
            properties: new Dictionary<string, object?>
            {
                ["step"] = "candidates_ready",
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = page.Url,
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

        var sidebarReady = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                AvitoInteractionWaiter.Target.ProfileBalance,
                new AvitoInteractionWaiter.Options(12_000, 500, Operation: "balance_sidebar"),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForInteractionFailure(sidebarReady);
        // Timeout remains best-effort: Avito can render the money widget after the profile shell.

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
        var result = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                AvitoInteractionWaiter.Target.ProfileItems,
                new AvitoInteractionWaiter.Options(30_000, 750, Operation: "profile_items_shell"),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForInteractionFailure(result);
        if (result.Status == AvitoInteractionWaiter.Status.TimedOut)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower profile-items: action-ready wait timed out.",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "shell_timeout",
                    ["page.url"] = result.Url,
                    ["ready.reason"] = result.Reason
                });
        }
    }

    private static async Task WaitForProfileItemsReadyAsync(
        IPage page,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        var result = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                AvitoInteractionWaiter.Target.ProfileItems,
                new AvitoInteractionWaiter.Options(60_000, 750, Operation: "profile_items"),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForInteractionFailure(result);
        if (result.Status == AvitoInteractionWaiter.Status.TimedOut)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower profile-items: action-ready wait timed out, capturing whatever is on the page.",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "items_timeout",
                    ["page.url"] = result.Url,
                    ["ready.reason"] = result.Reason
                });
        }
    }

    private static async Task WaitForBlockedItemsReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        var result = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                AvitoInteractionWaiter.Target.BlockedItems,
                new AvitoInteractionWaiter.Options(60_000, 750, Operation: "blocked_items"),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForInteractionFailure(result);
        if (result.Status == AvitoInteractionWaiter.Status.TimedOut)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower blocked-items: action-ready wait timed out, capturing whatever is on the page.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LoadBlockedItemsHtmlOnPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "items_timeout",
                    ["page.url"] = result.Url,
                    ["ready.reason"] = result.Reason
                });
        }
    }

    private static void ThrowForInteractionFailure(AvitoInteractionWaiter.Result result)
    {
        if (result.Status == AvitoInteractionWaiter.Status.LoginRequired)
        {
            throw new AvitoLoginRequiredException(result.Url, result.PageState?.Title);
        }

        if (result.Status == AvitoInteractionWaiter.Status.CaptchaOrFirewall)
        {
            throw new AvitoCaptchaDetectedException(
                result.PageState?.HasFirewallIp == true ? "firewall" : "captcha",
                result.Url,
                html: null);
        }

        if (result.Status is AvitoInteractionWaiter.Status.ProbeFailed or AvitoInteractionWaiter.Status.ContextMismatch)
        {
            throw new InvalidOperationException(
                $"Avito interaction cannot continue for {result.Target}: {result.Reason}.");
        }
    }

    private static async Task WaitForInteractionBestEffortAsync(
        IPage page,
        AvitoInteractionWaiter.Target target,
        string operation,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        var result = await AvitoInteractionWaiter.WaitOnPageAsync(
                page,
                target,
                new AvitoInteractionWaiter.Options(60_000, 750, Operation: operation),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForInteractionFailure(result);
        if (result.Status != AvitoInteractionWaiter.Status.TimedOut)
        {
            return;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower {operation}: action-ready wait timed out, preserving best-effort capture.",
            DeskLinkAuditLogLevel.Warning,
            memberName: callerMemberName,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "interaction_timeout",
                ["avito.operation"] = operation,
                ["ready.reason"] = result.Reason,
                ["ready.waitMs"] = result.WaitMs,
                ["page.url"] = result.Url
            });
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
