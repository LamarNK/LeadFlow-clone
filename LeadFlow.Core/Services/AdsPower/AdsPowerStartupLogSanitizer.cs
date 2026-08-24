using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadFlow.Core.Logging.Audit;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Allowlist-санитизация Local API / CDP диагностик старта AdsPower.
/// Не кладёт в свойства полный WebSocket URL, proxy, cookies, tokens, raw JSON/HTML.
/// </summary>
internal static class AdsPowerStartupLogSanitizer
{
    public const int MaxMessageLength = 160;
    public const int MaxDataKeys = 16;
    public const int MaxSerializedBlobLength = 2048;

    internal const string BrowserStartOperation = "browser/start";

    private static readonly HashSet<string> DataKeyAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "ws",
        "debug_port",
        "webdriver"
    };

    private static readonly Regex EmailRegex = new(
        @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex AnyUrlRegex = new(
        @"(?i)(?:https?|wss?|socks5?)://[^\s<>""']+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)bearer\s+\S+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex SessionCookieRegex = new(
        @"(?i)(sessionid|sid|cookie)\s*[:=]\s*[^\s;]+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public static AdsPowerLocalApiStartSnapshot SummarizeBrowserStart(
        string? payload,
        int? httpStatusCode,
        Exception? transportException,
        TimeSpan duration,
        string? parsedWebSocketUrl,
        string? debugPort,
        string? openUrl)
    {
        var payloadKind = ClassifyPayload(payload);
        var payloadLength = payload?.Length ?? 0;
        int? adsPowerCode = null;
        string? adsPowerMessage = null;
        var hasWsPuppeteer = false;
        var debugPortPresent = !string.IsNullOrWhiteSpace(debugPort);
        string dataKind = "missing";
        int? dataKeyCount = null;
        string? dataKeys = null;

        if (payloadKind == "json" && !string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                adsPowerCode = ReadInt(root, "code");
                adsPowerMessage = LimitText(ReadStringish(root, "msg") ?? ReadStringish(root, "message"));
                if (root.TryGetProperty("data", out var data))
                {
                    dataKind = DescribeValueKind(data.ValueKind);
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        var (totalKeys, allowlistedKeys) = ReadAllowlistedDataKeys(data);
                        dataKeyCount = totalKeys;
                        dataKeys = allowlistedKeys;
                        debugPortPresent = debugPortPresent
                                           || data.TryGetProperty("debug_port", out var debugPortProp)
                                           && debugPortProp.ValueKind is not JsonValueKind.Null
                                           and not JsonValueKind.Undefined
                                           && !string.IsNullOrWhiteSpace(debugPortProp.ToString());
                        hasWsPuppeteer = HasWsPuppeteer(data);
                    }
                }
            }
            catch (JsonException)
            {
                payloadKind = payloadKind == "html" ? "html" : "text";
            }
        }

        if (!hasWsPuppeteer)
        {
            hasWsPuppeteer = !string.IsNullOrWhiteSpace(parsedWebSocketUrl);
        }

        var ok = transportException is null
                 && httpStatusCode is >= 200 and < 300
                 && (adsPowerCode is null or 0)
                 && hasWsPuppeteer;

        return new AdsPowerLocalApiStartSnapshot(
            HttpStatusCode: httpStatusCode,
            TransportExceptionType: transportException is null ? null : DescribeExceptionType(transportException),
            AdsPowerCode: adsPowerCode,
            AdsPowerMessage: adsPowerMessage,
            HasWsPuppeteer: hasWsPuppeteer,
            DataKind: dataKind,
            DataKeyCount: dataKeyCount,
            DataKeys: dataKeys,
            PayloadKind: payloadKind,
            PayloadLength: payloadLength,
            Ws: DescribeWsEndpoint(parsedWebSocketUrl),
            DebugPortPresent: debugPortPresent,
            OpenUrlClass: string.IsNullOrWhiteSpace(openUrl)
                ? null
                : AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(openUrl),
            DurationMs: duration.TotalMilliseconds,
            Ok: ok);
    }

    public static AdsPowerWsEndpointMetadata? DescribeWsEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var raw = url.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return new AdsPowerWsEndpointMetadata(
                Scheme: "invalid",
                HostClass: "invalid",
                Port: null,
                PathClass: "invalid",
                HasQuery: raw.Contains('?'),
                QueryKeyCount: 0,
                UrlLength: raw.Length,
                HasUserInfo: raw.Contains('@'));
        }

        var queryKeyCount = 0;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            queryKeyCount = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }

        return new AdsPowerWsEndpointMetadata(
            Scheme: string.IsNullOrEmpty(uri.Scheme) ? "empty" : uri.Scheme.ToLowerInvariant(),
            HostClass: ClassifyHost(uri),
            Port: uri.IsDefaultPort ? null : uri.Port,
            PathClass: ClassifyWsPath(uri.AbsolutePath),
            HasQuery: !string.IsNullOrEmpty(uri.Query),
            QueryKeyCount: queryKeyCount,
            UrlLength: raw.Length,
            HasUserInfo: !string.IsNullOrEmpty(uri.UserInfo));
    }

    public static string ExternalDetail(string? apiMessage, string fallback)
    {
        var safe = LimitText(apiMessage);
        return string.IsNullOrEmpty(safe) ? fallback : safe;
    }

    public static string LimitText(string? text, int maxLength = MaxMessageLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var s = LogSanitizer.RedactSensitivePatterns(text, maxLen: Math.Max(maxLength, 256));
        try
        {
            s = RedactUrls(s);
            s = BearerTokenRegex.Replace(s, "Bearer ***");
            s = SessionCookieRegex.Replace(s, "$1=***");
            s = EmailRegex.Replace(s, "***@***");
        }
        catch (RegexMatchTimeoutException)
        {
            // оставляем уже redact'нутую строку
        }

        if (s.Length <= maxLength)
        {
            return s;
        }

        return s[..maxLength] + "…";
    }

    public static string DescribeExceptionType(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    public static string ClassifyPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "empty";
        }

        var trimmed = payload.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal)
            || trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return "html";
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return "json";
        }

        return "text";
    }

    public static Dictionary<string, object?> ToProperties(AdsPowerLocalApiStartSnapshot snapshot)
    {
        var props = new Dictionary<string, object?>
        {
            ["startup.boundary"] = "local_api",
            ["startup.event"] = "browser_start",
            ["localApi.durationMs"] = snapshot.DurationMs,
            ["localApi.httpStatus"] = snapshot.HttpStatusCode,
            ["localApi.transportExceptionType"] = snapshot.TransportExceptionType,
            ["localApi.adsPowerCode"] = snapshot.AdsPowerCode,
            ["localApi.adsPowerMessage"] = snapshot.AdsPowerMessage,
            ["localApi.hasWsPuppeteer"] = snapshot.HasWsPuppeteer,
            ["localApi.dataKind"] = snapshot.DataKind,
            ["localApi.dataKeyCount"] = snapshot.DataKeyCount,
            ["localApi.dataKeys"] = snapshot.DataKeys,
            ["localApi.payloadKind"] = snapshot.PayloadKind,
            ["localApi.payloadLength"] = snapshot.PayloadLength,
            ["localApi.debugPortPresent"] = snapshot.DebugPortPresent,
            ["localApi.openUrlClass"] = snapshot.OpenUrlClass,
            ["localApi.ok"] = snapshot.Ok
        };

        if (snapshot.Ws is { } ws)
        {
            props["localApi.ws.scheme"] = ws.Scheme;
            props["localApi.ws.hostClass"] = ws.HostClass;
            props["localApi.ws.port"] = ws.Port;
            props["localApi.ws.pathClass"] = ws.PathClass;
            props["localApi.ws.hasQuery"] = ws.HasQuery;
            props["localApi.ws.queryKeyCount"] = ws.QueryKeyCount;
            props["localApi.ws.urlLength"] = ws.UrlLength;
            props["localApi.ws.hasUserInfo"] = ws.HasUserInfo;
        }

        return props;
    }

    public static void CopySafeEndpointProperties(
        Dictionary<string, object?> properties,
        string prefix,
        string? url)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var meta = DescribeWsEndpoint(url);
        if (meta is null)
        {
            return;
        }

        properties[$"{prefix}.scheme"] = meta.Scheme;
        properties[$"{prefix}.hostClass"] = meta.HostClass;
        properties[$"{prefix}.port"] = meta.Port;
        properties[$"{prefix}.pathClass"] = meta.PathClass;
        properties[$"{prefix}.hasQuery"] = meta.HasQuery;
        properties[$"{prefix}.queryKeyCount"] = meta.QueryKeyCount;
        properties[$"{prefix}.urlLength"] = meta.UrlLength;
        properties[$"{prefix}.hasUserInfo"] = meta.HasUserInfo;
    }

    public static string FormatLocalApiExceptionHint(AdsPowerLocalApiStartSnapshot snapshot)
    {
        var code = snapshot.AdsPowerCode is { } apiCode ? $" code {apiCode}" : string.Empty;
        var msg = string.IsNullOrWhiteSpace(snapshot.AdsPowerMessage)
            ? string.Empty
            : $" {snapshot.AdsPowerMessage}";
        var transport = string.IsNullOrWhiteSpace(snapshot.TransportExceptionType)
            ? string.Empty
            : $" {snapshot.TransportExceptionType}";
        var status = snapshot.HttpStatusCode is { } http ? http.ToString() : "transport";
        return $"{status}{code}{transport}{msg}".Trim();
    }

    public static bool ContainsForbiddenSecret(string? blob, params string[] secrets)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return false;
        }

        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret)
                && blob.Contains(secret, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string SerializeForInspection(IReadOnlyDictionary<string, object?> properties)
    {
        try
        {
            var json = JsonSerializer.Serialize(properties);
            return json.Length <= MaxSerializedBlobLength
                ? json
                : json[..MaxSerializedBlobLength];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string RedactUrls(string text)
    {
        return AnyUrlRegex.Replace(text, match =>
        {
            var value = match.Value;
            var trimmed = value.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
            var suffix = value[trimmed.Length..];
            var cls = AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(trimmed);
            return $"<url:{cls}>{suffix}";
        });
    }

    private static (int Total, string? Allowlisted) ReadAllowlistedDataKeys(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return (0, null);
        }

        var total = 0;
        var allowed = new List<string>();
        foreach (var property in data.EnumerateObject())
        {
            total++;
            if (!DataKeyAllowlist.Contains(property.Name) || allowed.Count >= MaxDataKeys)
            {
                continue;
            }

            allowed.Add(property.Name);
        }

        return (total, allowed.Count == 0 ? null : string.Join(",", allowed));
    }

    private static bool HasWsPuppeteer(JsonElement data)
    {
        if (!data.TryGetProperty("ws", out var ws) || ws.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!ws.TryGetProperty("puppeteer", out var puppeteer))
        {
            return false;
        }

        return puppeteer.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(puppeteer.GetString());
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadStringish(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string DescribeValueKind(JsonValueKind kind) =>
        kind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "other"
        };

    private static string ClassifyHost(Uri uri)
    {
        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return "empty";
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost.", StringComparison.OrdinalIgnoreCase))
        {
            return "localhost";
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return "loopback";
            }

            return address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        }

        return "hostname";
    }

    private static string ClassifyWsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "empty";
        }

        if (path.Contains("/devtools/browser", StringComparison.OrdinalIgnoreCase))
        {
            return "devtools-browser";
        }

        if (path.Contains("/devtools/page", StringComparison.OrdinalIgnoreCase))
        {
            return "devtools-page";
        }

        return "other";
    }
}

internal sealed record AdsPowerLocalApiStartSnapshot(
    int? HttpStatusCode,
    string? TransportExceptionType,
    int? AdsPowerCode,
    string? AdsPowerMessage,
    bool HasWsPuppeteer,
    string DataKind,
    int? DataKeyCount,
    string? DataKeys,
    string PayloadKind,
    int PayloadLength,
    AdsPowerWsEndpointMetadata? Ws,
    bool DebugPortPresent,
    string? OpenUrlClass,
    double DurationMs,
    bool Ok)
{
    public bool ApiSucceeded =>
        TransportExceptionType is null
        && HttpStatusCode is >= 200 and < 300
        && AdsPowerCode is null or 0;
}

internal sealed record AdsPowerWsEndpointMetadata(
    string Scheme,
    string HostClass,
    int? Port,
    string PathClass,
    bool HasQuery,
    int QueryKeyCount,
    int UrlLength,
    bool HasUserInfo);
