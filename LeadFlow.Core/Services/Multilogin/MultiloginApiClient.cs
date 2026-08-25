using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// HTTP-клиент launcher Multilogin X только для подтверждённых start/stop.
/// Не вызывает cloud API, profile/search и неподтверждённые пути.
/// </summary>
public sealed class MultiloginApiClient(IHttpClientFactory httpClientFactory) : IMultiloginApiClient
{
    public async Task<MultiloginBrowserStartResult> StartProfileAsync(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var normalized = options.Normalized();
        var origin = ResolveLauncherOrigin(normalized.LauncherUrl);
        var token = RequireAutomationToken(normalized);
        var url =
            $"{origin}/api/v2/profile/f/{Uri.EscapeDataString(folderId.Trim())}/p/{Uri.EscapeDataString(profileId.Trim())}/start?automation_type=puppeteer&headless_mode=false";

        var (statusCode, isSuccess, json) = await SendGetAsync(token, url, cancellationToken)
            .ConfigureAwait(false);
        if (!isSuccess)
        {
            throw new InvalidOperationException(
                FormatHttpError("Multilogin profile/start", statusCode, json, token));
        }

        return MultiloginBrowserStartResult.FromPort(ParsePort(json));
    }

    public async Task StopProfileAsync(
        MultiloginConnectionOptions options,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var normalized = options.Normalized();
        var origin = ResolveLauncherOrigin(normalized.LauncherUrl);
        var token = RequireAutomationToken(normalized);
        var url = $"{origin}/api/v1/profile/stop/p/{Uri.EscapeDataString(profileId.Trim())}";

        var (statusCode, isSuccess, json) = await SendGetAsync(token, url, cancellationToken)
            .ConfigureAwait(false);
        if (!isSuccess)
        {
            throw new InvalidOperationException(
                FormatHttpError("Multilogin profile/stop", statusCode, json, token));
        }
    }

    private async Task<(int StatusCode, bool IsSuccess, string Json)> SendGetAsync(
        string token,
        string url,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, response.IsSuccessStatusCode, json);
    }

    private static string ResolveLauncherOrigin(string? launcherUrl)
    {
        var normalized = MultiloginUrl.Normalize(launcherUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Укажите URL launcher Multilogin X.", nameof(launcherUrl));
        }

        if (normalized.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/api/v2".Length];
        }

        return normalized.TrimEnd('/');
    }

    private static string RequireAutomationToken(MultiloginConnectionOptions options)
    {
        if (!options.HasAutomationToken)
        {
            throw new InvalidOperationException("Укажите automation token Multilogin X.");
        }

        return options.AutomationToken!.Trim();
    }

    private static int ParsePort(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("port", out var portProp))
            {
                throw new InvalidOperationException(
                    "Multilogin profile/start: в ответе нет data.port.");
            }

            var port = portProp.ValueKind switch
            {
                JsonValueKind.Number when portProp.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(portProp.GetString(), out var parsed) => parsed,
                _ => throw new InvalidOperationException(
                    "Multilogin profile/start: data.port имеет неверный тип.")
            };

            return port;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Multilogin profile/start: некорректный JSON.", ex);
        }
    }

    private static string FormatHttpError(string operation, int statusCode, string body, string? token) =>
        $"{operation}: {statusCode} {Truncate(SanitizeErrorBody(body, token), 500)}";

    private static string SanitizeErrorBody(string? body, string? token)
    {
        var result = body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return result;
        }

        result = Regex.Replace(
            result,
            @"bearer\s+" + Regex.Escape(token),
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return result.Replace(token, string.Empty, StringComparison.Ordinal);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "…";
    }
}
