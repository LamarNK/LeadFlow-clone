using System.Runtime.CompilerServices;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace LeadFlow.Core.Services.Captcha;

public sealed class AvitoGeeTestSolver(
    IRuCaptchaClient ruCaptcha,
    IWorkerConfigProvider configProvider) : IAvitoGeeTestSolver
{
    private const int MaxAttempts = AvitoGeeTestSolveSupport.MaxGeeTestAttempts;
    private const int PostVerifyNavigationTimeoutMs = 20_000;
    private const int PostVerifyPaintPolls = 6;
    private const int PostVerifyPaintPollMs = 400;
    private const string LoginClickCaptchaImageSelector = ".geetest_click .geetest_bg, [class*='geetest_click'] [class*='geetest_bg']";
    private const string LoginNineGridCaptchaImageSelector = ".geetest_nine, [class*='geetest_nine']";
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
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
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

            var activation = await ActivateAndProbeCaptchaAsync(page, cancellationToken).ConfigureAwait(false);

            var effectiveTaskOptions = (taskOptions ?? AvitoCaptchaTaskContext.Options ?? new GeeTestV4TaskOptions())
                .WithUserAgent(activation.UserAgent);

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

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captchaId = AvitoCaptchaDetector.ExtractGeeTestCaptchaId(html);
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
                        ["captcha.concurrentLimit"] = AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves
                    });

                GeeTestV4Solution solution;
                try
                {
                    solution = await ruCaptcha
                        .SolveGeeTestV4Async(apiKey, websiteUrl, captchaId, effectiveTaskOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
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

                if (string.IsNullOrWhiteSpace(solution.CaptchaId))
                {
                    solution = solution with { CaptchaId = captchaId };
                }

                var verifyRaw = await EvaluateVerifyAsync(page, solution, cancellationToken).ConfigureAwait(false);
                if (!RuCaptchaResponseParser.IsVerifyAccepted(verifyRaw))
                {
                    var verify = AvitoGeeTestSolveSupport.ParseVerifyResult(verifyRaw);
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
                            ["captcha.userAgentPresent"] = !string.IsNullOrWhiteSpace(effectiveTaskOptions.UserAgent)
                        });
                    await ReportSolutionAsync(apiKey, solution.ProviderTask, isCorrect: false, "GeeTest v4", cancellationToken)
                        .ConfigureAwait(false);
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
            try
            {
                solution = await ruCaptcha
                    .SolveClickCaptchaAsync(
                        apiKey,
                        capture.ImageBody,
                        capture.HintImageBody,
                        capture.HintText,
                        "ru",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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

            var applied = await ApplyLoginClickCaptchaAsync(page, capture, solution, cancellationToken).ConfigureAwait(false);
            if (!applied)
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

            await Task.Delay(1_000, cancellationToken).ConfigureAwait(false);
            var afterHtml = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(afterHtml)
                && !AvitoGeeTestSolveSupport.IsLoginClickCaptchaOverlay(afterHtml))
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
                AvitoCaptchaTaskContext.NoteSolved();
                return true;
            }

            _ = GlobalLogger.Instance.LogAsync(
                "Captcha: ClickCaptcha приняла клики, но оверлей остался на экране.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_login_click_overlay_visible",
                    ["page.url"] = page.Url,
                    ["captcha.attempt"] = attempt
                });
            await DelayLoginOverlayRetryAsync(page, attempt, "ClickCaptcha не приняла ответ", cancellationToken)
                .ConfigureAwait(false);
            html = null;
        }

        return false;
    }

    private static async Task<LoginClickCaptchaCapture?> CaptureLoginClickCaptchaAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await page.QuerySelectorAsync(LoginClickCaptchaImageSelector).ConfigureAwait(false);
            var isNineGrid = image is null;
            image ??= await page.QuerySelectorAsync(LoginNineGridCaptchaImageSelector).ConfigureAwait(false);
            var hint = await page.QuerySelectorAsync(".geetest_ques_tips, [class*='geetest_ques_tips']")
                .ConfigureAwait(false);
            if (image is null || hint is null)
            {
                return null;
            }

            var imageBody = await image.ScreenshotBase64Async().ConfigureAwait(false);
            var hintImageBody = await hint.ScreenshotBase64Async().ConfigureAwait(false);
            var (width, height) = ReadPngDimensions(imageBody);
            var hintText = await page.EvaluateExpressionAsync<string>(
                    "(() => document.querySelector('.geetest_text_tips, [class*=\"geetest_text_tips\"]')?.textContent || '')()")
                .ConfigureAwait(false);
            return width > 0 && height > 0 && !string.IsNullOrWhiteSpace(imageBody) && !string.IsNullOrWhiteSpace(hintImageBody)
                ? new LoginClickCaptchaCapture(imageBody, hintImageBody, hintText, width, height, isNineGrid)
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
    }

    private static async Task<bool> ApplyLoginClickCaptchaAsync(
        IPage page,
        LoginClickCaptchaCapture capture,
        ClickCaptchaSolution solution,
        CancellationToken cancellationToken)
    {
        try
        {
            if (solution.Points.Count is < 1 or > 8)
            {
                return false;
            }

            var imageSelector = capture.IsNineGrid
                ? LoginNineGridCaptchaImageSelector
                : LoginClickCaptchaImageSelector;
            var image = await page.QuerySelectorAsync(imageSelector).ConfigureAwait(false);
            var box = image is null ? null : await image.BoundingBoxAsync().ConfigureAwait(false);
            if (box is null || box.Width <= 0 || box.Height <= 0)
            {
                return false;
            }

            foreach (var point in solution.Points)
            {
                if (point.X < 0 || point.Y < 0 || point.X > capture.ImageWidth || point.Y > capture.ImageHeight)
                {
                    return false;
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

            var submit = await page.QuerySelectorAsync(".geetest_submit, [class*='geetest_submit']")
                .ConfigureAwait(false);
            if (submit is null)
            {
                // У nine-grid отдельной кнопки нет: GeeTest отправляет ответ после
                // последнего выбранного изображения.
                return capture.IsNineGrid;
            }

            return await AvitoHumanPointer
                .TryClickSelectorAsync(page, ".geetest_submit, [class*='geetest_submit']", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: ошибка применения ClickCaptcha логина — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_login_click_apply_failed" });
            return false;
        }
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
        bool IsNineGrid);

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
            try
            {
                solution = await ruCaptcha
                    .SolveHCaptchaAsync(apiKey, websiteUrl, websiteKey, taskOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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
            try
            {
                solution = await ruCaptcha
                    .SolveImageToTextAsync(apiKey, activation.ImageData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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

    private async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(config.RuCaptchaApiKey) ? null : config.RuCaptchaApiKey.Trim();
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
