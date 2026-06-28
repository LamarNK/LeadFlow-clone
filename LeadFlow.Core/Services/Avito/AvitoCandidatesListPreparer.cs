using System.Collections.Generic;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;

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

    public static async Task<CandidatesListPrepareResult> PrepareAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        string logContext,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot = null,
        string? pageUrl = null)
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
        var result = new CandidatesListPrepareResult(
            scrollRounds,
            phoneRevealRounds,
            lastCount < 0 ? phonesProbe?.Items ?? 0 : lastCount,
            phonesProbe?.WithPhone ?? 0,
            phonesProbe?.Ready == true,
            phonesProbe?.Masked ?? 0,
            phoneClicksTotal);

        _ = GlobalLogger.Instance.LogAsync(
            $"Candidates list prepared for {logContext}: scrollRounds={result.ScrollRounds}, domItems={result.DomItemCount}, phonesReady={result.PhonesReady} ({result.CardsWithPhone}/{result.DomItemCount}), maskedLeft={result.MaskedPhonesLeft}, phoneRevealRounds={result.PhoneRevealRounds}, phoneClicks={result.PhoneRevealClicks}.",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["candidates.prepare.scrollRounds"] = result.ScrollRounds,
                ["candidates.prepare.domItemCount"] = result.DomItemCount,
                ["candidates.prepare.cardsWithPhone"] = result.CardsWithPhone,
                ["candidates.prepare.phonesReady"] = result.PhonesReady,
                ["candidates.prepare.maskedPhonesLeft"] = result.MaskedPhonesLeft,
                ["candidates.prepare.phoneRevealRounds"] = result.PhoneRevealRounds,
                ["candidates.prepare.phoneRevealClicks"] = result.PhoneRevealClicks
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
}

public sealed record CandidatesListPrepareResult(
    int ScrollRounds,
    int PhoneRevealRounds,
    int DomItemCount,
    int CardsWithPhone,
    bool PhonesReady,
    int MaskedPhonesLeft = 0,
    int PhoneRevealClicks = 0);
