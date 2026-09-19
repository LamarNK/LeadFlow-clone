using System.Text.Json;
using LeadFlow.Core.Services.AdsPower;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito.Session;

/// <summary>
/// Выполняет единый probe-скрипт препятствий в браузерной вкладке и разбирает результат.
/// Ошибка CDP — это Unknown, а не «чистая страница»: оркестратор не должен считать
/// недоступную проверку отсутствием препятствий.
/// </summary>
public static class AvitoPageObstacleProbe
{
    private static readonly string ProbeScript = AvitoPageObstacleScripts.BuildProbeScript();
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FetchHtmlTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Выполняет probe в текущем документе вкладки. Никогда не бросает исключений, кроме отмены.</summary>
    public static async Task<AvitoPageObstacle> ProbeAsync(IPage page, CancellationToken cancellationToken)
    {
        string? raw;
        try
        {
            raw = await AdsPowerCdpGuard
                .WaitAsync(
                    page.EvaluateExpressionAsync<string>(ProbeScript),
                    ProbeTimeout,
                    "JavaScript-проверка препятствий страницы",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AvitoPageObstacle.Unknown;
        }

        return Parse(raw);
    }

    /// <summary>Разбирает JSON probe-скрипта; повреждённый ответ — Unknown.</summary>
    public static AvitoPageObstacle Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AvitoPageObstacle.Unknown;
        }

        try
        {
            var text = raw.Trim();
            if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
            {
                text = JsonSerializer.Deserialize<string>(text) ?? text;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var kindName = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            var kind = ParseKind(kindName);
            if (kind == AvitoPageObstacleKind.Unknown)
            {
                return AvitoPageObstacle.Unknown;
            }

            var captchaKind = root.TryGetProperty("captchaKind", out var captchaProp)
                ? captchaProp.GetString()
                : null;
            var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            List<string>? signals = null;
            if (root.TryGetProperty("signals", out var signalsProp)
                && signalsProp.ValueKind == JsonValueKind.Array)
            {
                signals = signalsProp.EnumerateArray()
                    .Select(static x => x.GetString())
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Select(static x => x!)
                    .ToList();
            }

            var profileSwitchOpen = root.TryGetProperty("profileSwitchOpen", out var switchProp)
                && switchProp.ValueKind == JsonValueKind.True;
            var profileSwitchCardCount = root.TryGetProperty("profileSwitchCardCount", out var cardsProp)
                && cardsProp.TryGetInt32(out var cards)
                    ? cards
                    : 0;

            return new AvitoPageObstacle(
                kind,
                captchaKind,
                url,
                title,
                signals,
                profileSwitchOpen,
                profileSwitchCardCount);
        }
        catch
        {
            return AvitoPageObstacle.Unknown;
        }
    }

    private static AvitoPageObstacleKind ParseKind(string? kindName) => kindName switch
    {
        "none" => AvitoPageObstacleKind.None,
        "captcha" => AvitoPageObstacleKind.Captcha,
        "ipBlocked" => AvitoPageObstacleKind.IpBlocked,
        "loginRequired" => AvitoPageObstacleKind.LoginRequired,
        "transientError" => AvitoPageObstacleKind.TransientError,
        "manualAction" => AvitoPageObstacleKind.ManualActionRequired,
        _ => AvitoPageObstacleKind.Unknown
    };

    /// <summary>
    /// Оркестратор для вкладки: probe — единый детектор, HTML для терминальных исключений — по запросу.
    /// Создавать внутри контекста субпрофиля (<see cref="Captcha.AvitoCaptchaTaskContext"/>):
    /// наблюдатель наследует AsyncLocal-контекст на момент Start.
    /// </summary>
    public static AvitoSessionOrchestrator CreateOrchestrator(
        IPage page,
        TimeSpan? probeInterval = null,
        Func<CancellationToken, Task<string?>>? fetchHtmlAsync = null) =>
        new(
            ct => ProbeAsync(page, ct),
            fetchHtmlAsync ?? (ct => FetchPageHtmlAsync(page, ct)),
            probeInterval);

    private static async Task<string?> FetchPageHtmlAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await AdsPowerCdpGuard
                .WaitAsync(
                    page.GetContentAsync(),
                    FetchHtmlTimeout,
                    "чтение HTML вкладки для препятствия",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
