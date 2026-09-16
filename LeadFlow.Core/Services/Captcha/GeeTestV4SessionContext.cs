using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// Динамический контекст одного запуска GeeTest v4. Значения challenge/risk type используются
/// только в памяти и не должны передаваться в логи или статистику в открытом виде.
/// </summary>
public sealed record GeeTestV4SessionContext(
    string? CaptchaId,
    string? Challenge,
    string? RiskType,
    string Source,
    DateTime CapturedAtUtc)
{
    public bool HasChallenge => !string.IsNullOrWhiteSpace(Challenge);
    public bool HasRiskType => !string.IsNullOrWhiteSpace(RiskType);

    /// <summary>
    /// Контекст дополнен телом ответа GeeTest/firewall. Актуальный challenge выдаётся сервером в response:
    /// challenge из request-стадии почти всегда отклоняется целевым сайтом при verify.
    /// </summary>
    public bool HasCapturedResponse => Source.Contains("response", StringComparison.OrdinalIgnoreCase);

    public string Fingerprint
    {
        get
        {
            var material = string.Join('\n', CaptchaId ?? string.Empty, Challenge ?? string.Empty, RiskType ?? string.Empty);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }
    }

    public CaptchaContextDiagnostics ToDiagnostics(DateTime atUtc) => new(
        Source,
        Fingerprint,
        HasChallenge,
        HasRiskType,
        Math.Max(0, (int)Math.Min(int.MaxValue, (atUtc - CapturedAtUtc).TotalMilliseconds)));
}

public sealed record CaptchaContextDiagnostics(
    string Source,
    string Fingerprint,
    bool ChallengePresent,
    bool RiskTypePresent,
    int ContextAgeMs);

public sealed class GeeTestV4AttemptContextTracker
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public bool TryUse(GeeTestV4SessionContext context) => _used.Add(context.Fingerprint);

    public static bool IsCurrent(GeeTestV4SessionContext submitted, GeeTestV4SessionContext? observed) =>
        observed is null || string.Equals(submitted.Fingerprint, observed.Fingerprint, StringComparison.Ordinal);
}

/// <summary>Извлекает параметры GeeTest из URL, form body и JSON/JSONP без сохранения исходного payload.</summary>
public static class GeeTestV4SessionContextParser
{
    public static GeeTestV4SessionContext? Parse(
        string? url,
        string? postData,
        string? responseBody,
        string source,
        DateTime capturedAtUtc)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ParseUrl(url, values);
        ParsePayload(postData, values);
        ParsePayload(responseBody, values);

        values.TryGetValue("captchaid", out var captchaId);
        values.TryGetValue("challenge", out var challenge);
        values.TryGetValue("risktype", out var riskType);
        if (string.IsNullOrWhiteSpace(captchaId)
            && string.IsNullOrWhiteSpace(challenge)
            && string.IsNullOrWhiteSpace(riskType))
        {
            return null;
        }

        return new GeeTestV4SessionContext(
            NullIfWhiteSpace(captchaId),
            NullIfWhiteSpace(challenge),
            NullIfWhiteSpace(riskType),
            string.IsNullOrWhiteSpace(source) ? "network" : source.Trim(),
            capturedAtUtc);
    }

    public static GeeTestV4SessionContext Merge(
        GeeTestV4SessionContext? current,
        GeeTestV4SessionContext incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        return new GeeTestV4SessionContext(
            Prefer(incoming.CaptchaId, current.CaptchaId),
            Prefer(incoming.Challenge, current.Challenge),
            Prefer(incoming.RiskType, current.RiskType),
            string.Equals(current.Source, incoming.Source, StringComparison.Ordinal)
                ? current.Source
                : $"{current.Source}+{incoming.Source}",
            incoming.CapturedAtUtc > current.CapturedAtUtc ? incoming.CapturedAtUtc : current.CapturedAtUtc);
    }

    public static bool IsRelevantUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("firewallCaptcha/get", StringComparison.OrdinalIgnoreCase)
               || url.Contains("geetest", StringComparison.OrdinalIgnoreCase)
               || url.Contains("gcaptcha4", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseUrl(string? url, IDictionary<string, string> values)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        ParseForm(uri.Query.TrimStart('?'), values);
    }

    private static void ParsePayload(string? payload, IDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        ParseForm(payload, values);
        var candidate = payload.Trim();
        if (!candidate.StartsWith('{') && !candidate.StartsWith('['))
        {
            var open = candidate.IndexOf('(');
            var close = candidate.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                candidate = candidate[(open + 1)..close].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            Visit(document.RootElement, values);
        }
        catch (JsonException)
        {
            // Payload может быть обычной form-urlencoded строкой; она уже разобрана выше.
        }
    }

    private static void Visit(JsonElement element, IDictionary<string, string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = NormalizeKey(property.Name);
                if (IsKnownKey(normalized) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                    Add(values, normalized, value);
                }

                else if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var nested = property.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(nested)
                        && (nested.StartsWith('{') || nested.StartsWith('[') || nested.Contains("captcha_id=", StringComparison.OrdinalIgnoreCase)))
                    {
                        ParsePayload(nested, values);
                    }
                }

                Visit(property.Value, values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Visit(child, values);
            }
        }
    }

    private static void ParseForm(string payload, IDictionary<string, string> values)
    {
        foreach (var part in payload.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = NormalizeKey(Decode(part[..equals]));
            if (IsKnownKey(key))
            {
                Add(values, key, Decode(part[(equals + 1)..]));
            }
        }
    }

    private static void Add(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private static bool IsKnownKey(string key) => key is "captchaid" or "challenge" or "risktype";

    private static string NormalizeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Краткоживущий CDP-сборщик параметров текущего запуска GeeTest.</summary>
public sealed class GeeTestV4NetworkContextCapture : IAsyncDisposable
{
    private readonly ICDPSession _client;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _requests = new(StringComparer.Ordinal);
    private GeeTestV4SessionContext? _context;
    private int _disposed;

    private GeeTestV4NetworkContextCapture(ICDPSession client) => _client = client;

    public static async Task<GeeTestV4NetworkContextCapture> StartAsync(IPage page)
    {
        var client = await page.CreateCDPSessionAsync().ConfigureAwait(false);
        var capture = new GeeTestV4NetworkContextCapture(client);
        client.MessageReceived += capture.HandleMessageReceived;
        try
        {
            await client.SendAsync("Network.enable").ConfigureAwait(false);
            return capture;
        }
        catch
        {
            client.MessageReceived -= capture.HandleMessageReceived;
            await client.DetachAsync().ConfigureAwait(false);
            throw;
        }
    }

    public GeeTestV4SessionContext? Snapshot()
    {
        lock (_sync)
        {
            return _context;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _context = null;
            _requests.Clear();
        }
    }

    public async Task<GeeTestV4SessionContext?> WaitForContextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Snapshot();
            if (snapshot is { CaptchaId: not null } && (snapshot.HasChallenge || snapshot.HasRiskType))
            {
                return snapshot;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return Snapshot();
    }

    /// <summary>
    /// Ждёт контекст, дополненный телом ответа (актуальный challenge приходит в response).
    /// После таймаута возвращает лучший доступный снимок — request-only контекст.
    /// </summary>
    public async Task<GeeTestV4SessionContext?> WaitForResponseContextAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Snapshot();
            if (snapshot is { CaptchaId: not null } && snapshot.HasCapturedResponse)
            {
                return snapshot;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return Snapshot();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.MessageReceived -= HandleMessageReceived;
        try { await _client.SendAsync("Network.disable").ConfigureAwait(false); } catch { }
        try { await _client.DetachAsync().ConfigureAwait(false); } catch { }
    }

    private void HandleMessageReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            if (string.Equals(e.MessageID, "Network.requestWillBeSent", StringComparison.Ordinal))
            {
                ObserveRequest(e.MessageData);
            }
            else if (string.Equals(e.MessageID, "Network.loadingFinished", StringComparison.Ordinal))
            {
                _ = ObserveResponseBodyAsync(e.MessageData);
            }
        }
        catch
        {
            // Диагностика не должна ломать прохождение капчи.
        }
    }

    private void ObserveRequest(JsonElement data)
    {
        var requestId = data.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        if (!data.TryGetProperty("request", out var request)
            || !request.TryGetProperty("url", out var urlProperty))
        {
            return;
        }

        var url = urlProperty.GetString();
        if (!GeeTestV4SessionContextParser.IsRelevantUrl(url))
        {
            return;
        }

        var postData = request.TryGetProperty("postData", out var post) ? post.GetString() : null;
        if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(url))
        {
            lock (_sync) { _requests[requestId] = url; }
        }

        Observe(GeeTestV4SessionContextParser.Parse(url, postData, null, SourceFor(url!, "request"), DateTime.UtcNow));
    }

    private async Task ObserveResponseBodyAsync(JsonElement data)
    {
        var requestId = data.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        string? url;
        lock (_sync) { _requests.TryGetValue(requestId, out url); }
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var response = await _client.SendAsync("Network.getResponseBody", new { requestId }).ConfigureAwait(false);
            if (response is null || !response.Value.TryGetProperty("body", out var bodyProperty))
            {
                return;
            }

            var body = bodyProperty.GetString();
            if (response.Value.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True)
            {
                body = string.IsNullOrWhiteSpace(body) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(body));
            }

            Observe(GeeTestV4SessionContextParser.Parse(url, null, body, SourceFor(url, "response"), DateTime.UtcNow));
        }
        catch
        {
            // Response body мог быть уже выгружен из CDP cache.
        }
        finally
        {
            lock (_sync) { _requests.Remove(requestId); }
        }
    }

    private void Observe(GeeTestV4SessionContext? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        lock (_sync)
        {
            _context = GeeTestV4SessionContextParser.Merge(_context, candidate);
        }
    }

    private static string SourceFor(string url, string direction) =>
        url.Contains("firewallCaptcha/get", StringComparison.OrdinalIgnoreCase)
            ? $"firewall_{direction}"
            : $"geetest_{direction}";
}
