using System.Text.Json;

namespace LeadFlow.Services.Avito;

/// <summary>
/// Ожидание списка откликов: короткий poll, ранний выход при firewall/капче.
/// </summary>
public static class AvitoCandidatesPageWaiter
{
    private const int MaxWaitMs = 50_000;
    private const int PollMs = 500;

    public static async Task WaitForCandidatesOrThrowFirewallAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot,
        string? pageUrl,
        CancellationToken cancellationToken)
    {
        for (var elapsed = 0; elapsed < MaxWaitMs; elapsed += PollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
                .ConfigureAwait(false);

            if (await TryParseReadyAsync(executeScript, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(PollMs, cancellationToken).ConfigureAwait(false);
        }

        await AvitoFirewallProbe.ThrowIfBlockedAsync(executeScript, fetchHtmlSnapshot, pageUrl, cancellationToken)
            .ConfigureAwait(false);

        if (!await TryParseReadyAsync(executeScript, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("Таймаут загрузки страницы кандидатов Авито (нет карточек откликов).");
        }
    }

    private static async Task<bool> TryParseReadyAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildWaitForReadyProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("blocked", out var blocked) && blocked.GetBoolean())
            {
                return false;
            }

            return root.TryGetProperty("ready", out var r) && r.GetBoolean();
        }
        catch
        {
            return false;
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
}
