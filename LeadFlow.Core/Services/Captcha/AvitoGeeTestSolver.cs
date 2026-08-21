using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public sealed class AvitoGeeTestSolver(
    IRuCaptchaClient ruCaptcha,
    IWorkerConfigProvider configProvider) : IAvitoGeeTestSolver
{
    private const int MaxAttempts = 2;
    private static readonly SemaphoreSlim Gate = new(2, 2);

    public async Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        html ??= await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
        if (!AvitoCaptchaDetector.HasGeeTestWidget(html))
        {
            return false;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                        ["captcha.attempt"] = attempt
                    });

                GeeTestV4Solution solution;
                try
                {
                    solution = await ruCaptcha
                        .SolveGeeTestV4Async(apiKey, websiteUrl, captchaId, cancellationToken)
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
                            ["captcha.attempt"] = attempt
                        });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(solution.CaptchaId))
                {
                    solution = solution with { CaptchaId = captchaId };
                }

                var verifyRaw = await EvaluateVerifyAsync(page, solution, cancellationToken).ConfigureAwait(false);
                if (!RuCaptchaResponseParser.IsVerifyAccepted(verifyRaw))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "Captcha: Avito не принял токен GeeTest v4.",
                        DeskLinkAuditLogLevel.Warning,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "captcha_verify_rejected",
                            ["page.url"] = websiteUrl,
                            ["captcha.attempt"] = attempt
                        });
                    continue;
                }

                await ReloadAfterVerifyAsync(page, cancellationToken).ConfigureAwait(false);
                var after = await SafeGetHtmlAsync(page, cancellationToken).ConfigureAwait(false);
                if (!AvitoCaptchaDetector.IsCaptchaHtml(after))
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
                    return true;
                }

                html = after;
            }

            return false;
        }
        finally
        {
            Gate.Release();
        }
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

    private static async Task ReloadAfterVerifyAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.ReloadAsync(60_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: reload после verify — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?> { ["step"] = "captcha_reload_failed" });
        }

        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> SafeGetHtmlAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.GetContentAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
