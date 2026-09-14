using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Captcha;

public sealed class AvitoGeeTestSolver(
    IRuCaptchaClient ruCaptcha,
    IWorkerConfigProvider configProvider,
    ICaptchaProviderRequestReporter? requestReporter = null) : IAvitoGeeTestSolver
{
    private const int MaxAttempts = AvitoGeeTestSolveSupport.MaxGeeTestAttempts;
    private const int PostVerifyNavigationTimeoutMs = 20_000;
    private const int PostVerifyPaintPolls = 6;
    private const int PostVerifyPaintPollMs = 400;
    private const int LoginClickCaptchaOutcomePolls = 20;
    private const int LoginClickCaptchaOutcomePollMs = 500;
    private const string LoginClickCaptchaImageSelector = ".geetest_click .geetest_bg, [class*='geetest_click'] [class*='geetest_bg']";
    private const string LoginNineGridCaptchaImageSelector = ".geetest_nine, [class*='geetest_nine']";
    private const string LoginClickCaptchaPreparedHostId = "leadflow-geetest-capture";
    private const string LoginClickCaptchaPreparedImageSelector = "#leadflow-geetest-capture-image";
    private const string LoginClickCaptchaPreparedHintSelector = "#leadflow-geetest-capture-hint";
    private static readonly SemaphoreSlim Gate = new(
        AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves,
        AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves);
    private static readonly ConditionalWeakTable<IPage, SemaphoreSlim> PageGates = new();

    public Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        CancellationToken cancellationToken = default) =>
        TrySolveOnPageAsync(page, html, pageUrl, taskOptions: null, cancellationToken);

    public async Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken = default)
    {
        var config = await ResolveConfigAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = string.IsNullOrWhiteSpace(config?.RuCaptchaApiKey) ? null : config.RuCaptchaApiKey.Trim();
        var dynamicContextEnabled = config?.GeeTestDynamicContextEnabled == true;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: автопроход GeeTest v4 пропущен — нет ключа RuCaptcha в карточке воркера.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_solve_skipped_no_key",
                    ["page.url"] = pageUrl ?? page.Url
                });
            return false;
        }

        var pageGate = GetPageGate(page);
        await pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateAcquired = false;
        try
        {
            html ??= await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
            if (AvitoCaptchaDetector.HasIpBlockChallenge(html))
            {
                return false;
            }

            if (AvitoCaptchaRedirectRecovery.RequiresRecovery(html))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: Avito показывает «перенаправление» — новую задачу RuCaptcha не создаём, восстанавливаем страницу.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_redirect_recover_without_provider",
                        ["page.url"] = pageUrl ?? page.Url
                    });
                var redirectLeave = await LeaveCaptchaPageAsync(page, cancellationToken).ConfigureAwait(false);
                return redirectLeave.Recovered
                       && !string.IsNullOrWhiteSpace(redirectLeave.Html)
                       && !AvitoCaptchaDetector.IsCaptchaHtml(redirectLeave.Html);
            }

            if (!AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html))
            {
                return false;
            }

            if (AvitoGeeTestSolveSupport.IsLoginGeeTestOverlay(html))
            {
                if (!AvitoGeeTestSolveSupport.IsLoginClickCaptchaOverlay(html))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: GeeTest на логине не является ClickCaptcha — координатную задачу не создаём.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_login_unsupported_geetest_kind",
                            ["page.url"] = pageUrl ?? page.Url
                        });
                    return false;
                }

                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
                return await TrySolveLoginOverlayAsync(
                        page,
                        html,
                        pageUrl,
                        apiKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var contextCapture = await StartContextCaptureAsync(page).ConfigureAwait(false);
            var activation = await ActivateAndProbeCaptchaAsync(page, cancellationToken).ConfigureAwait(false);

            var baseTaskOptions = taskOptions ?? AvitoCaptchaTaskContext.Options ?? new GeeTestV4TaskOptions();
            var effectiveTaskOptions = baseTaskOptions.WithUserAgent(activation.UserAgent);

            if (!activation.IsGeeTest && !activation.IsHCaptcha && activation.Kind != AvitoCaptchaKind.Internal)
            {
                var afterProbeHtml = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
                if (AvitoCaptchaRedirectRecovery.RequiresRecovery(afterProbeHtml))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: activate не подтвердил вид проверки, потому что Avito уже на «перенаправление».",
                        DeskLinkAuditLogLevel.Info,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_activation_became_redirect",
                            ["page.url"] = page.Url
                        });
                    var afterProbeLeave = await LeaveCaptchaPageAsync(page, cancellationToken).ConfigureAwait(false);
                    return afterProbeLeave.Recovered
                           && !string.IsNullOrWhiteSpace(afterProbeLeave.Html)
                           && !AvitoCaptchaDetector.IsCaptchaHtml(afterProbeLeave.Html);
                }

                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: автопроход пропущен — Avito выбрал «{activation.Kind.ToLogValue()}».",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_activation_unsupported",
                        ["page.url"] = page.Url,
                        ["captcha.kind"] = activation.Kind.ToLogValue(),
                        ["captcha.serverKind"] = activation.ServerKind,
                        ["captcha.continueClicked"] = activation.ContinueClicked,
                        ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(activation.UserAgent)
                    });
                return false;
            }

            // Клик «Продолжить» и ожидание появления виджета идут в каждом браузере параллельно.
            // Общая очередь начинается только перед платной задачей RuCaptcha.
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            if (activation.IsHCaptcha)
            {
                return await TrySolveHCaptchaAsync(
                        page,
                        html,
                        pageUrl,
                        apiKey,
                        activation,
                        effectiveTaskOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (activation.Kind == AvitoCaptchaKind.Internal)
            {
                return await TrySolveInternalCaptchaAsync(
                        page,
                        apiKey,
                        activation,
                        effectiveTaskOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var contextTracker = new GeeTestV4AttemptContextTracker();
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 1)
                {
                    html = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
                    if (AvitoCaptchaDetector.HasIpBlockChallenge(html)
                        || AvitoCaptchaRedirectRecovery.RequiresRecovery(html)
                        || !AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html))
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            "Captcha: после обновления исчезла GeeTest-сессия — новую задачу не создаём.",
                            DeskLinkAuditLogLevel.Info,
                            properties: new Dictionary<string, object?> { ["step"] = "captcha_retry_context_unavailable", ["captcha.attempt"] = attempt, ["page.url"] = page.Url });
                        return false;
                    }

                    contextCapture?.Reset();
                    activation = await ActivateAndProbeCaptchaAsync(page, cancellationToken).ConfigureAwait(false);
                    if (!activation.IsGeeTest)
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            "Captcha: после обновления Avito выбрал другой тип проверки — GeeTest-задачу не создаём.",
                            DeskLinkAuditLogLevel.Info,
                            properties: new Dictionary<string, object?> { ["step"] = "captcha_retry_kind_changed", ["captcha.attempt"] = attempt, ["captcha.kind"] = activation.Kind.ToLogValue() });
                        return false;
                    }

                    effectiveTaskOptions = baseTaskOptions.WithUserAgent(activation.UserAgent);
                }

                var liveContext = contextCapture is null
                    ? null
                    : await contextCapture.WaitForContextAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var captchaId = dynamicContextEnabled ? liveContext?.CaptchaId : null;
                captchaId ??= AvitoCaptchaDetector.ExtractGeeTestCaptchaId(html);
                var context = liveContext ?? new GeeTestV4SessionContext(captchaId, null, null, "fallback", DateTime.UtcNow);
                if (string.IsNullOrWhiteSpace(context.CaptchaId))
                {
                    context = context with { CaptchaId = captchaId };
                }

                if (dynamicContextEnabled)
                {
                    effectiveTaskOptions = effectiveTaskOptions.WithSessionContext(context);
                }

                if (dynamicContextEnabled && !contextTracker.TryUse(context))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: контекст GeeTest повторился — платную задачу не создаём.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?> { ["step"] = "captcha_duplicate_context", ["captcha.attempt"] = attempt, ["captcha.contextFingerprint"] = context.Fingerprint });
                    return false;
                }

                var websiteUrl = string.IsNullOrWhiteSpace(pageUrl) ? page.Url : pageUrl;
                if (string.IsNullOrWhiteSpace(websiteUrl))
                {
                    websiteUrl = "https://www.avito.ru/";
                }

                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: RuCaptcha GeeTest v4, попытка {attempt}/{MaxAttempts}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_solve_start",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt,
                        ["captcha.proxyMode"] = effectiveTaskOptions.UsesSuppliedProxy ? "profile" : "proxyless",
                        ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(effectiveTaskOptions.UserAgent),
                        ["captcha.contextSource"] = context.Source,
                        ["captcha.contextFingerprint"] = context.Fingerprint,
                        ["captcha.challengePresent"] = context.HasChallenge,
                        ["captcha.riskTypePresent"] = context.HasRiskType,
                        ["captcha.dynamicContextEnabled"] = dynamicContextEnabled,
                        ["captcha.concurrentLimit"] = AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves
                    });

                GeeTestV4Solution solution;
                var requestId = await CreateProviderRequestAsync(page, "geetest_v4", attempt, MaxAttempts, websiteUrl, context.ToDiagnostics(DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
                var solveStarted = Stopwatch.GetTimestamp();
                try
                {
                    solution = await ruCaptcha
                        .SolveGeeTestV4Async(apiKey, websiteUrl, captchaId, effectiveTaskOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (requestId is Guid failedRequestId)
                        await requestReporter!.MarkProviderFailedAsync(failedRequestId, ex.Message.Contains("NO_SLOT", StringComparison.OrdinalIgnoreCase) ? "no_slot" : "error", ex.GetType().Name, cancellationToken, ElapsedMilliseconds(solveStarted)).ConfigureAwait(false);
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Captcha: RuCaptcha не решила GeeTest v4 — {ex.Message}",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_solve_api_failed",
                            ["page.url"] = websiteUrl,
                            ["captcha.attempt"] = attempt,
                            ["captcha.proxyMode"] = effectiveTaskOptions.UsesSuppliedProxy ? "profile" : "proxyless"
                        });
                    await DelayBeforeRetryAsync(page, attempt, "RuCaptcha не решила", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var solveDurationMs = ElapsedMilliseconds(solveStarted);
                if (requestId is Guid acceptedRequestId)
                    await requestReporter!.MarkProviderAcceptedAsync(acceptedRequestId, solution.ProviderTask?.Id.ToString() ?? string.Empty, cancellationToken, solveDurationMs).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(solution.CaptchaId))
                {
                    solution = solution with { CaptchaId = captchaId };
                }

                var currentContext = contextCapture?.Snapshot();
                if (dynamicContextEnabled && !GeeTestV4AttemptContextTracker.IsCurrent(context, currentContext))
                {
                    var observedContext = currentContext!;
                    if (requestId is Guid changedRequestId)
                        await requestReporter!.MarkTargetOutcomeAsync(changedRequestId, "rejected", cancellationToken, "context_changed", null, observedContext.ToDiagnostics(DateTime.UtcNow).ContextAgeMs).ConfigureAwait(false);
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: контекст GeeTest изменился до verify — устаревший токен не отправляем.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?> { ["step"] = "captcha_context_changed_before_verify", ["captcha.attempt"] = attempt, ["captcha.contextFingerprint"] = context.Fingerprint });
                    await DelayBeforeRetryAsync(page, attempt, "контекст GeeTest изменился", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var verifyRaw = await EvaluateVerifyAsync(page, solution, cancellationToken).ConfigureAwait(false);
                var verify = AvitoGeeTestSolveSupport.ParseVerifyResult(verifyRaw);
                if (!RuCaptchaResponseParser.IsVerifyAccepted(verifyRaw))
                {
                    var targetReason = verify.StatusCode is null or >= 400 || !string.IsNullOrWhiteSpace(verify.Error)
                        ? "http_error"
                        : "verified_false";
                    if (requestId is Guid rejectedRequestId)
                        await requestReporter!.MarkTargetOutcomeAsync(rejectedRequestId, "rejected", cancellationToken, targetReason, verify.StatusCode, context.ToDiagnostics(DateTime.UtcNow).ContextAgeMs).ConfigureAwait(false);
                    _ = GlobalLogger.Instance.LogAsync(
                        FormatTokenRejectedMessage("GeeTest v4", verify, effectiveTaskOptions),
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_verify_rejected",
                            ["page.url"] = websiteUrl,
                            ["captcha.attempt"] = attempt,
                            ["captcha.verify.status"] = verify.StatusCode,
                            ["captcha.verify.verified"] = verify.Verified,
                            ["captcha.verify"] = verify.Summary,
                            ["captcha.proxyMode"] = effectiveTaskOptions.UsesSuppliedProxy ? "profile" : "proxyless",
                            ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(effectiveTaskOptions.UserAgent),
                            ["captcha.providerReport"] = "skipped_target_rejection"
                        });
                    await DelayBeforeRetryAsync(page, attempt, "токен отклонён", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var leaveResult = await LeaveCaptchaPageAsync(page, cancellationToken, afterAcceptedVerify: true)
                    .ConfigureAwait(false);
                var after = leaveResult.Html;
                if (LeftCaptcha(leaveResult))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: GeeTest v4 пройдена через RuCaptcha.",
                        DeskLinkAuditLogLevel.Info,
                        properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_solved",
                            ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt
                    });
                    await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: true, "GeeTest v4", cancellationToken)
                        .ConfigureAwait(false);
                    AvitoCaptchaTaskContext.NoteSolved();
                    if (requestId is Guid solvedRequestId)
                        await requestReporter!.MarkTargetOutcomeAsync(solvedRequestId, "accepted", cancellationToken, "verified_true", verify.StatusCode, context.ToDiagnostics(DateTime.UtcNow).ContextAgeMs).ConfigureAwait(false);
                    return true;
                }

                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: проверка принята, но Avito не вышел с экрана капчи после восстановления страницы.",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_post_verify_stuck",
                        ["page.url"] = page.Url,
                        ["captcha.recoveryAttempts"] = leaveResult.Attempts,
                        ["captcha.redirectPending"] = AvitoCaptchaRedirectRecovery.RequiresRecovery(after),
                        ["captcha.htmlReceived"] = !string.IsNullOrWhiteSpace(after),
                        ["captcha.proxyMode"] = effectiveTaskOptions.UsesSuppliedProxy ? "profile" : "proxyless"
                    });
                if (requestId is Guid stuckRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(stuckRequestId, "rejected", cancellationToken, "left_captcha", null, context.ToDiagnostics(DateTime.UtcNow).ContextAgeMs).ConfigureAwait(false);
                await DelayBeforeRetryAsync(page, attempt, "страница не ушла после verify", cancellationToken)
                    .ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            if (gateAcquired)
            {
                Gate.Release();
            }

            pageGate.Release();
        }
    }

    private async Task<Guid?> CreateProviderRequestAsync(
        IPage page,
        string captchaType,
        int attempt,
        int maxAttempts,
        string pageUrl,
        CaptchaContextDiagnostics? diagnostics = null,
        CancellationToken ct = default)
    {
        if (requestReporter is null)
        {
            LogProviderRequestDiagnostic(
                "Captcha: статистика запроса провайдеру недоступна — reporter не настроен.",
                "captcha_provider_request_reporter_missing",
                captchaType,
                attempt,
                maxAttempts,
                pageUrl,
                null);
            return null;
        }

        var requestContext = CaptchaProviderRequestContext.Current;
        if (requestContext is null)
        {
            LogProviderRequestDiagnostic(
                "Captcha: задача провайдеру выполняется без контекста статистики.",
                "captcha_provider_request_context_missing",
                captchaType,
                attempt,
                maxAttempts,
                pageUrl,
                null);
            return null;
        }

        byte[]? screenshot = null;
        try { screenshot = await page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png, FullPage = true }).ConfigureAwait(false); } catch { }
        try
        {
            var requestId = await requestReporter.CreateAsync(new CaptchaProviderRequestSubmission(
                requestContext.AccountId, requestContext.CycleRunId, requestContext.SubProfileRunId, requestContext.SubProfileId,
                requestContext.SubProfileName, "rucaptcha", captchaType, requestContext.Stage, requestContext.Reason,
                attempt, maxAttempts, SafePageUrl(pageUrl), DateTime.UtcNow, diagnostics), screenshot, ct).ConfigureAwait(false);
            if (requestId is null)
            {
                LogProviderRequestDiagnostic(
                    "Captcha: API не создал запись статистики запроса провайдеру.",
                    "captcha_provider_request_create_returned_null",
                    captchaType,
                    attempt,
                    maxAttempts,
                    pageUrl,
                    null);
            }

            return requestId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProviderRequestDiagnostic(
                "Captcha: ошибка записи статистики запроса провайдеру.",
                "captcha_provider_request_create_failed",
                captchaType,
                attempt,
                maxAttempts,
                pageUrl,
                ex);
            return null;
        }
    }

    private static void LogProviderRequestDiagnostic(
        string message,
        string step,
        string captchaType,
        int attempt,
        int maxAttempts,
        string? pageUrl,
        Exception? exception)
    {
        _ = GlobalLogger.Instance.LogAsync(
            message,
            DeskLinkAuditLogLevel.Warning,
            properties: new Dictionary<string, object?>
            {
                ["step"] = step,
                ["captcha.type"] = captchaType,
                ["captcha.attempt"] = attempt,
                ["captcha.maxAttempts"] = maxAttempts,
                ["page.host"] = SafePageHost(pageUrl),
                ["error.type"] = exception?.GetType().Name
            });
    }

    private static string? SafePageHost(string? pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string? SafePageUrl(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static async Task<GeeTestV4NetworkContextCapture?> StartContextCaptureAsync(IPage page)
    {
        try
        {
            return await GeeTestV4NetworkContextCapture.StartAsync(page).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось включить CDP-сбор параметров GeeTest — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_context_capture_failed" });
            return null;
        }
    }

    private static int ElapsedMilliseconds(long startedTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        return (int)Math.Clamp(elapsed, 0, int.MaxValue);
    }

    private async Task<bool> TrySolveLoginOverlayAsync(
        IPage page,
        string? html,
        string? pageUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var websiteUrl = string.IsNullOrWhiteSpace(pageUrl) ? page.Url : pageUrl;
        if (string.IsNullOrWhiteSpace(websiteUrl))
        {
            websiteUrl = "https://www.avito.ru/";
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            html ??= await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
            if (!AvitoGeeTestSolveSupport.IsLoginClickCaptchaOverlay(html))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: ClickCaptcha логина исчезла до создания задачи.",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_click_widget_missing",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt
                    });
                return false;
            }

            var capture = await CaptureLoginClickCaptchaAsync(page, cancellationToken).ConfigureAwait(false);
            if (capture is null)
            {
                await DelayLoginOverlayRetryAsync(page, attempt, "не удалось снять изображение ClickCaptcha", cancellationToken)
                    .ConfigureAwait(false);
                html = null;
                continue;
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: RuCaptcha ClickCaptcha на логине, попытка {attempt}/{MaxAttempts}.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_login_solve_start",
                    ["page.url"] = websiteUrl,
                    ["captcha.attempt"] = attempt
                });

            ClickCaptchaSolution solution;
            var requestId = await CreateProviderRequestAsync(page, "click", attempt, MaxAttempts, websiteUrl, ct: cancellationToken).ConfigureAwait(false);
            try
            {
                solution = await ruCaptcha
                    .SolveClickCaptchaAsync(
                        apiKey,
                        capture.ImageBody,
                        capture.HintImageBody,
                        capture.HintText,
                        capture.RequiredClicks,
                        "ru",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (requestId is Guid failedRequestId)
                    await requestReporter!.MarkProviderFailedAsync(failedRequestId, ex.Message.Contains("NO_SLOT", StringComparison.OrdinalIgnoreCase) ? "no_slot" : "error", ex.GetType().Name, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: RuCaptcha не решила ClickCaptcha логина — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_solve_api_failed",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt
                    });
                await DelayLoginOverlayRetryAsync(page, attempt, "RuCaptcha не решила", cancellationToken)
                    .ConfigureAwait(false);
                html = null;
                continue;
            }

            if (requestId is Guid acceptedRequestId)
                await requestReporter!.MarkProviderAcceptedAsync(acceptedRequestId, solution.ProviderTask?.Id ?? string.Empty, cancellationToken).ConfigureAwait(false);

            var applyResult = await ApplyLoginClickCaptchaAsync(page, capture, solution, cancellationToken).ConfigureAwait(false);
            if (applyResult == LoginClickCaptchaApplyResult.ChallengeChanged)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: изображение ClickCaptcha сменилось до применения ответа — решаем актуальный раунд без report incorrect.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_challenge_changed_before_apply",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt
                    });
                html = null;
                continue;
            }

            if (applyResult != LoginClickCaptchaApplyResult.Applied)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: координаты ClickCaptcha логина не применились.",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_apply_failed",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt
                    });
                await DelayLoginOverlayRetryAsync(page, attempt, "координаты не применились", cancellationToken)
                    .ConfigureAwait(false);
                html = null;
                continue;
            }

            var outcome = await WaitForLoginClickCaptchaOutcomeAsync(page, capture.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (outcome == LoginClickCaptchaOutcome.Accepted)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: ClickCaptcha логина пройдена через RuCaptcha.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_solved",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt,
                        ["captcha.points"] = solution.Points.Count
                    });
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: true, "ClickCaptcha логина", cancellationToken)
                    .ConfigureAwait(false);
                if (requestId is Guid targetAcceptedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(targetAcceptedRequestId, "accepted", cancellationToken).ConfigureAwait(false);
                AvitoCaptchaTaskContext.NoteSolved();
                return true;
            }

            if (outcome == LoginClickCaptchaOutcome.NextRound)
            {
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: true, "ClickCaptcha логина", cancellationToken)
                    .ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: текущий раунд ClickCaptcha принят, GeeTest показала следующий — продолжаем без обновления виджета.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_login_next_round",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt
                    });
                await Task.Delay(LoginClickCaptchaOutcomePollMs, cancellationToken).ConfigureAwait(false);
                html = null;
                continue;
            }

            if (outcome == LoginClickCaptchaOutcome.Rejected)
            {
                if (requestId is Guid rejectedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(rejectedRequestId, "rejected", cancellationToken).ConfigureAwait(false);
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: false, "ClickCaptcha логина", cancellationToken)
                    .ConfigureAwait(false);
            }

            _ = GlobalLogger.Instance.LogAsync(
                outcome == LoginClickCaptchaOutcome.Rejected
                    ? "Captcha: GeeTest явно отклонила ClickCaptcha; RuCaptcha получила report incorrect."
                    : "Captcha: GeeTest не сообщила окончательный исход ClickCaptcha; report не отправлен.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_login_click_overlay_visible",
                    ["page.url"] = page.Url,
                    ["captcha.attempt"] = attempt,
                    ["captcha.outcome"] = outcome.ToString()
                });
            await DelayLoginOverlayRetryAsync(
                    page,
                    attempt,
                    outcome == LoginClickCaptchaOutcome.Rejected
                        ? "GeeTest явно отклонила ClickCaptcha"
                        : "исход ClickCaptcha не прочитался",
                    cancellationToken)
                .ConfigureAwait(false);
            html = null;
        }

        return false;
    }

    private static async Task<LoginClickCaptchaOutcome> WaitForLoginClickCaptchaOutcomeAsync(
        IPage page,
        string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        var lastOutcome = LoginClickCaptchaOutcome.Unknown;
        var consecutiveOverlayGonePolls = 0;
        for (var poll = 0; poll < LoginClickCaptchaOutcomePolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadLoginClickCaptchaStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (state.Readable)
            {
                lastOutcome = AvitoGeeTestSolveSupport.ClassifyLoginClickCaptchaOutcome(
                    state.OverlayVisible,
                    state.ExplicitAccepted,
                    state.ExplicitRejected,
                    expectedFingerprint,
                    state.Fingerprint);
                if (lastOutcome == LoginClickCaptchaOutcome.Accepted)
                {
                    if (state.ExplicitAccepted || ++consecutiveOverlayGonePolls >= 2)
                    {
                        return LoginClickCaptchaOutcome.Accepted;
                    }
                }
                else
                {
                    consecutiveOverlayGonePolls = 0;
                }

                if (lastOutcome is LoginClickCaptchaOutcome.Rejected
                    or LoginClickCaptchaOutcome.NextRound)
                {
                    return lastOutcome;
                }
            }
            else
            {
                consecutiveOverlayGonePolls = 0;
                lastOutcome = LoginClickCaptchaOutcome.Unknown;
            }

            if (poll + 1 < LoginClickCaptchaOutcomePolls)
            {
                await Task.Delay(LoginClickCaptchaOutcomePollMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return lastOutcome == LoginClickCaptchaOutcome.Accepted
            ? LoginClickCaptchaOutcome.Pending
            : lastOutcome;
    }

    private static async Task<LoginClickCaptchaCapture?> CaptureLoginClickCaptchaAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stateBeforeCapture = await ReadLoginClickCaptchaStateAsync(page, cancellationToken).ConfigureAwait(false);
            var preparedRaw = await page
                .EvaluateExpressionAsync<string>(BuildPrepareLoginClickCaptchaCaptureScript())
                .ConfigureAwait(false);
            var prepared = ParsePreparedLoginClickCaptchaCapture(preparedRaw);
            if (prepared is null)
            {
                return null;
            }

            var preparedImage = await FindVisibleElementAsync(
                    page,
                    LoginClickCaptchaPreparedImageSelector,
                    cancellationToken)
                .ConfigureAwait(false);
            var preparedHint = await FindVisibleElementAsync(
                    page,
                    LoginClickCaptchaPreparedHintSelector,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preparedImage is null || preparedHint is null)
            {
                return null;
            }

            var imageBody = await preparedImage.ScreenshotBase64Async().ConfigureAwait(false);
            var hintImageBody = await preparedHint.ScreenshotBase64Async().ConfigureAwait(false);
            var (width, height) = ReadPngDimensions(imageBody);
            var state = await ReadLoginClickCaptchaStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (stateBeforeCapture.Readable
                && state.Readable
                && !string.IsNullOrWhiteSpace(stateBeforeCapture.Fingerprint)
                && !string.IsNullOrWhiteSpace(state.Fingerprint)
                && !string.Equals(stateBeforeCapture.Fingerprint, state.Fingerprint, StringComparison.Ordinal))
            {
                return null;
            }

            return width > 0 && height > 0 && !string.IsNullOrWhiteSpace(imageBody) && !string.IsNullOrWhiteSpace(hintImageBody)
                ? new LoginClickCaptchaCapture(
                    imageBody,
                    hintImageBody,
                    prepared.HintText,
                    width,
                    height,
                    prepared.IsNineGrid,
                    prepared.RequiredClicks,
                    state.Fingerprint)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось снять ClickCaptcha логина — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_login_click_capture_failed" });
            return null;
        }
        finally
        {
            await RemovePreparedLoginClickCaptchaCaptureAsync(page).ConfigureAwait(false);
        }
    }

    private static string BuildPrepareLoginClickCaptchaCaptureScript() =>
        $$"""
        (async () => {
          const hostId = '{{LoginClickCaptchaPreparedHostId}}';
          const imageId = '{{LoginClickCaptchaPreparedImageSelector[1..]}}';
          const hintId = '{{LoginClickCaptchaPreparedHintSelector[1..]}}';
          document.getElementById(hostId)?.remove();

          const isVisible = (el) => {
            if (!el) return false;
            for (let node = el; node && node.nodeType === Node.ELEMENT_NODE; node = node.parentElement) {
              const style = window.getComputedStyle(node);
              if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
            }
            const rect = el.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
          };
          const readBackgroundUrl = (el) => {
            if (!el) return '';
            let value = '';
            try { value = window.getComputedStyle(el).backgroundImage || el.style.backgroundImage || ''; } catch {}
            const match = /^url\((['"]?)(.*?)\1\)$/.exec(String(value).trim());
            return match ? match[2] : '';
          };
          const loadImage = (source) => new Promise((resolve, reject) => {
            const image = document.createElement('img');
            image.decoding = 'sync';
            image.draggable = false;
            image.onload = () => resolve(image);
            image.onerror = () => reject(new Error(`image-load-failed:${source}`));
            image.src = source;
          });

          const exactRoots = Array.from(document.querySelectorAll('.geetest_box'));
          const roots = exactRoots.length > 0
            ? exactRoots
            : Array.from(document.querySelectorAll('[class*="geetest_box"]'));
          const root = roots.find((candidate) => isVisible(candidate)
            && candidate.querySelector('.geetest_ques_tips img, [class*="geetest_ques_tips"] img'));
          if (!root) return JSON.stringify({ ok: false, error: 'root-not-found' });

          const clickImage = Array.from(root.querySelectorAll(
              '.geetest_click .geetest_bg, [class*="geetest_click"] [class*="geetest_bg"]'))
            .find(isVisible);
          const nineGrid = Array.from(root.querySelectorAll('.geetest_nine, [class*="geetest_nine"]'))
            .find(isVisible);
          const isNineGrid = !clickImage && !!nineGrid;
          const sourceNode = clickImage
            || (nineGrid && Array.from(nineGrid.querySelectorAll(
                '.geetest_item_img, [class*="geetest_item_img"]')).find(isVisible));
          const imageUrl = readBackgroundUrl(sourceNode);
          const hintUrls = Array.from(root.querySelectorAll(
              '.geetest_ques_tips img, [class*="geetest_ques_tips"] img'))
            .filter(isVisible)
            .map((image) => image.currentSrc || image.src || image.getAttribute('src') || '')
            .filter(Boolean);
          if (!imageUrl || hintUrls.length === 0) {
            return JSON.stringify({ ok: false, error: 'source-assets-not-found' });
          }

          const host = document.createElement('div');
          host.id = hostId;
          Object.assign(host.style, {
            position: 'fixed',
            zIndex: '2147483647',
            left: '0',
            top: '0',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'flex-start',
            gap: '8px',
            padding: '0',
            margin: '0',
            border: '0',
            background: '#fff',
            pointerEvents: 'none',
            transform: 'none',
            animation: 'none',
            transition: 'none'
          });
          document.documentElement.appendChild(host);

          try {
            const mainImage = await loadImage(imageUrl);
            mainImage.id = imageId;
            Object.assign(mainImage.style, {
              display: 'block',
              width: `${mainImage.naturalWidth}px`,
              height: `${mainImage.naturalHeight}px`,
              maxWidth: 'none',
              maxHeight: 'none',
              padding: '0',
              margin: '0',
              border: '0',
              borderRadius: '0',
              objectFit: 'fill',
              transform: 'none',
              animation: 'none',
              transition: 'none'
            });
            host.appendChild(mainImage);

            const hintImages = [];
            for (const source of hintUrls) {
              const image = await loadImage(source);
              hintImages.push(image);
            }
            const gapWidth = Math.max(0, hintImages.length - 1) * 8;
            const naturalWidth = hintImages.reduce((sum, image) => sum + image.naturalWidth, 0);
            const naturalHeight = Math.max(...hintImages.map((image) => image.naturalHeight));
            const scale = Math.min(
              1,
              naturalWidth > 0 ? (400 - gapWidth) / naturalWidth : 1,
              naturalHeight > 0 ? 150 / naturalHeight : 1);
            const widths = hintImages.map((image) => Math.max(1, Math.round(image.naturalWidth * scale)));
            const heights = hintImages.map((image) => Math.max(1, Math.round(image.naturalHeight * scale)));
            const canvas = document.createElement('canvas');
            canvas.id = hintId;
            canvas.dataset.maxWidth = '400px';
            canvas.dataset.maxHeight = '150px';
            canvas.width = Math.max(1, widths.reduce((sum, width) => sum + width, 0) + gapWidth);
            canvas.height = Math.max(1, Math.max(...heights));
            Object.assign(canvas.style, {
              display: 'block',
              width: `${canvas.width}px`,
              height: `${canvas.height}px`,
              maxWidth: '400px',
              maxHeight: '150px',
              padding: '0',
              margin: '0',
              border: '0',
              background: '#fff',
              transform: 'none',
              animation: 'none',
              transition: 'none'
            });
            const context = canvas.getContext('2d');
            if (!context) throw new Error('hint-canvas-context-unavailable');
            context.fillStyle = '#fff';
            context.fillRect(0, 0, canvas.width, canvas.height);
            let offsetX = 0;
            hintImages.forEach((image, index) => {
              context.drawImage(image, offsetX, 0, widths[index], heights[index]);
              offsetX += widths[index] + 8;
            });
            host.appendChild(canvas);

            const hintText = String(
              root.querySelector('.geetest_text_tips, [class*="geetest_text_tips"]')?.textContent || '').trim();
            const requestedGridClicks = Number((hintText.match(/\b(\d+)\b/) || [])[1]);
            const requiredClicks = isNineGrid
              ? (Number.isInteger(requestedGridClicks) && requestedGridClicks > 0
                  ? requestedGridClicks
                  : hintUrls.length)
              : hintUrls.length;
            return JSON.stringify({
              ok: true,
              isNineGrid,
              hintText,
              requiredClicks
            });
          } catch (error) {
            host.remove();
            return JSON.stringify({
              ok: false,
              error: String(error && error.message ? error.message : error)
            });
          }
        })()
        """;

    private static PreparedLoginClickCaptchaCapture? ParsePreparedLoginClickCaptchaCapture(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            return new PreparedLoginClickCaptchaCapture(
                root.TryGetProperty("isNineGrid", out var isNineGrid)
                && isNineGrid.ValueKind == JsonValueKind.True,
                root.TryGetProperty("hintText", out var hintText) ? hintText.GetString() : null,
                root.TryGetProperty("requiredClicks", out var requiredClicks)
                && requiredClicks.TryGetInt32(out var parsedClicks)
                    ? parsedClicks
                    : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task RemovePreparedLoginClickCaptchaCaptureAsync(IPage page)
    {
        try
        {
            await page
                .EvaluateExpressionAsync(
                    $"document.getElementById('{LoginClickCaptchaPreparedHostId}')?.remove()")
                .ConfigureAwait(false);
        }
        catch
        {
            // Страница могла закрыться или перейти дальше во время очистки.
        }
    }

    private static async Task<LoginClickCaptchaApplyResult> ApplyLoginClickCaptchaAsync(
        IPage page,
        LoginClickCaptchaCapture capture,
        ClickCaptchaSolution solution,
        CancellationToken cancellationToken)
    {
        try
        {
            if (solution.Points.Count is < 1 or > 8)
            {
                return LoginClickCaptchaApplyResult.Failed;
            }

            var imageSelector = capture.IsNineGrid
                ? LoginNineGridCaptchaImageSelector
                : LoginClickCaptchaImageSelector;
            var image = await FindVisibleElementAsync(page, imageSelector, cancellationToken).ConfigureAwait(false);
            var box = image is null ? null : await image.BoundingBoxAsync().ConfigureAwait(false);
            if (box is null || box.Width <= 0 || box.Height <= 0)
            {
                return LoginClickCaptchaApplyResult.Failed;
            }

            var currentState = await ReadLoginClickCaptchaStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (currentState.Readable
                && AvitoGeeTestSolveSupport.ClassifyLoginClickCaptchaOutcome(
                    currentState.OverlayVisible,
                    currentState.ExplicitAccepted,
                    currentState.ExplicitRejected,
                    capture.Fingerprint,
                    currentState.Fingerprint) == LoginClickCaptchaOutcome.NextRound)
            {
                return LoginClickCaptchaApplyResult.ChallengeChanged;
            }

            foreach (var point in solution.Points)
            {
                if (point.X < 0 || point.Y < 0 || point.X > capture.ImageWidth || point.Y > capture.ImageHeight)
                {
                    return LoginClickCaptchaApplyResult.Failed;
                }

                var x = box.X + box.Width * point.X / capture.ImageWidth;
                var y = box.Y + box.Height * point.Y / capture.ImageHeight;
                cancellationToken.ThrowIfCancellationRequested();
                await page.Mouse.MoveAsync(x, y, new MoveOptions { Steps = Random.Shared.Next(4, 9) }).ConfigureAwait(false);
                await Task.Delay(Random.Shared.Next(90, 190), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await page.Mouse.ClickAsync(x, y, new ClickOptions { Delay = Random.Shared.Next(35, 85) }).ConfigureAwait(false);
                await Task.Delay(Random.Shared.Next(110, 230), cancellationToken).ConfigureAwait(false);
            }

            var submit = await FindVisibleElementAsync(
                    page,
                    ".geetest_submit, [class*='geetest_submit']",
                    cancellationToken)
                .ConfigureAwait(false);
            if (submit is null)
            {
                // У nine-grid отдельной кнопки нет: GeeTest отправляет ответ после
                // последнего выбранного изображения.
                return capture.IsNineGrid
                    ? LoginClickCaptchaApplyResult.Applied
                    : LoginClickCaptchaApplyResult.Failed;
            }

            var submitBox = await submit.BoundingBoxAsync().ConfigureAwait(false);
            if (submitBox is null || submitBox.Width <= 0 || submitBox.Height <= 0)
            {
                return LoginClickCaptchaApplyResult.Failed;
            }

            var submitX = submitBox.X + submitBox.Width / 2;
            var submitY = submitBox.Y + submitBox.Height / 2;
            await page.Mouse.MoveAsync(submitX, submitY, new MoveOptions { Steps = Random.Shared.Next(4, 9) }).ConfigureAwait(false);
            await page.Mouse.ClickAsync(submitX, submitY, new ClickOptions { Delay = Random.Shared.Next(35, 85) }).ConfigureAwait(false);
            return LoginClickCaptchaApplyResult.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: ошибка применения ClickCaptcha логина — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_login_click_apply_failed" });
            return LoginClickCaptchaApplyResult.Failed;
        }
    }

    private static async Task<LoginClickCaptchaState> ReadLoginClickCaptchaStateAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await page
                .EvaluateExpressionAsync<string>(AvitoGeeTestSolveSupport.BuildReadLoginClickCaptchaStateScript())
                .ConfigureAwait(false);
            return AvitoGeeTestSolveSupport.ParseLoginClickCaptchaState(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось прочитать состояние ClickCaptcha логина — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_login_click_state_failed" });
            return LoginClickCaptchaState.Unknown;
        }
    }

    private static async Task<IElementHandle?> FindVisibleElementAsync(
        IPage page,
        string selector,
        CancellationToken cancellationToken)
    {
        var handles = await page.QuerySelectorAllAsync(selector).ConfigureAwait(false);
        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var box = await handle.BoundingBoxAsync().ConfigureAwait(false);
                if (box is { Width: > 0, Height: > 0 })
                {
                    return handle;
                }
            }
            catch (PuppeteerException)
            {
                // GeeTest заменяет DOM во время анимации; пробуем следующий совпавший узел.
            }
        }

        return null;
    }

    private static (decimal Width, decimal Height) ReadPngDimensions(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length < 24
                || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
                || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
            {
                return (0, 0);
            }

            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 ? (width, height) : (0, 0);
        }
        catch (FormatException)
        {
            return (0, 0);
        }
    }

    private sealed record LoginClickCaptchaCapture(
        string ImageBody,
        string HintImageBody,
        string? HintText,
        decimal ImageWidth,
        decimal ImageHeight,
        bool IsNineGrid,
        int RequiredClicks,
        string? Fingerprint);

    private sealed record PreparedLoginClickCaptchaCapture(
        bool IsNineGrid,
        string? HintText,
        int RequiredClicks);

    private enum LoginClickCaptchaApplyResult
    {
        Failed,
        Applied,
        ChallengeChanged
    }

    private static async Task DelayLoginOverlayRetryAsync(
        IPage page,
        int attempt,
        string reason,
        CancellationToken cancellationToken)
    {
        if (attempt >= MaxAttempts)
        {
            return;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Captcha: {reason} — обновляем виджет логина и повторяем ({attempt + 1}/{MaxAttempts}).",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "captcha_login_retry_scheduled",
                ["captcha.attempt"] = attempt,
                ["page.url"] = page.Url
            });
        try
        {
            await page
                .EvaluateExpressionAsync<bool>(AvitoGeeTestSolveSupport.BuildRefreshLoginGeeTestScript())
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: refresh виджета логина не удался — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_login_refresh_failed",
                    ["page.url"] = page.Url
                });
        }

        await Task.Delay(AvitoGeeTestSolveSupport.RetryDelayMs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> SafeGetUserAgentAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<string>("navigator.userAgent || ''").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось прочитать userAgent — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_login_useragent_failed" });
            return null;
        }
    }

    private async Task<bool> TrySolveHCaptchaAsync(
        IPage page,
        string? html,
        string? pageUrl,
        string apiKey,
        AvitoCaptchaActivation activation,
        GeeTestV4TaskOptions taskOptions,
        CancellationToken cancellationToken)
    {
        var websiteUrl = string.IsNullOrWhiteSpace(pageUrl) ? page.Url : pageUrl;
        if (string.IsNullOrWhiteSpace(websiteUrl))
        {
            websiteUrl = "https://www.avito.ru/";
        }

        var websiteKey = activation.SiteKey ?? AvitoGeeTestSolveSupport.ExtractHCaptchaSiteKey(html);
        if (string.IsNullOrWhiteSpace(websiteKey))
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: Avito выбрал hCaptcha, но сайт не отдал sitekey — решение не отправлено в RuCaptcha.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_hcaptcha_sitekey_missing",
                    ["page.url"] = websiteUrl,
                    ["captcha.serverKind"] = activation.ServerKind,
                    ["captcha.proxyMode"] = taskOptions.UsesSuppliedProxy ? "profile" : "proxyless"
                });
            return false;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: RuCaptcha hCaptcha, попытка {attempt}/{MaxAttempts}.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_hcaptcha_solve_start",
                    ["page.url"] = websiteUrl,
                    ["captcha.attempt"] = attempt,
                    ["captcha.proxyMode"] = taskOptions.UsesSuppliedProxy ? "profile" : "proxyless",
                    ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(taskOptions.UserAgent),
                    ["captcha.concurrentLimit"] = AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves
                });

            HCaptchaSolution solution;
            var requestId = await CreateProviderRequestAsync(page, "hcaptcha", attempt, MaxAttempts, websiteUrl, ct: cancellationToken).ConfigureAwait(false);
            try
            {
                solution = await ruCaptcha
                    .SolveHCaptchaAsync(apiKey, websiteUrl, websiteKey, taskOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (requestId is Guid failedRequestId)
                    await requestReporter!.MarkProviderFailedAsync(failedRequestId, ex.Message.Contains("NO_SLOT", StringComparison.OrdinalIgnoreCase) ? "no_slot" : "error", ex.GetType().Name, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: RuCaptcha не решила hCaptcha — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_hcaptcha_solve_api_failed",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt,
                        ["captcha.proxyMode"] = taskOptions.UsesSuppliedProxy ? "profile" : "proxyless"
                    });
                await DelayBeforeRetryAsync(page, attempt, "RuCaptcha не решила hCaptcha", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (requestId is Guid acceptedRequestId)
                await requestReporter!.MarkProviderAcceptedAsync(acceptedRequestId, solution.ProviderTask?.Id ?? string.Empty, cancellationToken).ConfigureAwait(false);

            var verifyRaw = await EvaluateHCaptchaVerifyAsync(page, solution, cancellationToken).ConfigureAwait(false);
            if (!RuCaptchaResponseParser.IsVerifyAccepted(verifyRaw))
            {
                var verify = AvitoGeeTestSolveSupport.ParseVerifyResult(verifyRaw);
                _ = GlobalLogger.Instance.LogAsync(
                    FormatTokenRejectedMessage("hCaptcha", verify, taskOptions),
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_hcaptcha_verify_rejected",
                        ["page.url"] = websiteUrl,
                        ["captcha.attempt"] = attempt,
                        ["captcha.verify.status"] = verify.StatusCode,
                        ["captcha.verify.verified"] = verify.Verified,
                        ["captcha.verify"] = verify.Summary,
                        ["captcha.proxyMode"] = taskOptions.UsesSuppliedProxy ? "profile" : "proxyless",
                        ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(taskOptions.UserAgent)
                    });
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: false, "hCaptcha", cancellationToken)
                    .ConfigureAwait(false);
                if (requestId is Guid rejectedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(rejectedRequestId, "rejected", cancellationToken).ConfigureAwait(false);
                await DelayBeforeRetryAsync(page, attempt, "токен hCaptcha отклонён", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var leaveResult = await LeaveCaptchaPageAsync(page, cancellationToken, afterAcceptedVerify: true)
                .ConfigureAwait(false);
            var after = leaveResult.Html;
            if (LeftCaptcha(leaveResult))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: hCaptcha пройдена через RuCaptcha.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_hcaptcha_solved",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt
                    });
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: true, "hCaptcha", cancellationToken)
                    .ConfigureAwait(false);
                if (requestId is Guid targetAcceptedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(targetAcceptedRequestId, "accepted", cancellationToken).ConfigureAwait(false);
                AvitoCaptchaTaskContext.NoteSolved();
                return true;
            }

            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: hCaptcha принята, но Avito не вышел с экрана капчи после восстановления страницы.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_hcaptcha_post_verify_stuck",
                    ["page.url"] = page.Url,
                    ["captcha.recoveryAttempts"] = leaveResult.Attempts,
                    ["captcha.redirectPending"] = AvitoCaptchaRedirectRecovery.RequiresRecovery(after),
                    ["captcha.proxyMode"] = taskOptions.UsesSuppliedProxy ? "profile" : "proxyless"
                });
            await DelayBeforeRetryAsync(page, attempt, "страница не ушла после hCaptcha", cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TrySolveInternalCaptchaAsync(
        IPage page,
        string apiKey,
        AvitoCaptchaActivation activation,
        GeeTestV4TaskOptions taskOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activation.ImageData))
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: Avito выбрал внутреннюю картинку, но изображение не удалось получить из страницы.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_internal_image_missing",
                    ["page.url"] = page.Url,
                    ["captcha.serverKind"] = activation.ServerKind
                });
            return false;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: RuCaptcha внутренняя картинка, попытка {attempt}/{MaxAttempts}.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_internal_solve_start",
                    ["page.url"] = page.Url,
                    ["captcha.attempt"] = attempt,
                    ["captcha.concurrentLimit"] = AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves
                });

            ImageCaptchaSolution solution;
            var requestId = await CreateProviderRequestAsync(page, "image_to_text", attempt, MaxAttempts, page.Url, ct: cancellationToken).ConfigureAwait(false);
            try
            {
                solution = await ruCaptcha
                    .SolveImageToTextAsync(apiKey, activation.ImageData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (requestId is Guid failedRequestId)
                    await requestReporter!.MarkProviderFailedAsync(failedRequestId, ex.Message.Contains("NO_SLOT", StringComparison.OrdinalIgnoreCase) ? "no_slot" : "error", ex.GetType().Name, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: RuCaptcha не решила внутреннюю картинку — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_internal_solve_api_failed",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt
                    });
                await DelayBeforeRetryAsync(page, attempt, "RuCaptcha не решила картинку", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (requestId is Guid acceptedRequestId)
                await requestReporter!.MarkProviderAcceptedAsync(acceptedRequestId, solution.ProviderTask?.Id ?? string.Empty, cancellationToken).ConfigureAwait(false);

            var verifyRaw = await EvaluateInternalCaptchaVerifyAsync(page, solution, cancellationToken).ConfigureAwait(false);
            if (!RuCaptchaResponseParser.IsVerifyAccepted(verifyRaw))
            {
                var verify = AvitoGeeTestSolveSupport.ParseVerifyResult(verifyRaw);
                _ = GlobalLogger.Instance.LogAsync(
                    FormatTokenRejectedMessage("внутренней картинки", verify, taskOptions),
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_internal_verify_rejected",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt,
                        ["captcha.verify.status"] = verify.StatusCode,
                        ["captcha.verify.verified"] = verify.Verified,
                        ["captcha.verify"] = verify.Summary
                    });
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: false, "внутренней картинки", cancellationToken)
                    .ConfigureAwait(false);
                if (requestId is Guid rejectedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(rejectedRequestId, "rejected", cancellationToken).ConfigureAwait(false);
                await DelayBeforeRetryAsync(page, attempt, "текст картинки отклонён", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var leaveResult = await LeaveCaptchaPageAsync(page, cancellationToken, afterAcceptedVerify: true)
                .ConfigureAwait(false);
            var after = leaveResult.Html;
            if (LeftCaptcha(leaveResult))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    "Captcha: внутренняя картинка пройдена через RuCaptcha.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_internal_solved",
                        ["page.url"] = page.Url,
                        ["captcha.attempt"] = attempt
                    });
                await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: true, "внутренней картинки", cancellationToken)
                    .ConfigureAwait(false);
                if (requestId is Guid targetAcceptedRequestId)
                    await requestReporter!.MarkTargetOutcomeAsync(targetAcceptedRequestId, "accepted", cancellationToken).ConfigureAwait(false);
                AvitoCaptchaTaskContext.NoteSolved();
                return true;
            }

            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: внутренняя картинка принята, но Avito не вышел с экрана капчи после восстановления страницы.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_internal_post_verify_stuck",
                    ["page.url"] = page.Url,
                    ["captcha.recoveryAttempts"] = leaveResult.Attempts,
                    ["captcha.redirectPending"] = AvitoCaptchaRedirectRecovery.RequiresRecovery(after)
                });
            await DelayBeforeRetryAsync(page, attempt, "страница не ушла после картинки", cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task ReportSolutionAsync(
        string apiKey,
        RuCaptchaTask? task,
        bool isCorrect,
        string captchaKind,
        CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await ruCaptcha.ReportAsync(apiKey, task, isCorrect, cancellationToken).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: RuCaptcha получила report {(isCorrect ? "correct" : "incorrect")} для {captchaKind}.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_provider_report_sent",
                    ["captcha.kind"] = captchaKind,
                    ["captcha.report"] = isCorrect ? "correct" : "incorrect",
                    ["captcha.taskId"] = task.Id,
                    ["captcha.apiVersion"] = task.ApiVersion.ToString()
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось отправить report {(isCorrect ? "correct" : "incorrect")} для {captchaKind} — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_provider_report_failed",
                    ["captcha.kind"] = captchaKind,
                    ["captcha.report"] = isCorrect ? "correct" : "incorrect",
                    ["captcha.taskId"] = task.Id,
                    ["captcha.apiVersion"] = task.ApiVersion.ToString()
                });
        }
    }

    private static string FormatTokenRejectedMessage(
        string captchaKind,
        AvitoVerifyResult verify,
        GeeTestV4TaskOptions taskOptions)
    {
        var status = verify.StatusCode?.ToString() ?? "нет HTTP-статуса";
        var reason = string.IsNullOrWhiteSpace(verify.Summary) ? "ответ без причины" : verify.Summary;
        var proxyMode = taskOptions.UsesSuppliedProxy ? "профильный прокси" : "proxyless";
        return $"Captcha: Avito не принял токен {captchaKind} (HTTP {status}; {proxyMode}; {reason}).";
    }

    private async Task<WorkerMonitoringConfig?> ResolveConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            return config;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось прочитать ключ RuCaptcha — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_api_key_read_failed" });
            return null;
        }
    }

    private static async Task<string?> EvaluateVerifyAsync(
        IPage page,
        GeeTestV4Solution solution,
        CancellationToken cancellationToken)
    {
        try
        {
            return await page
                .EvaluateExpressionAsync<string>(AvitoGeeTestSolveSupport.BuildVerifyScript(solution))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: ошибка POST firewallCaptcha/verify — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_verify_eval_failed" });
            return null;
        }
    }

    private static async Task<string?> EvaluateHCaptchaVerifyAsync(
        IPage page,
        HCaptchaSolution solution,
        CancellationToken cancellationToken)
    {
        try
        {
            return await page
                .EvaluateExpressionAsync<string>(AvitoGeeTestSolveSupport.BuildHCaptchaVerifyScript(solution))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: ошибка POST firewallCaptcha/verify для hCaptcha — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_hcaptcha_verify_eval_failed" });
            return null;
        }
    }

    private static async Task<string?> EvaluateInternalCaptchaVerifyAsync(
        IPage page,
        ImageCaptchaSolution solution,
        CancellationToken cancellationToken)
    {
        try
        {
            return await page
                .EvaluateExpressionAsync<string>(AvitoGeeTestSolveSupport.BuildInternalCaptchaVerifyScript(solution))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: ошибка POST firewallCaptcha/verify для внутренней картинки — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_internal_verify_eval_failed" });
            return null;
        }
    }

    private static async Task<AvitoCaptchaActivation> ActivateAndProbeCaptchaAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var latestHtml = attempt == 1
                    ? null
                    : await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
                if (attempt > 1 && AvitoCaptchaRedirectRecovery.RequiresRecovery(latestHtml))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: после навигации Avito уже на «перенаправление» — activate не повторяем.",
                        DeskLinkAuditLogLevel.Info,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_activation_skipped_redirect",
                            ["page.url"] = page.Url
                        });
                    return AvitoCaptchaActivation.Unknown;
                }

                var raw = await page
                    .EvaluateExpressionAsync<string>(AvitoGeeTestSolveSupport.BuildActivateAndProbeScript())
                    .ConfigureAwait(false);
                var activation = AvitoGeeTestSolveSupport.ParseActivation(raw);
                _ = GlobalLogger.Instance.LogAsync(
                    activation.IsGeeTest
                        ? "Captcha: Avito подтвердил GeeTest v4."
                        : activation.IsHCaptcha
                            ? "Captcha: Avito подтвердил hCaptcha."
                        : $"Captcha: Avito выбрал проверку «{activation.Kind.ToLogValue()}».",
                    activation.IsGeeTest || activation.IsHCaptcha ? DeskLinkAuditLogLevel.Info : DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_activation_probed",
                        ["page.url"] = page.Url,
                        ["captcha.kind"] = activation.Kind.ToLogValue(),
                        ["captcha.serverKind"] = activation.ServerKind,
                        ["captcha.hcaptchaSiteKeyPresent"] = !string.IsNullOrWhiteSpace(activation.SiteKey),
                        ["captcha.continueClicked"] = activation.ContinueClicked,
                        ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(activation.UserAgent)
                    });
                return activation;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt < 2 && IsDestroyedExecutionContext(ex))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: activate прерван навигацией, повторяем после паузы.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_activation_retry_after_navigation",
                            ["page.url"] = page.Url
                        });
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: не удалось определить тип проверки Avito — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?> { ["step"] = "captcha_activation_probe_failed" });
                break;
            }
        }

        return AvitoCaptchaActivation.Unknown;
    }

    private static bool IsDestroyedExecutionContext(Exception ex) =>
        ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase);

    private static SemaphoreSlim GetPageGate(IPage page) =>
        PageGates.GetValue(page, static _ => new SemaphoreSlim(1, 1));

    private static bool LeftCaptcha(AvitoCaptchaLeaveResult leave) =>
        leave.Recovered
        && !string.IsNullOrWhiteSpace(leave.Html)
        && (!AvitoCaptchaDetector.IsCaptchaHtml(leave.Html)
            || AvitoCaptchaDetector.ShowsLoginForm(leave.Html));

    private static async Task DelayBeforeRetryAsync(
        IPage page,
        int attempt,
        string reason,
        CancellationToken cancellationToken)
    {
        if (attempt >= MaxAttempts)
        {
            return;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Captcha: {reason} — обновляем страницу и повторяем ({attempt + 1}/{MaxAttempts}).",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "captcha_retry_scheduled",
                ["captcha.attempt"] = attempt,
                ["page.url"] = page.Url
            });
        await Task.Delay(AvitoGeeTestSolveSupport.RetryDelayMs, cancellationToken).ConfigureAwait(false);
        try
        {
            await page.ReloadAsync(PostVerifyNavigationTimeoutMs).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: reload перед повтором не завершился — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_retry_reload_failed",
                    ["page.url"] = page.Url
                });
        }

        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AvitoCaptchaLeaveResult> LeaveCaptchaPageAsync(
        IPage page,
        CancellationToken cancellationToken,
        bool afterAcceptedVerify = false)
    {
        var latestHtml = afterAcceptedVerify
            ? await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false)
            : await WaitForRedirectOverlayOrLeaveAsync(page, cancellationToken).ConfigureAwait(false);
        if (!AvitoCaptchaDetector.IsCaptchaHtml(latestHtml)
            || AvitoCaptchaDetector.ShowsLoginForm(latestHtml))
        {
            return new AvitoCaptchaLeaveResult(true, latestHtml, 0);
        }

        if (!afterAcceptedVerify && !AvitoCaptchaRedirectRecovery.RequiresRecovery(latestHtml))
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: экран «Проверка пройдена, перенаправление» не появился — живую капчу не обновляем.",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_skip_reload_live_challenge",
                    ["page.url"] = page.Url
                });
            return new AvitoCaptchaLeaveResult(false, latestHtml, 0);
        }

        if (afterAcceptedVerify)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: Avito принял токен — уходим со страницы как штатный скрипт (Reload).",
                DeskLinkAuditLogLevel.Info,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_leave_after_accepted_verify",
                    ["page.url"] = page.Url
                });
        }

        var currentTarget = AvitoCaptchaRedirectRecovery.GetCurrentPageTarget(page.Url);
        var attempts = 0;

        for (var attempt = 1; ; attempt++)
        {
            var action = AvitoCaptchaRedirectRecovery.GetAction(attempt);
            if (action == AvitoCaptchaRecoveryAction.None)
            {
                break;
            }

            attempts = attempt;
            try
            {
                switch (action)
                {
                    case AvitoCaptchaRecoveryAction.Reload:
                        await page.ReloadAsync(PostVerifyNavigationTimeoutMs).ConfigureAwait(false);
                        break;
                    case AvitoCaptchaRecoveryAction.NavigateCurrentPage:
                        await NavigateAsync(page, currentTarget).ConfigureAwait(false);
                        break;
                    case AvitoCaptchaRecoveryAction.NavigateProfileItems:
                        await NavigateAsync(page, AvitoCaptchaRedirectRecovery.ProfileItemsUrl).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Captcha: восстановление после verify ({action}) не завершило переход — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "captcha_post_verify_recovery_failed",
                        ["captcha.recoveryAction"] = action.ToString(),
                        ["captcha.recoveryAttempt"] = attempt,
                        ["page.url"] = page.Url
                    });
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            latestHtml = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(latestHtml)
                && (!AvitoCaptchaDetector.IsCaptchaHtml(latestHtml)
                    || AvitoCaptchaDetector.ShowsLoginForm(latestHtml)))
            {
                return new AvitoCaptchaLeaveResult(true, latestHtml, attempts);
            }

            var redirectPending = AvitoCaptchaRedirectRecovery.RequiresRecovery(latestHtml);
            _ = GlobalLogger.Instance.LogAsync(
                redirectPending
                    ? $"Captcha: Avito всё ещё показывает «перенаправление» после {action}; пробуем следующий шаг восстановления."
                    : $"Captcha: после {action} зелёный экран ушёл, но капча ещё на странице — дальше не перезагружаем.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_post_verify_recovery_probe",
                    ["captcha.recoveryAction"] = action.ToString(),
                    ["captcha.recoveryAttempt"] = attempt,
                    ["captcha.redirectPending"] = redirectPending,
                    ["captcha.htmlReceived"] = !string.IsNullOrWhiteSpace(latestHtml),
                    ["page.url"] = page.Url
                });

            if (!redirectPending)
            {
                break;
            }
        }

        return new AvitoCaptchaLeaveResult(false, latestHtml, attempts);
    }

    private static async Task<string?> WaitForRedirectOverlayOrLeaveAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        string? html = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
        for (var probe = 0; probe < PostVerifyPaintPolls; probe++)
        {
            if (!AvitoCaptchaDetector.IsCaptchaHtml(html)
                || AvitoCaptchaRedirectRecovery.RequiresRecovery(html))
            {
                return html;
            }

            await Task.Delay(PostVerifyPaintPollMs, cancellationToken).ConfigureAwait(false);
            html = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
        }

        return html;
    }

    private static Task NavigateAsync(IPage page, string url) =>
        page.GoToAsync(url, new NavigationOptions
        {
            Timeout = PostVerifyNavigationTimeoutMs,
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
        });

    private static async Task<string?> SafeGetHtmlAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.GetContentAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private sealed record AvitoCaptchaLeaveResult(bool Recovered, string? Html, int Attempts);
}
