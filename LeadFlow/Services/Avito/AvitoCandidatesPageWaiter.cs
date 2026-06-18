using System.Text.Json;
using LeadFlow.Services;

namespace LeadFlow.Services.Avito;

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
        string? baselineListSignature = null)
    {
        var maxWaitMs = MonitoringTiming.CandidatesPageMaxWaitMs;
        var pollMs = MonitoringTiming.CandidatesPagePollMs;
        var stableRequired = MonitoringTiming.CandidatesPageStablePollsRequired;

        string? lastStableSignature = null;
        var stablePolls = 0;

        for (var elapsed = 0; elapsed < maxWaitMs; elapsed += pollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
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

            if (!string.IsNullOrEmpty(baselineListSignature)
                && string.Equals(probe.ListSignature, baselineListSignature, StringComparison.Ordinal)
                && !probe.EmptyConfirmed)
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

            if (stablePolls >= stableRequired)
            {
                return;
            }

            await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
        }

        await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
            .ConfigureAwait(false);

        var finalProbe = await TryParseReadyProbeAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (finalProbe is not { ContentReady: true, Blocked: false }
            || (!string.IsNullOrEmpty(baselineListSignature)
                && string.Equals(finalProbe.ListSignature, baselineListSignature, StringComparison.Ordinal)
                && !finalProbe.EmptyConfirmed))
        {
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
                blocked);
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
        bool Blocked);
}