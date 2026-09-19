using System.Threading.Channels;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Avito.Session;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public sealed class CaptchaSessionHost(IAdsPowerApiClient adsPowerApiClient)
{
    private const int LiveFrameFirstFrameTimeoutMs = 5_000;
    private const int LiveFrameStartAttempts = 2;
    private const string DefaultAdsPowerApiBaseUrl = "http://local.adspower.net:50325";
    private const int CaptchaDetectTimeoutSeconds = 300;
    private const int CaptchaClearChecksRequired = 2;
    private const int CaptchaMissingChecksRequired = 4;
    private const int CaptchaProbeIntervalMs = 1_200;
    private const int LiveFrameRestartDelayMs = 300;

    public async Task<CaptchaSessionHostResult> RunAsync(
        CaptchaSessionHostRequest request,
        Func<CaptchaFramePayload, CancellationToken, Task> onFrame,
        ChannelReader<CaptchaInputPayload> inputs,
        Func<CaptchaSessionProgress, CancellationToken, Task> onProgress,
        CancellationToken cancellationToken = default)
    {
        IBrowser? browser = null;
        try
        {
            await LogAsync($"Captcha: старт сессии {request.SessionId}, профиль {request.AdsPowerProfileId}.", DeskLinkAuditLogLevel.Info)
                .ConfigureAwait(false);

            await onProgress(new CaptchaSessionProgress(CaptchaSessionStatuses.Opening, "Открытие браузера…"), cancellationToken)
                .ConfigureAwait(false);

            var options = new AdsPowerConnectionOptions(
                ResolveAdsPowerApiBaseUrl(request.AdsPowerApiBaseUrl),
                request.AdsPowerApiKey);

            var start = await adsPowerApiClient
                .StartBrowserAsync(options, request.AdsPowerProfileId, openUrl: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
            {
                return CaptchaSessionHostResult.Failed("AdsPower не вернул CDP endpoint.");
            }

            browser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserWSEndpoint = start.WebSocketDebuggerUrl,
                DefaultViewport = null
            }).ConfigureAwait(false);

            var page = await ResolveCaptchaPageAsync(browser, request, cancellationToken).ConfigureAwait(false);
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = request.ViewportWidth,
                Height = request.ViewportHeight
            }).ConfigureAwait(false);

            await LogAsync($"Captcha: страница открыта {page.Url}.", DeskLinkAuditLogLevel.Info).ConfigureAwait(false);

            await onProgress(new CaptchaSessionProgress(CaptchaSessionStatuses.Active, null), cancellationToken)
                .ConfigureAwait(false);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inputTask = PumpInputsAsync(page, request, inputs, linkedCts.Token);
            var visualTask = PumpLiveFramesAsync(page, request, onFrame, linkedCts.Token);
            var probeTask = ProbeCompletionAsync(page, request, onProgress, linkedCts);

            await Task.WhenAny(probeTask, inputTask, visualTask).ConfigureAwait(false);
            linkedCts.Cancel();

            try
            {
                await Task.WhenAll(inputTask, visualTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected after probe completion
            }

            if (probeTask.IsCompletedSuccessfully)
            {
                var probe = await probeTask.ConfigureAwait(false);
                if (probe.Success)
                {
                    await LogAsync(
                            $"Captcha: {probe.Message ?? "капча пройдена."}",
                            DeskLinkAuditLogLevel.Info)
                        .ConfigureAwait(false);
                    return CaptchaSessionHostResult.Completed(probe.Message);
                }

                return CaptchaSessionHostResult.Failed(probe.Message ?? "Капча не пройдена.");
            }

            return CaptchaSessionHostResult.Failed("Сессия капчи прервана.");
        }
        catch (Exception ex)
        {
            await LogAsync($"Captcha: ошибка — {ex.Message}", DeskLinkAuditLogLevel.Error).ConfigureAwait(false);
            return CaptchaSessionHostResult.Failed(ex.Message);
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    browser.Disconnect();
                }
                catch
                {
                    // ignore disconnect errors
                }
            }

            try
            {
                var options = new AdsPowerConnectionOptions(
                    ResolveAdsPowerApiBaseUrl(request.AdsPowerApiBaseUrl),
                    request.AdsPowerApiKey);
                await adsPowerApiClient
                    .StopBrowserAsync(options, request.AdsPowerProfileId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort browser stop
            }
        }
    }

    private static string ResolveAdsPowerApiBaseUrl(string? configuredUrl) =>
        string.IsNullOrWhiteSpace(configuredUrl) ? DefaultAdsPowerApiBaseUrl : configuredUrl.Trim().TrimEnd('/');

    private static async Task<IPage> ResolveCaptchaPageAsync(
        IBrowser browser,
        CaptchaSessionHostRequest request,
        CancellationToken cancellationToken)
    {
        var pages = await browser.PagesAsync().ConfigureAwait(false);
        foreach (var candidate in pages)
        {
            if (string.IsNullOrWhiteSpace(candidate.Url)
                || !candidate.Url.Contains("avito.ru", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var obstacle = await AvitoPageObstacleProbe.ProbeAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (obstacle.IsSolvableCaptcha)
            {
                await LogAsync(
                    $"Captcha: используем открытую вкладку с капчей ({candidate.Url}).",
                    DeskLinkAuditLogLevel.Info).ConfigureAwait(false);
                return candidate;
            }
        }

        var page = pages.FirstOrDefault() ?? await browser.NewPageAsync().ConfigureAwait(false);
        var currentObstacle = await AvitoPageObstacleProbe.ProbeAsync(page, cancellationToken)
            .ConfigureAwait(false);
        if (!currentObstacle.IsSolvableCaptcha)
        {
            await page.GoToAsync(request.PageUrl, new NavigationOptions
            {
                Timeout = 90_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }

        return page;
    }

    private static async Task PumpLiveFramesAsync(
        IPage page,
        CaptchaSessionHostRequest request,
        Func<CaptchaFramePayload, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        var attemptsWithoutFrame = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var liveStream = await TryPumpLiveFramesAsync(page, request, onFrame, cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || liveStream.CompletedNormally)
            {
                return;
            }

            if (!liveStream.ReceivedFrame)
            {
                attemptsWithoutFrame++;
                if (attemptsWithoutFrame >= LiveFrameStartAttempts)
                {
                    throw new InvalidOperationException(
                        "Не удалось получить live-кадр из браузера. Сессия остановлена, чтобы не оставлять оператора в ожидании.");
                }

                await LogAsync(
                        $"Captcha: live screencast не дал кадр, повторный запуск ({attemptsWithoutFrame + 1}/{LiveFrameStartAttempts}).",
                        DeskLinkAuditLogLevel.Warning)
                    .ConfigureAwait(false);
                continue;
            }

            attemptsWithoutFrame = 0;
            await LogAsync(
                    "Captcha: live screencast остановился после кадра, перезапускаем поток.",
                    DeskLinkAuditLogLevel.Warning)
                .ConfigureAwait(false);
            await Task.Delay(LiveFrameRestartDelayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<LiveFramePumpResult> TryPumpLiveFramesAsync(
        IPage page,
        CaptchaSessionHostRequest request,
        Func<CaptchaFramePayload, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var relay = await CaptchaScreencastRelay.StartAsync(
                    page,
                    request.SessionId,
                    request.ViewportWidth,
                    request.ViewportHeight,
                    onFrame,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!await relay.WaitForFirstFrameAsync(
                    TimeSpan.FromMilliseconds(LiveFrameFirstFrameTimeoutMs),
                    cancellationToken).ConfigureAwait(false))
            {
                return new LiveFramePumpResult(false, false);
            }

            return new LiveFramePumpResult(
                true,
                await relay.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new LiveFramePumpResult(false, true);
        }
        catch (Exception ex)
        {
            await LogAsync(
                $"Captcha: live screencast не стартовал — {ex.Message}",
                DeskLinkAuditLogLevel.Warning).ConfigureAwait(false);
            return new LiveFramePumpResult(false, false);
        }
    }

    private readonly record struct LiveFramePumpResult(bool ReceivedFrame, bool CompletedNormally);

    private static async Task PumpInputsAsync(
        IPage page,
        CaptchaSessionHostRequest request,
        ChannelReader<CaptchaInputPayload> inputs,
        CancellationToken cancellationToken)
    {
        CaptchaInputPayload? buffered = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var input = buffered;
            buffered = null;

            if (input is null)
            {
                if (!await inputs.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                if (!inputs.TryRead(out input))
                {
                    continue;
                }
            }

            if (IsMouseMoveInput(input))
            {
                while (inputs.TryRead(out var next))
                {
                    if (IsMouseMoveInput(next))
                    {
                        input = next;
                        continue;
                    }

                    buffered = next;
                    break;
                }
            }

            if (CaptchaKeyboardRelay.IsKeyboardEvent(input.EventType))
            {
                await CaptchaKeyboardRelay.DispatchAsync(page, input, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var (x, y) = CaptchaCoordinateMapper.MapToRemote(
                input.X,
                input.Y,
                input.PanelWidth,
                input.PanelHeight,
                request.ViewportWidth,
                request.ViewportHeight);

            await CaptchaMouseRelay.DispatchAsync(page, input.EventType, x, y, input.Button, input.Buttons, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsMouseMoveInput(CaptchaInputPayload input) =>
        string.Equals(input.EventType, "mousemove", StringComparison.OrdinalIgnoreCase);

    private static async Task<CaptchaProbeResult> ProbeCompletionAsync(
        IPage page,
        CaptchaSessionHostRequest request,
        Func<CaptchaSessionProgress, CancellationToken, Task> onProgress,
        CancellationTokenSource linkedCts)
    {
        var seenCaptcha = false;
        string? lastKind = null;
        var clearChecks = 0;
        var missingChecks = 0;
        var deadline = DateTime.UtcNow.AddSeconds(CaptchaDetectTimeoutSeconds);
        const string noCaptchaMessage = "Капча не обнаружена на странице. Ошибка закрыта.";

        while (!linkedCts.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var obstacle = await AvitoPageObstacleProbe.ProbeAsync(page, linkedCts.Token).ConfigureAwait(false);
            var kind = obstacle.Kind == AvitoPageObstacleKind.IpBlocked
                ? "firewall"
                : obstacle.IsSolvableCaptcha
                    ? obstacle.CaptchaKind ?? "captcha"
                    : null;

            if (kind is not null)
            {
                seenCaptcha = true;
                lastKind = kind;
                clearChecks = 0;
                missingChecks = 0;
            }
            else if (seenCaptcha)
            {
                clearChecks++;
                if (clearChecks >= CaptchaClearChecksRequired)
                {
                    await onProgress(
                        new CaptchaSessionProgress(CaptchaSessionStatuses.Completed, "Капча пройдена."),
                        linkedCts.Token).ConfigureAwait(false);
                    return CaptchaProbeResult.Completed("Капча пройдена.");
                }
            }
            else
            {
                missingChecks++;
                if (missingChecks >= CaptchaMissingChecksRequired)
                {
                    await onProgress(
                        new CaptchaSessionProgress(CaptchaSessionStatuses.Completed, noCaptchaMessage),
                        linkedCts.Token).ConfigureAwait(false);
                    return CaptchaProbeResult.Completed(noCaptchaMessage);
                }
            }

            await Task.Delay(CaptchaProbeIntervalMs, linkedCts.Token).ConfigureAwait(false);
        }

        if (!seenCaptcha)
        {
            return CaptchaProbeResult.Completed(noCaptchaMessage);
        }

        return CaptchaProbeResult.Failed($"Капча ({lastKind}) не пройдена за отведённое время.");
    }

    private static Task LogAsync(string message, DeskLinkAuditLogLevel level) =>
        GlobalLogger.Instance.LogAsync(
            message,
            level,
            errorKey: "captcha.host",
            memberName: nameof(CaptchaSessionHost),
            filePath: nameof(CaptchaSessionHost) + ".cs",
            properties: new Dictionary<string, object?> { ["captcha.worker"] = true });

    private sealed record CaptchaProbeResult(bool Success, string? Message)
    {
        public static CaptchaProbeResult Completed(string? message = null) => new(true, message);
        public static CaptchaProbeResult Failed(string message) => new(false, message);
    }
}

public sealed record CaptchaSessionHostRequest(
    Guid SessionId,
    string AdsPowerProfileId,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    string PageUrl,
    string? SubProfileId,
    int ViewportWidth,
    int ViewportHeight);

public sealed record CaptchaSnapshotPayload(
    Guid SessionId,
    string MhtmlGzipBase64,
    int ViewportWidth,
    int ViewportHeight,
    long TimestampMs);

public sealed record CaptchaFramePayload(
    Guid SessionId,
    string ImageBase64,
    int ViewportWidth,
    int ViewportHeight,
    long TimestampMs);

public sealed record CaptchaInputPayload(
    Guid SessionId,
    string EventType = "",
    double X = 0,
    double Y = 0,
    double PanelWidth = 0,
    double PanelHeight = 0,
    long TimestampMs = 0,
    int Button = 0,
    int Buttons = 0,
    string? Key = null,
    string? Code = null,
    bool AltKey = false,
    bool CtrlKey = false,
    bool ShiftKey = false,
    bool MetaKey = false,
    bool Repeat = false);

public sealed record CaptchaSessionProgress(string Status, string? Message);

public sealed record CaptchaSessionHostResult(bool Success, string? Message)
{
    public static CaptchaSessionHostResult Completed(string? message = null) => new(true, message);
    public static CaptchaSessionHostResult Failed(string message) => new(false, message);
}
