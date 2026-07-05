using System.Text.Json;
using LeadFlow.Core.Data;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Browser;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

public sealed class AvitoResponseSource(
    ICandidateDuplicateRepository duplicateRepository,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService,
    IPhoneNormalizer phoneNormalizer,
    IAvitoWebViewCandidatesFetcher? webViewCandidatesFetcher = null) : IAvitoResponseSource
{
    public const string CandidatesPageUrl = AvitoCandidatesPageUrls.LegacyCandidates;

    public const string JobResponsesPageUrl = AvitoCandidatesPageUrls.JobResponsesCrm;

    public Task<HashSet<string>> ResolveExistingPhonesAsync(
        Guid accountId,
        DuplicateScope duplicateScope,
        IEnumerable<string> phoneCandidates,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null) =>
        duplicateRepository.GetExistingNormalizedPhonesAsync(
            phoneCandidates,
            duplicateScope,
            accountId,
            cancellationToken,
            avitoSubProfileId);

    public async Task<IReadOnlyList<CandidateResponse>> ParseCandidatesFromRawAsync(
        AvitoAccount account,
        AppSettings settings,
        string rawExtractionJson,
        CancellationToken cancellationToken,
        AvitoSubProfile? activeSubProfile = null)
    {
        var result = await ParseCandidatesDetailedFromRawAsync(
                account,
                settings,
                rawExtractionJson,
                cancellationToken,
                activeSubProfile)
            .ConfigureAwait(false);
        return result.Candidates;
    }

    public Task<AvitoCandidatesParseResult> ParseCandidatesDetailedFromRawAsync(
        AvitoAccount account,
        AppSettings settings,
        string rawExtractionJson,
        CancellationToken cancellationToken,
        AvitoSubProfile? activeSubProfile = null) =>
        ParseResponsesFromRawExtractionAsync(account, settings, rawExtractionJson, cancellationToken, activeSubProfile);

    public async Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
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
                    var parsed = await ParseResponsesFromRawExtractionAsync(account, settings, rawAdsPower, cancellationToken)
                        .ConfigureAwait(false);
                    return parsed.Candidates;
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

        if (webViewCandidatesFetcher is null)
        {
            account.LastErrorMessage = "WebView2-источник откликов не зарегистрирован (только AdsPower в headless-режиме).";
            return [];
        }

        return await webViewCandidatesFetcher
            .FetchNewResponsesAsync(account, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<AvitoCandidatesParseResult> ParseResponsesFromRawExtractionAsync(
        AvitoAccount account,
        AppSettings settings,
        string raw,
        CancellationToken cancellationToken,
        AvitoSubProfile? activeSubProfile = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("Пустой ответ скрипта извлечения откликов.");
        }

        using var json = JsonDocument.Parse(raw.Trim());
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"Ожидался JSON-объект откликов, получено {root.ValueKind} (длина {raw.Length}).");
        }

        var hasCaptcha = root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean();
        var hasLogin = root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean();
        var pageUrl = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;

        if (hasCaptcha)
        {
            account.Status = AvitoAccountStatus.RequiresManualAction;
            var rawHtml = root.TryGetProperty("html", out var htmlProp) ? htmlProp.GetString() : null;
            var kind = AvitoCaptchaDetector.Classify(rawHtml) ?? "captcha";
            const string captchaDetail = "нужна проверка на странице откликов.";
            if (activeSubProfile is not null)
            {
                AccountIssueTracker.ApplySubProfileIssue(
                    account,
                    activeSubProfile,
                    AvitoSubProfileIssueKind.Captcha,
                    captchaDetail);
            }

            throw new AvitoCaptchaDetectedException(
                kind,
                pageUrl,
                rawHtml,
                subProfileId: activeSubProfile?.Id,
                subProfileName: activeSubProfile?.Name);
        }

        if (hasLogin)
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
            var loginDetail = "требуется повторная авторизация на странице откликов.";
            if (activeSubProfile is not null)
            {
                AccountIssueTracker.ApplySubProfileIssue(
                    account,
                    activeSubProfile,
                    AvitoSubProfileIssueKind.AuthRequired,
                    loginDetail);
            }
            else
            {
                account.LastErrorMessage = AccountIssueFormatting.FormatIssue(
                    account,
                    null,
                    AvitoSubProfileIssueKind.AuthRequired,
                    loginDetail);
            }

            return new AvitoCandidatesParseResult([], AvitoCandidatesExtractionSummary.Empty);
        }

        account.Status = AvitoAccountStatus.Authorized;
        if (!account.HasSubProfileIssues)
        {
            account.LastErrorMessage = string.Empty;
        }

        account.LastAuthCheckAt = DateTime.UtcNow;

        var pageVariant = root.TryGetProperty("pageVariant", out var variantProp) ? variantProp.GetString() ?? "unknown" : "unknown";
        var domItemCount = root.TryGetProperty("domItemCount", out var domItemsProp) ? domItemsProp.GetInt32() : 0;
        var domStatusCount = root.TryGetProperty("domStatusCount", out var domStatusProp) ? domStatusProp.GetInt32() : 0;
        var scriptCandidatesCount = AvitoCandidatesExtractionSummary.ReadScriptCandidatesCount(root);

        var candidates = AvitoCandidatesJsonParser.ParseCandidates(root, account);
        var parsedCount = candidates.Count;

        var existingIds = await duplicateRepository.GetExistingSourceResponseIdsAsync(
            account.Id,
            candidates.Select(x => x.SourceResponseId),
            cancellationToken);

        var afterSourceId = candidates
            .Where(x => !existingIds.Contains(x.SourceResponseId))
            .ToList();

        var phonesToQuery = afterSourceId
            .Select(c => phoneNormalizer.Normalize(c.PhoneRaw))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingPhones = phonesToQuery.Count == 0
            ? []
            : await duplicateRepository.GetExistingNormalizedPhonesAsync(
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
        var skippedDuplicatePhoneInBatch = 0;
        foreach (var c in afterDbPhone)
        {
            var n = phoneNormalizer.Normalize(c.PhoneRaw);
            if (!string.IsNullOrWhiteSpace(n) && !seenPhoneThisFetch.Add(n))
            {
                skippedDuplicatePhoneInBatch++;
                continue;
            }

            deduped.Add(c);
        }

        var ordered = deduped
            .OrderBy(x => phoneNormalizer.Normalize(x.PhoneRaw), StringComparer.Ordinal)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skippedExistingSourceId = candidates.Count - afterSourceId.Count;
        var skippedDuplicatePhoneInDb = afterSourceId.Count - afterDbPhone.Count;

        CandidateDedupLog.LogParseDedupSummary(
            account,
            activeSubProfile,
            settings.DuplicateScope,
            candidates.Count,
            skippedExistingSourceId,
            phonesToQuery.Count,
            skippedDuplicatePhoneInDb,
            skippedDuplicatePhoneInBatch);

        var sampleNames = ordered
            .Take(4)
            .Select(x => FormatSampleName(x.FullName, x.Vacancy))
            .ToList();

        var summary = new AvitoCandidatesExtractionSummary(
            pageUrl,
            pageVariant,
            domItemCount,
            domStatusCount,
            scriptCandidatesCount,
            parsedCount,
            skippedExistingSourceId,
            skippedDuplicatePhoneInDb,
            skippedDuplicatePhoneInBatch,
            ordered.Count,
            sampleNames);

        return new AvitoCandidatesParseResult(ordered, summary);
    }

    private static string FormatSampleName(string fullName, string vacancy)
    {
        var name = string.IsNullOrWhiteSpace(fullName) ? "—" : fullName.Trim();
        if (string.IsNullOrWhiteSpace(vacancy))
        {
            return name;
        }

        var shortVacancy = vacancy.Trim();
        if (shortVacancy.Length > 40)
        {
            shortVacancy = shortVacancy[..37] + "…";
        }

        return $"{name} ({shortVacancy})";
    }

    private static bool IsAdsPowerTransientCandidatesError(Exception ex) =>
        ex is PuppeteerException or TimeoutException or AdsPowerRateLimitExceededException;
}