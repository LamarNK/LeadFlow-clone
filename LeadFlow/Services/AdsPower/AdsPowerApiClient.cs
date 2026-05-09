using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LeadFlow.Logging.Audit;

namespace LeadFlow.Services.AdsPower;

public sealed class AdsPowerApiClient(IHttpClientFactory httpClientFactory) : IAdsPowerApiClient
{
    public async Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient();
        var baseUrl = NormalizeBaseUrl(options.BaseUrl);
        var url = $"{baseUrl}/api/v1/user/list?page=1&page_size=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request, options.ApiKey);
        Log(
            $"AdsPower list request started for {baseUrl}.",
            DeskLinkAuditLogLevel.Info,
            nameof(ListProfilesAsync),
            CreateProperties(baseUrl, hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey)));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
        {
            var code = codeProp.GetInt32();
            if (code != 0)
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                Log(
                    $"AdsPower list request returned API error {code}: {msg ?? "ошибка list"}.",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(ListProfilesAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        apiCode: code));
                throw new InvalidOperationException($"AdsPower: {msg ?? "ошибка list"} (code {code})");
            }
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return [];
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
    }

    public async Task<AdsPowerBrowserStartResult> StartBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string? openUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        var client = httpClientFactory.CreateClient();
        var baseUrl = NormalizeBaseUrl(options.BaseUrl);
        var q = $"user_id={Uri.EscapeDataString(adsPowerUserId)}";
        if (!string.IsNullOrWhiteSpace(openUrl))
        {
            var openUrlsJson = JsonSerializer.Serialize(new[] { openUrl });
            q += "&open_urls=" + Uri.EscapeDataString(openUrlsJson);
        }

        var url = $"{baseUrl}/api/v1/browser/start?{q}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request, options.ApiKey);
        Log(
            $"AdsPower browser/start request started for profile {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            nameof(StartBrowserAsync),
            CreateProperties(
                baseUrl,
                hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                userId: adsPowerUserId,
                openUrl: openUrl));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
        {
            var code = codeProp.GetInt32();
            if (code != 0)
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                Log(
                    $"AdsPower browser/start returned API error {code}: {msg ?? "ошибка"}.",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(StartBrowserAsync),
                    CreateProperties(
                        baseUrl,
                        hasApiKey: !string.IsNullOrWhiteSpace(options.ApiKey),
                        userId: adsPowerUserId,
                        openUrl: openUrl,
                        apiCode: code));
                throw new InvalidOperationException($"AdsPower browser/start: {msg ?? "ошибка"} (code {code})");
            }
        }

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
        Dictionary<string, object?>? properties = null)
    {
        try
        {
            _ = GlobalLogger.Instance.LogAsync(
                message,
                level,
                memberName: memberName,
                filePath: "AdsPowerApiClient.cs",
                properties: properties);
        }
        catch
        {
            // Логирование не должно ломать вызов API.
        }
    }
}
