using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Быстрый детект формы входа Avito до ожидания списка откликов.</summary>
public static class AvitoLoginProbe
{
    public sealed record Detection(bool HasLogin, string? Url, string? Title);

    public static async Task<Detection?> TryDetectAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoLoginDetectionScripts.BuildDetectionScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryParse(raw);
    }

    public static async Task ThrowIfLoginRequiredAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var detection = await TryDetectAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (detection is not { HasLogin: true })
        {
            return;
        }

        throw new AvitoLoginRequiredException(detection.Url, detection.Title);
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
            if (!root.TryGetProperty("hasLogin", out var loginProp) || !loginProp.GetBoolean())
            {
                return null;
            }

            var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            return new Detection(true, url, title);
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