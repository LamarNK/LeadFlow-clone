using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Ожидание списка откликов: poll + ранний выход при firewall/капче + стабильность DOM
/// (не считаем страницу готовой, пока после reload висит старый список предыдущего суб-профиля).
/// </summary>
public static class AvitoCandidatesPageWaiter
{
    public static async Task WaitForCandidatesOrThrowFirewallAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot,
        string? pageUrl,
        CancellationToken cancellationToken,
        string? baselineListSignature = null,
        IPage? pageForRecovery = null,
        Func<AvitoFirewallProbe.Detection, string?, CancellationToken, Task<bool>>? trySolveCaptchaAsync = null)
    {
        var maxWaitMs = MonitoringTiming.CandidatesPageMaxWaitMs;
        var pollMs = MonitoringTiming.CandidatesPagePollMs;
        var stableRequired = MonitoringTiming.CandidatesPageStablePollsRequired;
        var baselineGraceMs = MonitoringTiming.CandidatesBaselineStaleAcceptGraceMs;

        string? lastStableSignature = null;
        var stablePolls = 0;

        for (var elapsed = 0; elapsed < maxWaitMs; elapsed += pollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await AvitoFirewallProbe.ThrowIfBlockedAsync(
                    executeScript,
                    fetchHtmlSnapshot,
                    pageUrl,
                    cancellationToken,
                    trySolveCaptchaAsync)
                .ConfigureAwait(false);

            await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken, pageForRecovery)
                .ConfigureAwait(false);

            var probe = await TryParseReadyProbeAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                stablePolls = 0;
                lastStableSignature = null;
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (probe.Blocked)
            {
                stablePolls = 0;
                lastStableSignature = null;
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!probe.ContentReady)
            {
                stablePolls = 0;
                lastStableSignature = null;
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(probe.ListSignature, lastStableSignature, StringComparison.Ordinal))
            {
                stablePolls++;
            }
            else
            {
                stablePolls = 1;
                lastStableSignature = probe.ListSignature;
            }

            var baselineMatches = !string.IsNullOrEmpty(baselineListSignature)
                                  && string.Equals(probe.ListSignature, baselineListSignature, StringComparison.Ordinal)
                                  && !probe.EmptyConfirmed;

            if (baselineMatches)
            {
                var stableEmpty = probe.ItemCount == 0 && probe.StatusCount == 0;
                if ((stableEmpty || elapsed >= baselineGraceMs) && stablePolls >= stableRequired)
                {
                    return;
                }

                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (stablePolls >= stableRequired)
            {
                return;
            }

            await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
        }

        await AvitoFirewallProbe.ThrowIfBlockedAsync(
                executeScript,
                fetchHtmlSnapshot,
                pageUrl,
                cancellationToken,
                trySolveCaptchaAsync)
            .ConfigureAwait(false);

        await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken, pageForRecovery)
            .ConfigureAwait(false);

        var finalProbe = await TryParseReadyProbeAsync(executeScript, cancellationToken).ConfigureAwait(false);
        var baselineBlocksAccept = !string.IsNullOrEmpty(baselineListSignature)
                                   && finalProbe is not null
                                   && string.Equals(finalProbe.ListSignature, baselineListSignature, StringComparison.Ordinal)
                                   && !finalProbe.EmptyConfirmed
                                   && !(finalProbe.ItemCount == 0 && finalProbe.StatusCount == 0);

        if (finalProbe is not { ContentReady: true, Blocked: false } || baselineBlocksAccept)
        {
            _ = GlobalLogger.Instance.LogAsync(
                "Candidates page waiter timed out.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(AvitoCandidatesPageWaiter),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "candidates_wait_timeout",
                    ["candidates.baselineSignature"] = baselineListSignature ?? "<none>",
                    ["candidates.finalSignature"] = finalProbe?.ListSignature ?? "<none>",
                    ["candidates.itemCount"] = finalProbe?.ItemCount,
                    ["candidates.statusCount"] = finalProbe?.StatusCount,
                    ["candidates.contentReady"] = finalProbe?.ContentReady,
                    ["candidates.emptyConfirmed"] = finalProbe?.EmptyConfirmed,
                    ["candidates.waitMs"] = maxWaitMs
                });

            throw new TimeoutException("Таймаут загрузки страницы кандидатов Авито (список откликов не стабилизировался).");
        }
    }

    public static async Task<string?> TryCaptureListSignatureAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var probe = await TryParseReadyProbeAsync(executeScript, cancellationToken).ConfigureAwait(false);
        return probe?.ListSignature;
    }

    private static async Task<CandidatesReadyProbe?> TryParseReadyProbeAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildWaitForReadyProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var itemCount = root.TryGetProperty("itemCount", out var ic) ? ic.GetInt32() : 0;
            var statusCount = root.TryGetProperty("statusCount", out var sc) ? sc.GetInt32() : 0;
            var loading = root.TryGetProperty("loading", out var ld) && ld.GetBoolean();
            var readyState = root.TryGetProperty("readyState", out var rs) ? rs.GetString() : null;
            var emptyConfirmed = root.TryGetProperty("emptyConfirmed", out var ec) && ec.GetBoolean();
            var blocked = root.TryGetProperty("blocked", out var bl) && bl.GetBoolean();
            var listSignature = root.TryGetProperty("listSignature", out var sig) ? sig.GetString() ?? "" : "";

            var contentReady = !loading
                               && string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase)
                               && (itemCount > 0 || statusCount > 0 || emptyConfirmed
                                   || (itemCount == 0 && statusCount == 0 && !loading));

            return new CandidatesReadyProbe(
                listSignature,
                contentReady,
                emptyConfirmed,
                blocked,
                itemCount,
                statusCount);
        }
        catch
        {
            return null;
        }
    }

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

    private sealed record CandidatesReadyProbe(
        string ListSignature,
        bool ContentReady,
        bool EmptyConfirmed,
        bool Blocked,
        int ItemCount,
        int StatusCount);
}