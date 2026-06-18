using System.Text.Json;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services.AdsPower;
using LeadFlow.Services.Browser;
using LeadFlow.Services;
using PuppeteerSharp;

namespace LeadFlow.Services.Avito;

public sealed class AvitoResponseSource(
    AppRepository repository,
    IBrowserSessionService browserSessionService,
    IBackgroundWebViewHostFactory backgroundWebViewHostFactory,
    IWebPageAutomationService automationService,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService,
    IPhoneNormalizer phoneNormalizer) : IAvitoResponseSource
{
    public const string CandidatesPageUrl = "https://www.avito.ru/profile/candidates";

    public Task<IReadOnlyList<CandidateResponse>> ParseCandidatesFromRawAsync(
        AvitoAccount account,
        AppSettings settings,
        string rawExtractionJson,
        CancellationToken cancellationToken) =>
        ParseResponsesFromRawExtractionAsync(account, settings, rawExtractionJson, cancellationToken);

    public async Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        if (account.ProfileProvider == AvitoProfileProvider.AdsPower && !settings.DemoModeEnabled)
        {
            if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId) || string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl))
            {
                account.LastErrorMessage = "Аккаунт AdsPower: не заданы user_id или base URL Local API.";
                return [];
            }

            var options = new AdsPowerConnectionOptions(
                account.AdsPowerApiBaseUrl,
                string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);
            var messengerHints = new CandidatesMessengerEnrichmentHints(account.Id, settings.DuplicateScope);

            const int adsPowerMaxAttempts = 3;
            for (var attempt = 1; attempt <= adsPowerMaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    var pause = attempt == 2 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(12);
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower candidates fetch retry {attempt}/{adsPowerMaxAttempts} for {account.DisplayName} after {pause.TotalSeconds:F0} s.",
                        DeskLinkAuditLogLevel.Info);
                    await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var rawAdsPower = await adsPowerAvitoAutomationService
                        .ExtractCandidatesJsonAsync(options, account.AdsPowerProfileId, cancellationToken, messengerHints)
                        .ConfigureAwait(false);
                    return await ParseResponsesFromRawExtractionAsync(account, settings, rawAdsPower, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AvitoCaptchaDetectedException)
                {
                    throw;
                }
                catch (TimeoutException ex)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower timeout waiting for candidates page for {account.DisplayName} (attempt {attempt}/{adsPowerMaxAttempts}): {ex.Message}",
                        DeskLinkAuditLogLevel.Warning);
                    if (attempt == adsPowerMaxAttempts)
                    {
                        account.LastErrorMessage = "Таймаут загрузки страницы кандидатов AdsPower после нескольких попыток";
                        throw;
                    }
                }
                catch (Exception ex) when (IsAdsPowerTransientCandidatesError(ex))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower transient error on candidates page for {account.DisplayName} (attempt {attempt}/{adsPowerMaxAttempts}): {ex.Message}",
                        DeskLinkAuditLogLevel.Warning);
                    if (attempt == adsPowerMaxAttempts)
                    {
                        account.LastErrorMessage = "Не удалось получить отклики AdsPower после нескольких попыток";
                        throw;
                    }
                }
            }

            account.LastErrorMessage = "Не удалось получить отклики со страницы кандидатов AdsPower после нескольких попыток";
            return [];
        }

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
                    $"Candidates page fetch retry {attempt}/{maxAttempts} for {account.DisplayName} after {pause.TotalSeconds:F0} s.",
                    DeskLinkAuditLogLevel.Info);
                await Task.Delay(pause, cancellationToken);
            }

            try
            {
                var executeForBaseline = (string script, CancellationToken ct) =>
                    automationService.ExecuteScriptAsync(session, script, ct);

                string? staleListSignature = null;
                if (!string.IsNullOrWhiteSpace(session.CurrentUrl)
                    && session.CurrentUrl.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase))
                {
                    staleListSignature = await AvitoCandidatesPageWaiter
                        .TryCaptureListSignatureAsync(executeForBaseline, cancellationToken)
                        .ConfigureAwait(false);
                }

                await automationService.NavigateAsync(session, CandidatesPageUrl, cancellationToken);
                await WaitForCandidatesPageAsync(session, cancellationToken, staleListSignature);
                account.LastAuthCheckAt = DateTime.UtcNow;

                var execute = (string script, CancellationToken ct) =>
                    automationService.ExecuteScriptAsync(session, script, ct);
                await AvitoCandidatesListPreparer.PrepareAsync(
                    execute,
                    account.DisplayName,
                    cancellationToken,
                    ct => FetchPageHtmlSnapshotAsync(execute, ct),
                    CandidatesPageUrl).ConfigureAwait(false);

                var raw = await automationService.ExecuteScriptAsync(session, AvitoCandidatesPageScripts.BuildExtractionScript(), cancellationToken);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Empty extraction script result for {account.DisplayName} (attempt {attempt}/{maxAttempts}).",
                        DeskLinkAuditLogLevel.Warning);
                    if (attempt == maxAttempts)
                    {
                        account.LastErrorMessage = "Пустой ответ скрипта со страницы кандидатов после нескольких попыток";
                        return [];
                    }

                    continue;
                }

                return await ParseResponsesFromRawExtractionAsync(account, settings, raw, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"JSON parse error on candidates extraction for {account.DisplayName} (attempt {attempt}/{maxAttempts}): {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Некорректный JSON ответа страницы кандидатов после нескольких попыток";
                    return [];
                }
            }
            catch (TimeoutException ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Timeout waiting for candidates page for {account.DisplayName} (attempt {attempt}/{maxAttempts}): {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Таймаут загрузки страницы кандидатов после нескольких попыток";
                    return [];
                }
            }
        }

        account.LastErrorMessage = "Не удалось получить отклики со страницы кандидатов после нескольких попыток";
        return [];
    }

    private async Task<IReadOnlyList<CandidateResponse>> ParseResponsesFromRawExtractionAsync(
        AvitoAccount account,
        AppSettings settings,
        string raw,
        CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        var hasCaptcha = root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean();
        var hasLogin = root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean();
        var pageUrl = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;

        if (hasCaptcha)
        {
            // Поднимаем типизированное исключение: единый catch в MonitoringService поставит
            // RequiresManualAction и СРАЗУ выйдет из обхода аккаунта (без перебора оставшихся суб-профилей).
            // Кладём базовый статус сразу — на случай, если кто-то проигнорирует исключение.
            account.Status = AvitoAccountStatus.RequiresManualAction;
            account.LastErrorMessage = "На странице кандидатов требуется ручное действие";
            // Дополнительно строим короткий fingerprint HTML, если он есть — пригодится для классификации.
            var rawHtml = root.TryGetProperty("html", out var htmlProp) ? htmlProp.GetString() : null;
            var kind = AvitoCaptchaDetector.Classify(rawHtml) ?? "captcha";
            throw new AvitoCaptchaDetectedException(kind, pageUrl, rawHtml);
        }

        if (hasLogin)
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
            account.LastErrorMessage = "Для страницы кандидатов требуется повторная авторизация";
            return [];
        }

        account.Status = AvitoAccountStatus.Authorized;
        account.LastErrorMessage = string.Empty;
        account.LastAuthCheckAt = DateTime.UtcNow;

        var domItemCount = root.TryGetProperty("domItemCount", out var domItemsProp) ? domItemsProp.GetInt32() : 0;
        var domStatusCount = root.TryGetProperty("domStatusCount", out var domStatusProp) ? domStatusProp.GetInt32() : 0;

        var candidates = AvitoCandidatesJsonParser.ParseCandidates(root, account);
        var parsedCount = candidates.Count;
        if (domItemCount > 0 && parsedCount < domItemCount)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Candidates extraction for {account.DisplayName}: DOM has {domItemCount} cards (status buttons {domStatusCount}), parsed {parsedCount} with name+phone. Possible incomplete scroll or phones not loaded yet.",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["candidates.domItemCount"] = domItemCount,
                    ["candidates.domStatusCount"] = domStatusCount,
                    ["candidates.parsedCount"] = parsedCount
                });
        }

        var existingIds = await repository.GetExistingSourceResponseIdsAsync(
            candidates.Select(x => x.SourceResponseId),
            cancellationToken);

        var afterSourceId = candidates
            .Where(x => !existingIds.Contains(x.SourceResponseId))
            .ToList();

        // Avito меняет sourceResponseId (чат / хэш) — отсекаем по телефону так же, как при проверке дублей.
        var phonesToQuery = afterSourceId
            .Select(c => phoneNormalizer.Normalize(c.PhoneRaw))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingPhones = phonesToQuery.Count == 0
            ? []
            : await repository.GetExistingNormalizedPhonesAsync(
                phonesToQuery,
                settings.DuplicateScope,
                account.Id,
                cancellationToken);

        var afterDbPhone = afterSourceId
            .Where(c =>
            {
                var n = phoneNormalizer.Normalize(c.PhoneRaw);
                return string.IsNullOrWhiteSpace(n) || !existingPhones.Contains(n);
            })
            .ToList();

        var seenPhoneThisFetch = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<CandidateResponse>();
        foreach (var c in afterDbPhone)
        {
            var n = phoneNormalizer.Normalize(c.PhoneRaw);
            if (!string.IsNullOrWhiteSpace(n) && !seenPhoneThisFetch.Add(n))
            {
                continue;
            }

            deduped.Add(c);
        }

        return deduped
            .OrderBy(x => phoneNormalizer.Normalize(x.PhoneRaw), StringComparer.Ordinal)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task WaitForCandidatesPageAsync(
        BrowserAccountSession session,
        CancellationToken cancellationToken,
        string? baselineListSignature = null)
    {
        var execute = (string script, CancellationToken ct) =>
            automationService.ExecuteScriptAsync(session, script, ct);

        try
        {
            await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
                execute,
                ct => FetchPageHtmlSnapshotAsync(execute, ct),
                CandidatesPageUrl,
                cancellationToken,
                baselineListSignature).ConfigureAwait(false);
        }
        catch (AvitoCaptchaDetectedException)
        {
            throw;
        }
    }

    private static bool IsAdsPowerTransientCandidatesError(Exception ex) =>
        ex is PuppeteerException or TimeoutException;

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