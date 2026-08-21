using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Перед снятием JSON: прокрутка бесконечного списка откликов и раскрытие телефонов (клик по кнопке с маской «**»).
/// </summary>
public static class AvitoCandidatesListPreparer
{
    private const int MaxScrollRounds = 48;
    private const int StableRoundsRequired = 3;
    private const int MaxPhoneRevealRounds = 40;
    private const int MaxPhoneRevealRoundsWhenNoItems = 2;
    private const int MaxDetailEnrichClicks = 40;

    public static async Task<CandidatesListPrepareResult> PrepareAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        string logContext,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot = null,
        string? pageUrl = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingSourceResponseIdsAsync = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingCardFingerprintsAsync = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync = null,
        Func<IReadOnlyList<CandidateLookupProfileDto>, CancellationToken, Task<IReadOnlySet<int>>>? resolveExistingMatchedProfileIndicesAsync = null,
        ResponseCollectionFilters? responseCollectionFilters = null,
        Func<string, CancellationToken, Task<bool>>? isOpenPhoneWatchAsync = null)
    {
        await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
            .ConfigureAwait(false);

        var lastCount = -1;
        var stableRounds = 0;
        var scrollRounds = 0;

        for (var round = 0; round < MaxScrollRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scrollRounds++;

            if (round > 0 && round % 3 == 0)
            {
                await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            var step = await TryParseScrollStepAsync(executeScript, cancellationToken).ConfigureAwait(false);
            var count = step?.ItemCount ?? 0;

            if (count == lastCount && (step is null || !step.Moved || step.AtEnd))
            {
                stableRounds++;
                if (stableRounds >= StableRoundsRequired)
                {
                    break;
                }
            }
            else
            {
                stableRounds = 0;
                lastCount = count;
            }

            await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await executeScript(AvitoCandidatesPageScripts.BuildScrollToTopScript(), cancellationToken)
            .ConfigureAwait(false);
        await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);

        var domItems = lastCount < 0 ? 0 : lastCount;
        var phoneRevealLimit = domItems == 0 ? MaxPhoneRevealRoundsWhenNoItems : MaxPhoneRevealRounds;
        var phoneRevealRounds = 0;
        var phoneClicksTotal = 0;
        var openWatchProtected = await ResolveOpenPhoneWatchProtectedIndicesAsync(
                executeScript,
                isOpenPhoneWatchAsync,
                cancellationToken)
            .ConfigureAwait(false);

        var cardFingerprintSkipCount = await TryApplyKnownCardFingerprintSkipsAsync(
                executeScript,
                resolveExistingCardFingerprintsAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var phoneSkipCount = await TryApplyKnownPhoneSkipsAsync(
                executeScript,
                resolveExistingPhonesAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var profileSkipCount = await TryApplyKnownProfileSkipsAsync(
                executeScript,
                resolveExistingMatchedProfileIndicesAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var collectionFilterSkipCount = await TryApplyResponseCollectionFilterSkipsAsync(
                executeScript,
                responseCollectionFilters,
                cancellationToken)
            .ConfigureAwait(false);
        PhonesReadyProbe? phonesProbe = null;

        await TryEnableClipboardGuardAsync(executeScript, cancellationToken).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < phoneRevealLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                phoneRevealRounds++;

                var revealStep = await TryRevealMaskedPhonesAsync(executeScript, cancellationToken).ConfigureAwait(false);
                if (revealStep?.Clicked > 0)
                {
                    phoneClicksTotal += revealStep.Clicked;
                    await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);
                    phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
                    if (phonesProbe?.Ready == true || phonesProbe?.Items == 0)
                    {
                        break;
                    }

                    continue;
                }

                var popupStep = await TryRevealNextContactsPopupPhoneAsync(executeScript, cancellationToken)
                    .ConfigureAwait(false);
                if (popupStep?.Clicked == true)
                {
                    phoneClicksTotal++;
                    await HumanDelay.AfterPhoneRevealOutcomeAsync(popupStep.Revealed, cancellationToken)
                        .ConfigureAwait(false);
                }

                phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
                if (phonesProbe?.Ready == true || phonesProbe?.Items == 0)
                {
                    break;
                }

                if (revealStep?.Masked == 0 && popupStep?.Pending == 0)
                {
                    await HumanDelay.DelayAsync(280, 520, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await TryDisableClipboardGuardAsync(executeScript, CancellationToken.None).ConfigureAwait(false);
        }

        phonesProbe ??= await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);

        var detailEnrichClicks = 0;
        var detailEnrichSkipped = 0;
        var detailEnrichHits = 0;
        var isJobCrmPage = await TryDetectJobCrmResponsesPageAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);
        if (domItems > 0 && !isJobCrmPage)
        {
            var enrichment = await TryCollectDetailEnrichmentAsync(
                    executeScript,
                    Math.Min(domItems, MaxDetailEnrichClicks),
                    resolveExistingSourceResponseIdsAsync,
                    resolveExistingPhonesAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            detailEnrichClicks = enrichment.Clicks;
            detailEnrichSkipped = enrichment.Skipped;
            detailEnrichHits = enrichment.Hits;
            if (enrichment.Entries.Count > 0)
            {
                var enrichmentJson = JsonSerializer.Serialize(enrichment.Entries);
                _ = await executeScript(
                        AvitoCandidatesPageScripts.BuildApplyDetailEnrichmentScript(enrichmentJson),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var result = new CandidatesListPrepareResult(
            scrollRounds,
            phoneRevealRounds,
            lastCount < 0 ? phonesProbe?.Items ?? 0 : lastCount,
            phonesProbe?.WithPhone ?? 0,
            phonesProbe?.Ready == true,
            phonesProbe?.Masked ?? 0,
            phoneClicksTotal,
            detailEnrichClicks,
            detailEnrichSkipped,
            detailEnrichHits,
            cardFingerprintSkipCount,
            phoneSkipCount,
            profileSkipCount,
            collectionFilterSkipCount);

        var detailEnrichNote = isJobCrmPage
            ? "detailEnrich=skipped (CRM page)"
            : $"detailEnrich={result.DetailEnrichHits}/{result.DetailEnrichClicks} (skipped {result.DetailEnrichSkipped})";
        _ = GlobalLogger.Instance.LogAsync(
            $"Candidates list prepared for {logContext}: scrollRounds={result.ScrollRounds}, domItems={result.DomItemCount}, phonesReady={result.PhonesReady} ({result.CardsWithPhone}/{result.DomItemCount}), maskedLeft={result.MaskedPhonesLeft}, cardFingerprintSkips={result.CardFingerprintSkips}, phoneSkips={result.PhoneSkips}, profileSkips={result.ProfileSkips}, collectionFilterSkips={result.CollectionFilterSkips}, phoneRevealRounds={result.PhoneRevealRounds}, phoneClicks={result.PhoneRevealClicks}, {detailEnrichNote}.",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["candidates.prepare.isJobCrmPage"] = isJobCrmPage,
                ["candidates.prepare.scrollRounds"] = result.ScrollRounds,
                ["candidates.prepare.domItemCount"] = result.DomItemCount,
                ["candidates.prepare.cardsWithPhone"] = result.CardsWithPhone,
                ["candidates.prepare.phonesReady"] = result.PhonesReady,
                ["candidates.prepare.maskedPhonesLeft"] = result.MaskedPhonesLeft,
                ["candidates.prepare.phoneRevealRounds"] = result.PhoneRevealRounds,
                ["candidates.prepare.phoneRevealClicks"] = result.PhoneRevealClicks,
                ["candidates.prepare.detailEnrichClicks"] = result.DetailEnrichClicks,
                ["candidates.prepare.detailEnrichSkipped"] = result.DetailEnrichSkipped,
                ["candidates.prepare.detailEnrichHits"] = result.DetailEnrichHits,
                ["candidates.prepare.cardFingerprintSkips"] = result.CardFingerprintSkips,
                ["candidates.prepare.phoneSkips"] = result.PhoneSkips,
                ["candidates.prepare.profileSkips"] = result.ProfileSkips
            });

        return result;
    }

    private static async Task<ScrollStepProbe?> TryParseScrollStepAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildScrollStepScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ScrollStepProbe(
                root.TryGetProperty("itemCount", out var c) ? c.GetInt32() : 0,
                root.TryGetProperty("moved", out var m) && m.GetBoolean(),
                root.TryGetProperty("atEnd", out var e) && e.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PhonesReadyProbe?> TryParsePhonesReadyAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new PhonesReadyProbe(
                root.TryGetProperty("ready", out var r) && r.GetBoolean(),
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("withPhone", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("masked", out var m) ? m.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryEnableClipboardGuardAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        _ = await executeScript(AvitoCandidatesPageScripts.BuildEnableClipboardGuardScript(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TryDisableClipboardGuardAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await executeScript(AvitoCandidatesPageScripts.BuildDisableClipboardGuardScript(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Страница могла перезагрузиться; не прерываем подготовку списка.
        }
    }

    private static async Task<DetailEnrichmentResult> TryCollectDetailEnrichmentAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        int itemCount,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingSourceResponseIdsAsync,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var clicks = 0;
        var skipped = 0;
        var hits = 0;
        var listItems = await TryParseListItemSkipKeysAsync(executeScript, cancellationToken).ConfigureAwait(false);

        IReadOnlySet<string>? existingOnPage = null;
        if (resolveExistingSourceResponseIdsAsync is not null)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < itemCount; index++)
            {
                if (listItems.TryGetValue(index, out var listItem)
                    && !string.IsNullOrWhiteSpace(listItem.SourceResponseId))
                {
                    candidates.Add(listItem.SourceResponseId);
                }
            }

            if (candidates.Count > 0)
            {
                existingOnPage = await resolveExistingSourceResponseIdsAsync(candidates, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IReadOnlySet<string>? existingPhones = null;
        if (resolveExistingPhonesAsync is not null)
        {
            var phoneCandidates = listItems.Values
                .Select(static x => x.PhoneDigits)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (phoneCandidates.Length > 0)
            {
                existingPhones = await resolveExistingPhonesAsync(phoneCandidates, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (listItems.TryGetValue(index, out var listItem)
                && !string.IsNullOrWhiteSpace(listItem.SourceResponseId)
                && existingOnPage is not null
                && existingOnPage.Contains(listItem.SourceResponseId))
            {
                skipped++;
                continue;
            }

            if (listItems.TryGetValue(index, out var phoneListItem)
                && !string.IsNullOrWhiteSpace(phoneListItem.PhoneDigits)
                && existingPhones is not null
                && existingPhones.Contains(phoneListItem.PhoneDigits))
            {
                skipped++;
                continue;
            }

            clicks++;

            await HumanDelay.BeforeCandidateClickAsync(cancellationToken).ConfigureAwait(false);

            var clickRaw = await executeScript(
                    AvitoCandidatesPageScripts.BuildClickCandidateItemByIndexScript(index),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryParseClickStep(clickRaw, out var clickOk) || !clickOk)
            {
                continue;
            }

            await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);

            var detailRaw = await executeScript(AvitoCandidatesPageScripts.BuildReadDetailPanelScript(), cancellationToken)
                .ConfigureAwait(false);
            var detail = TryParseDetailPanel(detailRaw);
            if (detail is null)
            {
                continue;
            }

            var payload = new Dictionary<string, string?>
            {
                ["vacancyUrl"] = detail.VacancyUrl,
                ["vacancy"] = detail.Vacancy,
                ["city"] = detail.City,
                ["age"] = detail.Age
            };

            if (!string.IsNullOrWhiteSpace(detail.VacancyUrl))
            {
                hits++;
            }

            entries[index.ToString()] = payload;
            if (!string.IsNullOrWhiteSpace(detail.PhoneDigits))
            {
                entries[detail.PhoneDigits] = payload;
            }

            await HumanDelay.AfterDetailPanelReadAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DetailEnrichmentResult(entries, clicks, skipped, hits);
    }

    private static async Task<Dictionary<int, ListItemSkipKeys>> TryParseListItemSkipKeysAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemSkipKeysScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var map = new Dictionary<int, ListItemSkipKeys>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexProp))
                {
                    continue;
                }

                var index = indexProp.GetInt32();
                var phoneDigits = item.TryGetProperty("phoneDigits", out var phoneProp)
                    ? phoneProp.GetString() ?? string.Empty
                    : string.Empty;
                var sourceResponseId = item.TryGetProperty("sourceResponseId", out var sourceIdProp)
                    ? sourceIdProp.GetString() ?? string.Empty
                    : string.Empty;
                map[index] = new ListItemSkipKeys(phoneDigits, sourceResponseId);
            }

            return map;
        }
        catch
        {
            return [];
        }
    }

    private sealed record ListItemSkipKeys(string PhoneDigits, string SourceResponseId);

    private sealed record ListItemProfileKeys(
        int Index,
        string FullName,
        string City,
        string Age,
        string Gender,
        string PhoneDigits);

    private static async Task<IReadOnlyList<ListItemProfileKeys>> TryParseListItemProfilesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemCardFingerprintsScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var items = new List<ListItemProfileKeys>();
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var itemIndex = element.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : index;
                items.Add(new ListItemProfileKeys(
                    itemIndex,
                    element.TryGetProperty("fullName", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("age", out var ageProp) ? ageProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("gender", out var genderProp) ? genderProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("phoneDigits", out var phoneProp) ? phoneProp.GetString() ?? string.Empty : string.Empty));
                index++;
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<int> TryApplyResponseCollectionFilterSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        ResponseCollectionFilters? filters,
        CancellationToken cancellationToken)
    {
        if (filters is null || !filters.Enabled)
        {
            return 0;
        }

        var listItems = await TryParseListItemProfilesAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (listItems.Count == 0)
        {
            return 0;
        }

        var skipIndices = new List<int>();
        foreach (var item in listItems)
        {
            var result = ResponseCollectionFilter.EvaluateCandidate(
                item.FullName,
                ParseAge(item.Age),
                item.Gender,
                rawText: null,
                filters);
            if (!result.Pass)
            {
                skipIndices.Add(item.Index);
            }
        }

        if (skipIndices.Count == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Count;
    }

    private static int? ParseAge(string? ageText)
    {
        if (string.IsNullOrWhiteSpace(ageText))
        {
            return null;
        }

        var digits = new StringBuilder();
        foreach (var ch in ageText)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
                if (digits.Length >= 2)
                {
                    break;
                }
            }
        }

        return digits.Length > 0 && int.TryParse(digits.ToString(), out var age) ? age : null;
    }

    private static string NormalizePhoneDigits(string? phoneDigits)
    {
        if (string.IsNullOrWhiteSpace(phoneDigits))
        {
            return string.Empty;
        }

        var value = phoneDigits.Trim();
        if (value.Length == 11 && value.StartsWith("8", StringComparison.Ordinal))
        {
            value = $"7{value[1..]}";
        }
        else if (value.Length == 10)
        {
            value = $"7{value}";
        }

        return value;
    }

    private static bool TryParseClickStep(string? raw, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DetailPanelProbe? TryParseDetailPanel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            var phoneDigits = root.TryGetProperty("phoneDigits", out var phoneProp)
                ? phoneProp.GetString() ?? string.Empty
                : string.Empty;
            return new DetailPanelProbe(
                phoneDigits,
                root.TryGetProperty("vacancyUrl", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("vacancy", out var vacancyProp) ? vacancyProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("age", out var ageProp) ? ageProp.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TryDetectJobCrmResponsesPageAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildIsJobCrmResponsesPageScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            return doc.RootElement.TryGetProperty("isJobCrm", out var prop) && prop.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Индексы карточек с открытым phone-watch — phone-reveal для них нельзя скипать.
    /// </summary>
    private static async Task<HashSet<int>> ResolveOpenPhoneWatchProtectedIndicesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<string, CancellationToken, Task<bool>>? isOpenPhoneWatchAsync,
        CancellationToken cancellationToken)
    {
        if (isOpenPhoneWatchAsync is null)
        {
            return [];
        }

        var listItems = await TryParseListItemProfilesAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (listItems.Count == 0)
        {
            return [];
        }

        var protectedIndices = new HashSet<int>();
        foreach (var item in listItems)
        {
            if (string.IsNullOrWhiteSpace(item.FullName))
            {
                continue;
            }

            try
            {
                if (await isOpenPhoneWatchAsync(item.FullName, cancellationToken).ConfigureAwait(false))
                {
                    protectedIndices.Add(item.Index);
                }
            }
            catch
            {
                // best-effort: ошибка store не должна валить сбор
            }
        }

        return protectedIndices;
    }

    private static bool HasFullyRevealedPhoneDigits(string? phoneDigits)
    {
        var digits = NormalizePhoneDigits(phoneDigits);
        return digits.Length >= 10;
    }

    private static async Task<int> TryApplyKnownCardFingerprintSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingCardFingerprintsAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingCardFingerprintsAsync is null)
        {
            return 0;
        }

        var fingerprintsByIndex = await TryParseCardFingerprintsAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);
        if (fingerprintsByIndex.Count == 0)
        {
            return 0;
        }

        var existing = await resolveExistingCardFingerprintsAsync(
                fingerprintsByIndex.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return 0;
        }

        // Phone digits per index — не скипать, пока номер не раскрыт (маска / phone-watch).
        var phonesByIndex = await TryParseListItemSkipKeysAsync(executeScript, cancellationToken).ConfigureAwait(false);

        var skipIndices = fingerprintsByIndex
            .Where(kv => existing.Contains(kv.Value))
            .Where(kv => !openWatchProtected.Contains(kv.Key))
            .Where(kv =>
                phonesByIndex.TryGetValue(kv.Key, out var keys)
                && HasFullyRevealedPhoneDigits(keys.PhoneDigits))
            .Select(kv => kv.Key)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<int> TryApplyKnownPhoneSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingPhonesAsync is null)
        {
            return 0;
        }

        var phonesByIndex = await TryParseListItemSkipKeysAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (phonesByIndex.Count == 0)
        {
            return 0;
        }

        var phoneCandidates = phonesByIndex.Values
            .Select(static x => x.PhoneDigits)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (phoneCandidates.Length == 0)
        {
            return 0;
        }

        var existing = await resolveExistingPhonesAsync(phoneCandidates, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return 0;
        }

        // Известный старый номер + open watch — всё равно раскрываем (номер мог смениться под маской).
        var skipIndices = phonesByIndex
            .Where(kv => !openWatchProtected.Contains(kv.Key))
            .Where(kv => HasFullyRevealedPhoneDigits(kv.Value.PhoneDigits)
                         && existing.Contains(kv.Value.PhoneDigits))
            .Select(static kv => kv.Key)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<int> TryApplyKnownProfileSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<IReadOnlyList<CandidateLookupProfileDto>, CancellationToken, Task<IReadOnlySet<int>>>? resolveExistingMatchedProfileIndicesAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingMatchedProfileIndicesAsync is null)
        {
            return 0;
        }

        var listItems = await TryParseListItemProfilesAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (listItems.Count == 0)
        {
            return 0;
        }

        var profiles = listItems
            .Select(item => new CandidateLookupProfileDto(
                item.FullName,
                ParseAge(item.Age),
                item.City,
                NormalizePhoneDigits(item.PhoneDigits)))
            .ToList();
        var matched = await resolveExistingMatchedProfileIndicesAsync(profiles, cancellationToken)
            .ConfigureAwait(false);
        if (matched.Count == 0)
        {
            return 0;
        }

        // Уже в Орбите, но open phone-watch или номер не раскрыт — reveal обязателен.
        var skipIndices = matched
            .Where(i => i >= 0 && i < listItems.Count)
            .Select(i => listItems[i])
            .Where(item => !openWatchProtected.Contains(item.Index))
            .Where(item => HasFullyRevealedPhoneDigits(item.PhoneDigits))
            .Select(item => item.Index)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<Dictionary<int, string>> TryParseCardFingerprintsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemCardFingerprintsScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var map = new Dictionary<int, string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexProp))
                {
                    continue;
                }

                var fingerprint = item.TryGetProperty("cardFingerprint", out var fpProp)
                    ? fpProp.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    continue;
                }

                map[indexProp.GetInt32()] = fingerprint.Trim();
            }

            return map;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<RevealPhonesStepProbe?> TryRevealMaskedPhonesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildRevealMaskedPhonesStepScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new RevealPhonesStepProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("masked", out var m) ? m.GetInt32() : 0,
                root.TryGetProperty("clicked", out var c) ? c.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ContactsPopupRevealProbe?> TryRevealNextContactsPopupPhoneAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var click = await TryParseContactsPopupClickAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (click is null)
        {
            return null;
        }

        if (click.ClosedExisting)
        {
            await HumanDelay.DelayAsync(180, 420, cancellationToken).ConfigureAwait(false);
            click = await TryParseContactsPopupClickAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (click is null)
            {
                return null;
            }
        }

        if (!click.Clicked)
        {
            return new ContactsPopupRevealProbe(click.Items, click.Pending, false, false);
        }

        await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);

        for (var elapsed = 0;
             elapsed < MonitoringTiming.ContactsPopupMaxWaitMs;
             elapsed += (MonitoringTiming.ContactsPopupPollMinMs + MonitoringTiming.ContactsPopupPollMaxMs) / 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await TryParseContactsPopupProbeAsync(executeScript, click.Index, cancellationToken)
                .ConfigureAwait(false);
            if (probe is { Revealed: true })
            {
                return new ContactsPopupRevealProbe(click.Items, click.Pending, true, true);
            }

            if (probe is { State: "error" })
            {
                _ = await executeScript(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), cancellationToken)
                    .ConfigureAwait(false);
                return new ContactsPopupRevealProbe(click.Items, click.Pending, true, false);
            }

            await HumanDelay.DelayAsync(
                    MonitoringTiming.ContactsPopupPollMinMs,
                    MonitoringTiming.ContactsPopupPollMaxMs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _ = await executeScript(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), cancellationToken)
            .ConfigureAwait(false);
        return new ContactsPopupRevealProbe(click.Items, click.Pending, true, false);
    }

    private static async Task<ContactsPopupClickProbe?> TryParseContactsPopupClickAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildRevealNextContactsPopupPhoneScript(),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ContactsPopupClickProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("pending", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("clicked", out var c) && c.ValueKind == JsonValueKind.True,
                root.TryGetProperty("closedExisting", out var closed) && closed.ValueKind == JsonValueKind.True,
                root.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ContactsPopupStateProbe?> TryParseContactsPopupProbeAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        int targetIndex,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(targetIndex),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ContactsPopupStateProbe(
                root.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "",
                root.TryGetProperty("revealed", out var revealedProp) && revealedProp.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WebView2 иногда возвращает JSON-строку в кавычках.</summary>
    private static string UnwrapJsonString(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(t) ?? t;
            }
            catch
            {
                return t;
            }
        }

        return t;
    }

    private sealed record ScrollStepProbe(int ItemCount, bool Moved, bool AtEnd);

    private sealed record PhonesReadyProbe(bool Ready, int Items, int WithPhone, int Masked);

    private sealed record RevealPhonesStepProbe(int Items, int Masked, int Clicked);

    private sealed record ContactsPopupRevealProbe(int Items, int Pending, bool Clicked, bool Revealed);

    private sealed record ContactsPopupClickProbe(int Items, int Pending, bool Clicked, bool ClosedExisting, int Index);

    private sealed record ContactsPopupStateProbe(string State, bool Revealed);

    private sealed record DetailPanelProbe(string PhoneDigits, string VacancyUrl, string Vacancy, string City, string Age);

    private sealed record DetailEnrichmentResult(Dictionary<string, object> Entries, int Clicks, int Skipped, int Hits);
}

public sealed record CandidatesListPrepareResult(
    int ScrollRounds,
    int PhoneRevealRounds,
    int DomItemCount,
    int CardsWithPhone,
    bool PhonesReady,
    int MaskedPhonesLeft = 0,
    int PhoneRevealClicks = 0,
    int DetailEnrichClicks = 0,
    int DetailEnrichSkipped = 0,
    int DetailEnrichHits = 0,
    int CardFingerprintSkips = 0,
    int PhoneSkips = 0,
    int ProfileSkips = 0,
    int CollectionFilterSkips = 0);
