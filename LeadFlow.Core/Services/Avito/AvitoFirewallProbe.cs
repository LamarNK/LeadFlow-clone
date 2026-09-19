using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Быстрый детект страницы «Доступ ограничен / капча» на <c>/profile/candidates</c> (DOM + title),
/// без ожидания полной загрузки списка откликов.
/// </summary>
public static class AvitoFirewallProbe
{
    public sealed record Detection(string Kind, string? Url, string? Title);

    public static async Task<Detection?> TryDetectAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildFirewallProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryParse(raw);
    }

    public static async Task ThrowIfBlockedAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot,
        string? pageUrl,
        CancellationToken cancellationToken,
        Func<Detection, string?, CancellationToken, Task<bool>>? trySolveAsync = null)
    {
        var detection = await TryDetectAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (detection is null)
        {
            return;
        }

        string? html = null;
        if (fetchHtmlSnapshot is not null)
        {
            try
            {
                html = await fetchHtmlSnapshot(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Достаточно kind/title из probe.
            }
        }

        if (trySolveAsync is not null)
        {
            var solved = await trySolveAsync(detection, html, cancellationToken).ConfigureAwait(false);
            if (solved)
            {
                return;
            }
        }

        // HTML snapshot может содержать скрытую/устаревшую разметку другого challenge.
        // Единственный источник вида препятствия — живой probe видимой страницы.
        throw new AvitoCaptchaDetectedException(detection.Kind, detection.Url ?? pageUrl, html);
    }

    public static Detection? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("blocked", out var blockedProp) || !blockedProp.GetBoolean())
            {
                return null;
            }

            var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            return new Detection(string.IsNullOrWhiteSpace(kind) ? "captcha" : kind!, url, title);
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
}
