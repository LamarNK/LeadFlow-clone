using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Ожидание минимально достаточного DOM-контракта страницы откликов.
/// Полная загрузка документа и стабилизация списка не нужны: дальнейшую прокрутку и сбор
/// выполняет <see cref="AvitoCandidatesListPreparer"/>.
/// </summary>
public static class AvitoCandidatesPageWaiter
{
    public static async Task WaitForCandidatesOrThrowFirewallAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot,
        string? pageUrl,
        CancellationToken cancellationToken,
        IPage? pageForRecovery = null)
    {
        var loginRecoveryAttempted = false;
        while (true)
        {
            var result = await AvitoInteractionWaiter.WaitAsync(
                    executeScript,
                    AvitoInteractionWaiter.Target.Candidates,
                    new AvitoInteractionWaiter.Options(
                        MonitoringTiming.CandidatesPageMaxWaitMs,
                        MonitoringTiming.CandidatesPagePollMs,
                        Operation: "candidates"),
                    pageUrl,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Status == AvitoInteractionWaiter.Status.Ready)
            {
                return;
            }

            if (result.Status == AvitoInteractionWaiter.Status.LoginRequired)
            {
                if (loginRecoveryAttempted)
                {
                    throw new AvitoLoginRequiredException(result.Url, result.PageState?.Title);
                }

                // This method preserves the former recovery policy: if the recovery succeeds,
                // run the action-specific wait again against the restored session.
                loginRecoveryAttempted = true;
                await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken, pageForRecovery)
                    .ConfigureAwait(false);
                continue;
            }

            if (result.Status == AvitoInteractionWaiter.Status.CaptchaOrFirewall)
            {
                await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.Status is AvitoInteractionWaiter.Status.ProbeFailed or AvitoInteractionWaiter.Status.ContextMismatch)
            {
                throw new TimeoutException(
                    $"Не удалось проверить готовность откликов Авито: {result.Reason}.");
            }

            _ = GlobalLogger.Instance.LogAsync(
                "Candidates data did not appear before the action timeout.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(AvitoCandidatesPageWaiter),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "candidates_wait_timeout",
                    ["avito.operation"] = "candidates",
                    ["ready.reason"] = result.Reason,
                    ["ready.waitMs"] = result.WaitMs,
                    ["readyState"] = result.ReadyState,
                    ["hasLoader"] = result.HasLoader,
                    ["candidates.itemCount"] = result.ItemCount,
                    ["candidates.statusCount"] = result.StatusCount,
                    ["page.url"] = result.Url
                });

            throw new TimeoutException("Таймаут ожидания данных откликов Авито.");
        }
    }
}
