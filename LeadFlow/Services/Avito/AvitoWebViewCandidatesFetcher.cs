using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public sealed class AvitoWebViewCandidatesFetcher(
    IBrowserSessionService browserSessionService,
    IBackgroundWebViewHostFactory backgroundWebViewHostFactory,
    IWebPageAutomationService automationService,
    AvitoResponseSource avitoResponseSource) : IAvitoWebViewCandidatesFetcher
{
    public async Task<IReadOnlyList<CandidateResponse>> FetchNewResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var session = await browserSessionService.CreateSessionAsync(account, cancellationToken);
        await using var host = await backgroundWebViewHostFactory.CreateAsync(cancellationToken);
        await host.AttachAsync(session, cancellationToken);

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var pause = attempt == 2 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(12);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Candidates page fetch retry {attempt}/{maxAttempts} for {account.DisplayName}.",
                    DeskLinkAuditLogLevel.Info);
                await Task.Delay(pause, cancellationToken);
            }

            try
            {
                var executeForBaseline = (string script, CancellationToken ct) =>
                    automationService.ExecuteScriptAsync(session, script, ct);

                string? staleListSignature = null;
                if (AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(session.CurrentUrl))
                {
                    staleListSignature = await AvitoCandidatesPageWaiter
                        .TryCaptureListSignatureAsync(executeForBaseline, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!await TryNavigateToCandidatesPageAsync(session, cancellationToken, staleListSignature))
                {
                    if (attempt == maxAttempts)
                    {
                        account.LastErrorMessage = "Не удалось открыть страницу откликов Avito";
                        return [];
                    }

                    continue;
                }
                account.LastAuthCheckAt = DateTime.UtcNow;

                var execute = (string script, CancellationToken ct) =>
                    automationService.ExecuteScriptAsync(session, script, ct);
                await AvitoCandidatesListPreparer.PrepareAsync(
                    execute,
                    account.DisplayName,
                    cancellationToken,
                    ct => FetchPageHtmlSnapshotAsync(execute, ct),
                    AvitoResponseSource.CandidatesPageUrl,
                    async (phones, ct) => (IReadOnlySet<string>)await avitoResponseSource.ResolveExistingPhonesAsync(
                        account.Id,
                        settings.DuplicateScope,
                        phones,
                        ct),
                    async (fingerprints, ct) => (IReadOnlySet<string>)await avitoResponseSource.ResolveExistingCardFingerprintsAsync(
                        account.Id,
                        settings.DuplicateScope,
                        fingerprints,
                        ct)).ConfigureAwait(false);

                var raw = await automationService.ExecuteScriptAsync(
                    session,
                    AvitoCandidatesPageScripts.BuildExtractionScript(),
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (attempt == maxAttempts)
                    {
                        account.LastErrorMessage = "Пустой ответ скрипта со страницы кандидатов";
                        return [];
                    }

                    continue;
                }

                return await avitoResponseSource
                    .ParseCandidatesFromRawAsync(account, settings, raw, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Некорректный JSON страницы кандидатов";
                    return [];
                }
            }
            catch (TimeoutException)
            {
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Таймаут загрузки страницы кандидатов";
                    return [];
                }
            }
        }

        account.LastErrorMessage = "Не удалось получить отклики после нескольких попыток";
        return [];
    }

    private async Task<bool> TryNavigateToCandidatesPageAsync(
        BrowserAccountSession session,
        CancellationToken cancellationToken,
        string? baselineListSignature = null)
    {
        var execute = (string script, CancellationToken ct) =>
            automationService.ExecuteScriptAsync(session, script, ct);

        foreach (var targetUrl in AvitoCandidatesPageUrls.NavigationOrder)
        {
            try
            {
                await automationService.NavigateAsync(session, targetUrl, cancellationToken);
                await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
                    execute,
                    ct => FetchPageHtmlSnapshotAsync(execute, ct),
                    targetUrl,
                    cancellationToken,
                    baselineListSignature).ConfigureAwait(false);

                var probeRaw = await automationService.ExecuteScriptAsync(
                    session,
                    AvitoPageStateScripts.BuildProbeScript(),
                    cancellationToken);
                var state = AvitoPageStateProbe.TryParse(probeRaw);
                if (state?.IsOnCandidates == true)
                {
                    return true;
                }
            }
            catch (TimeoutException)
            {
                // Пробуем альтернативный URL (CRM job-responses).
            }
        }

        return false;
    }

    private static async Task<string?> FetchPageHtmlSnapshotAsync(
        Func<string, CancellationToken, Task<string>> execute,
        CancellationToken cancellationToken)
    {
        var raw = await execute(
            "(() => (document.documentElement?.outerHTML ?? '').slice(0, 120000))()",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch
        {
            return raw.Trim().Trim('"');
        }
    }
}