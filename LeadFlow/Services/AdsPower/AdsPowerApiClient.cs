using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LeadFlow.Logging.Audit;

namespace LeadFlow.Services.AdsPower;

public sealed class AdsPowerApiClient(IHttpClientFactory httpClientFactory) : IAdsPowerApiClient
{
    private const int RateLimitMaxAttempts = 6;

    public Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteWithRateLimitRetryAsync(
            options,
            async ct =>
            {
                var baseUrl = NormalizeBaseUrl(options.BaseUrl);
                var url = $"{baseUrl}/api/v1/user/list?page=1&page_size=100";
                Log(
                    $"AdsPower list request started for {baseUrl}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(ListProfilesAsync),
                    CreateProperties(baseUrl, hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey)));

                var (response, json) = await SendGetAsync(options, url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log(
                        $"AdsPower list request failed with HTTP {(int)response.StatusCode}. Body: {Truncate(json, 500)}",
                        DeskLinkAuditLogLevel.Error,
                        nameof(ListProfilesAsync),
                        CreateProperties(
                            baseUrl,
                            hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                            httpStatusCode: (int)response.StatusCode));
                    throw new InvalidOperationException(
                        $"AdsPower API вернул {(int)response.StatusCode}: {Truncate(json, 500)}");
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                EnsureApiSuccess(
                    root,
                    baseUrl,
                    options,
                    nameof(ListProfilesAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey)));

                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    return (IReadOnlyList<AdsPowerProfileSummary>)[];
                }

                if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var result = new List<AdsPowerProfileSummary>();
                foreach (var item in list.EnumerateArray())
                {
                    var userId = item.TryGetProperty("user_id", out var uid) ? uid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        continue;
                    }

                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var serial = item.TryGetProperty("serial_number", out var sn) ? sn.GetString() : null;
                    var group = item.TryGetProperty("group_name", out var gn) ? gn.GetString() : null;
                    result.Add(new AdsPowerProfileSummary(
                        userId,
                        string.IsNullOrWhiteSpace(name) ? userId : name!,
                        serial,
                        group));
                }

                Log(
                    $"AdsPower list request completed successfully. Profiles loaded: {result.Count}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(ListProfilesAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        profileCount: result.Count));
                return result;
            },
            cancellationToken);

    public Task<AdsPowerBrowserStartResult> StartBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string? openUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        return ExecuteWithRateLimitRetryAsync(
            options,
            async ct =>
            {
                var baseUrl = NormalizeBaseUrl(options.BaseUrl);
                var q = $"user_id={Uri.EscapeDataString(adsPowerUserId)}";
                if (!string.IsNullOrWhiteSpace(openUrl))
                {
                    var openUrlsJson = JsonSerializer.Serialize(new[] { openUrl });
                    q += "&open_urls=" + Uri.EscapeDataString(openUrlsJson);
                }

                var url = $"{baseUrl}/api/v1/browser/start?{q}";
                Log(
                    $"AdsPower browser/start request started for profile {adsPowerUserId}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(StartBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId,
                        openUrl: openUrl));

                var (response, json) = await SendGetAsync(options, url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log(
                        $"AdsPower browser/start failed with HTTP {(int)response.StatusCode}. Body: {Truncate(json, 500)}",
                        DeskLinkAuditLogLevel.Error,
                        nameof(StartBrowserAsync),
                        CreateProperties(
                            baseUrl,
                            hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                            userId: adsPowerUserId,
                            openUrl: openUrl,
                            httpStatusCode: (int)response.StatusCode));
                    throw new InvalidOperationException(
                        $"AdsPower browser/start: {(int)response.StatusCode} {Truncate(json, 500)}");
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                EnsureApiSuccess(
                    root,
                    baseUrl,
                    options,
                    nameof(StartBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId,
                        openUrl: openUrl));

                string? webSocketDebuggerUrl = null;
                string? debugPort = null;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("debug_port", out var debugPortProp))
                    {
                        debugPort = debugPortProp.GetString();
                    }

                    if (data.TryGetProperty("ws", out var ws) && ws.ValueKind == JsonValueKind.Object)
                    {
                        if (ws.TryGetProperty("puppeteer", out var puppeteerProp))
                        {
                            webSocketDebuggerUrl = puppeteerProp.GetString();
                        }
                    }
                }

                Log(
                    $"AdsPower browser/start completed successfully for profile {adsPowerUserId}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(StartBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId,
                        openUrl: openUrl));
                return new AdsPowerBrowserStartResult(webSocketDebuggerUrl, debugPort);
            },
            cancellationToken);
    }

    public Task StopBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        return ExecuteWithRateLimitRetryAsync(
            options,
            async ct =>
            {
                var baseUrl = NormalizeBaseUrl(options.BaseUrl);
                var url = $"{baseUrl}/api/v1/browser/stop?user_id={Uri.EscapeDataString(adsPowerUserId)}";
                Log(
                    $"AdsPower browser/stop request started for profile {adsPowerUserId}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(StopBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId));

                var (response, json) = await SendGetAsync(options, url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log(
                        $"AdsPower browser/stop failed with HTTP {(int)response.StatusCode}. Body: {Truncate(json, 500)}",
                        DeskLinkAuditLogLevel.Warning,
                        nameof(StopBrowserAsync),
                        CreateProperties(
                            baseUrl,
                            hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                            userId: adsPowerUserId,
                            httpStatusCode: (int)response.StatusCode));
                    return;
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    var code = codeProp.GetInt32();
                    if (code != 0)
                    {
                        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                        if (AdsPowerApiErrorClassifier.LooksLikeRateLimit(code, msg))
                        {
                            throw new AdsPowerRateLimitExceededException(code, msg);
                        }

                        Log(
                            $"AdsPower browser/stop returned API code {code}: {msg ?? "ошибка"} (профиль мог быть уже закрыт).",
                            DeskLinkAuditLogLevel.Warning,
                            nameof(StopBrowserAsync),
                            CreateProperties(
                                baseUrl,
                                hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                                userId: adsPowerUserId,
                                apiCode: code));
                        return;
                    }
                }

                Log(
                    $"AdsPower browser/stop completed for profile {adsPowerUserId}.",
                    DeskLinkAuditLogLevel.Info,
                    nameof(StopBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId));
            },
            cancellationToken);
    }

    private async Task<T> ExecuteWithRateLimitRetryAsync<T>(
        AdsPowerConnectionOptions options,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(options.BaseUrl);

        AdsPowerRateLimitExceededException? lastRateLimit = null;

        for (var attempt = 1; attempt <= RateLimitMaxAttempts; attempt++)
        {
            try
            {
                return await AdsPowerApiThrottler
                    .ExecuteAsync(baseUrl, action, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AdsPowerRateLimitExceededException ex)
            {
                lastRateLimit = ex;
                if (attempt >= RateLimitMaxAttempts)
                {
                    break;
                }

                var delaySeconds = Math.Min(30, Math.Pow(2, attempt));
                Log(
                    $"AdsPower rate limit (attempt {attempt}/{RateLimitMaxAttempts}): {ex.ApiMessage}. Повтор через {delaySeconds:F0} с.",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(ExecuteWithRateLimitRetryAsync),
                    new Dictionary<string, object?>
                    {
                        ["adsPower.baseUrl"] = baseUrl,
                        ["adsPower.apiCode"] = ex.ApiCode,
                        ["adsPower.apiMessage"] = ex.ApiMessage,
                        ["adsPower.rateLimitAttempt"] = attempt
                    },
                    AdsPowerRateLimitExceededException.ErrorKey);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastRateLimit
            ?? new InvalidOperationException("AdsPower: не удалось выполнить запрос после повторов при rate limit.");
    }

    private async Task ExecuteWithRateLimitRetryAsync(
        AdsPowerConnectionOptions options,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRateLimitRetryAsync(
                options,
                async ct =>
                {
                    await action(ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(HttpResponseMessage Response, string Json)> SendGetAsync(
        AdsPowerConnectionOptions options,
        string url,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request, options.ApiKey);
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response, json);
    }

    private static void EnsureApiSuccess(
        JsonElement root,
        string baseUrl,
        AdsPowerConnectionOptions options,
        string memberName,
        Dictionary<string, object?> props)
    {
        if (!root.TryGetProperty("code", out var codeProp) || codeProp.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var code = codeProp.GetInt32();
        if (code == 0)
        {
            return;
        }

        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
        props["adsPower.apiMessage"] = msg;
        props["adsPower.apiCode"] = code;

        if (AdsPowerDailyOpenLimitExceededException.LooksLikeDailyOpenLimit(code, msg))
        {
            Log(
                $"AdsPower API: дневной лимит запусков (API code {code}).",
                DeskLinkAuditLogLevel.Warning,
                memberName,
                props,
                AdsPowerDailyOpenLimitExceededException.ErrorKey);
            throw new AdsPowerDailyOpenLimitExceededException(code, msg);
        }

        if (AdsPowerApiErrorClassifier.LooksLikeRateLimit(code, msg))
        {
            Log(
                $"AdsPower API rate limit (code {code}): {msg}.",
                DeskLinkAuditLogLevel.Warning,
                memberName,
                props,
                AdsPowerRateLimitExceededException.ErrorKey);
            throw new AdsPowerRateLimitExceededException(code, msg);
        }

        Log(
            $"AdsPower API error {code}: {msg ?? "ошибка"}.",
            DeskLinkAuditLogLevel.Warning,
            memberName,
            props);
        throw new InvalidOperationException($"AdsPower: {msg ?? "ошибка"} (code {code})");
    }

    private static void AddAuthorizationHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var t = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(t))
        {
            throw new ArgumentException("Укажите базовый URL Local API AdsPower.", nameof(baseUrl));
        }

        return t;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s;
        }

        return s[..max] + "…";
    }

    private static Dictionary<string, object?> CreateProperties(
        string baseUrl,
        bool hasApiKey,
        string? userId = null,
        string? openUrl = null,
        int? httpStatusCode = null,
        int? apiCode = null,
        int? profileCount = null)
    {
        return new Dictionary<string, object?>
        {
            ["adsPower.baseUrl"] = baseUrl,
            ["adsPower.hasApiKey"] = hasApiKey,
            ["adsPower.userId"] = userId,
            ["adsPower.openUrl"] = openUrl,
            ["adsPower.httpStatusCode"] = httpStatusCode,
            ["adsPower.apiCode"] = apiCode,
            ["adsPower.profileCount"] = profileCount
        };
    }

    private static void Log(
        string message,
        DeskLinkAuditLogLevel level,
        string memberName,
        Dictionary<string, object?>? properties = null,
        string? errorKey = null)
    {
        try
        {
            _ = GlobalLogger.Instance.LogAsync(
                message,
                level,
                memberName: memberName,
                filePath: "AdsPowerApiClient.cs",
                errorKey: errorKey,
                properties: properties);
        }
        catch
        {
            // Логирование не должно ломать вызов API.
        }
    }
}