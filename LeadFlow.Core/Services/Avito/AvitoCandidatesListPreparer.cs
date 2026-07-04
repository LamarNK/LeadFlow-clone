using System.Collections.Generic;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Перед снятием JSON: прокрутка бесконечного списка откликов и раскрытие телефонов (клик по кнопке с маской «**»).
/// </summary>
public static class AvitoCandidatesListPreparer
{
    private const int MaxScrollRounds = 48;
    private const int StableRoundsRequired = 3;
    private const int MaxPhoneRevealRounds = 32;
    private const int MaxPhoneRevealRoundsWhenNoItems = 2;
    private const int MaxDetailEnrichClicks = 40;

    public static async Task<CandidatesListPrepareResult> PrepareAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        string logContext,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot = null,
        string? pageUrl = null,
        IReadOnlySet<string>? knownNormalizedPhones = null)
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

            await Task.Delay(380, cancellationToken).ConfigureAwait(false);
        }

        _ = await executeScript(AvitoCandidatesPageScripts.BuildScrollToTopScript(), cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(320, cancellationToken).ConfigureAwait(false);

        var domItems = lastCount < 0 ? 0 : lastCount;
        var phoneRevealLimit = domItems == 0 ? MaxPhoneRevealRoundsWhenNoItems : MaxPhoneRevealRounds;
        var phoneRevealRounds = 0;
        var phoneClicksTotal = 0;
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
                    await Task.Delay(420, cancellationToken).ConfigureAwait(false);
                }

                phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
                if (phonesProbe?.Ready == true || phonesProbe?.Items == 0)
                {
                    break;
                }

                if (revealStep?.Masked == 0)
                {
                    await Task.Delay(280, cancellationToken).ConfigureAwait(false);
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
        if (domItems > 0)
        {
            var enrichment = await TryCollectDetailEnrichmentAsync(
                    executeScript,
                    Math.Min(domItems, MaxDetailEnrichClicks),
                    knownNormalizedPhones,
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
            detailEnrichHits);

        _ = GlobalLogger.Instance.LogAsync(
            $"Candidates list prepared for {logContext}: scrollRounds={result.ScrollRounds}, domItems={result.DomItemCount}, phonesReady={result.PhonesReady} ({result.CardsWithPhone}/{result.DomItemCount}), maskedLeft={result.MaskedPhonesLeft}, phoneRevealRounds={result.PhoneRevealRounds}, phoneClicks={result.PhoneRevealClicks}, detailEnrich={result.DetailEnrichHits}/{result.DetailEnrichClicks} (skipped {result.DetailEnrichSkipped}).",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["candidates.prepare.scrollRounds"] = result.ScrollRounds,
                ["candidates.prepare.domItemCount"] = result.DomItemCount,
                ["candidates.prepare.cardsWithPhone"] = result.CardsWithPhone,
                ["candidates.prepare.phonesReady"] = result.PhonesReady,
                ["candidates.prepare.maskedPhonesLeft"] = result.MaskedPhonesLeft,
                ["candidates.prepare.phoneRevealRounds"] = result.PhoneRevealRounds,
                ["candidates.prepare.phoneRevealClicks"] = result.PhoneRevealClicks,
                ["candidates.prepare.detailEnrichClicks"] = result.DetailEnrichClicks,
                ["candidates.prepare.detailEnrichSkipped"] = result.DetailEnrichSkipped,
                ["candidates.prepare.detailEnrichHits"] = result.DetailEnrichHits
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
        IReadOnlySet<string>? knownNormalizedPhones,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var clicks = 0;
        var skipped = 0;
        var hits = 0;
        var listPhones = await TryParseListItemPhonesAsync(executeScript, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (listPhones.TryGetValue(index, out var listPhoneDigits)
                && !string.IsNullOrWhiteSpace(listPhoneDigits)
                && knownNormalizedPhones is not null
                && knownNormalizedPhones.Contains(listPhoneDigits))
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
        }

        return new DetailEnrichmentResult(entries, clicks, skipped, hits);
    }

    private static async Task<Dictionary<int, string>> TryParseListItemPhonesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemPhonesScript(), cancellationToken)
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

                var index = indexProp.GetInt32();
                var phoneDigits = item.TryGetProperty("phoneDigits", out var phoneProp)
                    ? phoneProp.GetString() ?? string.Empty
                    : string.Empty;
                map[index] = phoneDigits;
            }

            return map;
        }
        catch
        {
            return [];
        }
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
    int DetailEnrichHits = 0);
