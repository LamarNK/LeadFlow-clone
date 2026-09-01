using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// HTTP-клиент Multilogin X: launcher start/stop и POST /profile/search.
/// </summary>
public sealed class MultiloginApiClient(IHttpClientFactory httpClientFactory) : IMultiloginApiClient
{
    public const int ProfileSearchPageSize = 100;
    public const int ProfileSearchMaxPages = 50;

    private static readonly JsonSerializerOptions SearchRequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

    public async Task<MultiloginProfileSearchResult> SearchProfilesAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalized = options.Normalized();
        var origin = ResolveCloudOrigin(normalized.CloudApiUrl);
        var token = RequireAutomationToken(normalized);
        var url = $"{origin}/profile/search";
        var results = new List<MultiloginProfileSummary>();
        var discarded = 0;
        var fetchedRaw = 0;

        for (var page = 0; page < ProfileSearchMaxPages; page++)
        {
            var offset = page * ProfileSearchPageSize;
            var body = JsonSerializer.Serialize(
                new ProfileSearchRequestBody(
                    IsRemoved: false,
                    Limit: ProfileSearchPageSize,
                    Offset: offset,
                    SearchText: "",
                    StorageType: "all",
                    OrderBy: "created_at",
                    Sort: "asc"),
                SearchRequestJson);

            var (statusCode, isSuccess, json) = await SendPostAsync(token, url, body, cancellationToken)
                .ConfigureAwait(false);
            if (!isSuccess)
            {
                throw new InvalidOperationException(
                    FormatHttpError("Multilogin profile/search", statusCode, json, token));
            }

            var parsed = ParseSearchPage(json);
            discarded += parsed.DiscardedCount;
            fetchedRaw += parsed.RawCount;
            results.AddRange(parsed.Profiles);

            if (parsed.RawCount == 0)
            {
                if (offset == 0 && parsed.TotalCount == 0 && discarded == 0)
                {
                    return MultiloginProfileSearchResult.EmptyComplete;
                }

                throw SearchCatalogError("неполный или некорректный каталог.");
            }

            var fetchedAll = fetchedRaw >= parsed.TotalCount
                || offset + ProfileSearchPageSize >= parsed.TotalCount;
            if (!fetchedAll)
            {
                continue;
            }

            if (results.Count == 0)
            {
                throw SearchCatalogError("профили без id или folder_id.");
            }

            return new MultiloginProfileSearchResult(results, IsComplete: discarded == 0);
        }

        throw SearchCatalogError("каталог неполный.");
    }

    public async Task ProbeLauncherAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var origin = ResolveLauncherOrigin(options.Normalized().LauncherUrl);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin);
        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _ = response.StatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Launcher Multilogin недоступен.", ex);
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

    private async Task<(int StatusCode, bool IsSuccess, string Json)> SendPostAsync(
        string token,
        string url,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

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

    private static string ResolveCloudOrigin(string? cloudApiUrl) =>
        MultiloginUrl.Normalize(cloudApiUrl) ?? MultiloginWorkerSettings.DefaultCloudApiUrl;

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

    private static ParsedSearchPage ParseSearchPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                throw SearchCatalogError("нет data.");
            }

            if (!data.TryGetProperty("total_count", out var totalProp)
                || totalProp.ValueKind != JsonValueKind.Number
                || !totalProp.TryGetInt32(out var totalCount)
                || totalCount < 0)
            {
                throw SearchCatalogError("нет total_count.");
            }

            if (!data.TryGetProperty("profiles", out var profilesProp)
                || profilesProp.ValueKind != JsonValueKind.Array)
            {
                throw SearchCatalogError("нет profiles.");
            }

            var rawCount = profilesProp.GetArrayLength();
            if (totalCount == 0 && rawCount != 0)
            {
                throw SearchCatalogError("несогласованный каталог.");
            }

            var profiles = new List<MultiloginProfileSummary>();
            var discarded = 0;
            foreach (var item in profilesProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    discarded++;
                    continue;
                }

                var id = ReadString(item, "id");
                var folderId = ReadString(item, "folder_id");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(folderId))
                {
                    discarded++;
                    continue;
                }

                profiles.Add(new MultiloginProfileSummary(
                    id.Trim(),
                    folderId.Trim(),
                    ReadString(item, "name")?.Trim() ?? string.Empty));
            }

            return new ParsedSearchPage(profiles, rawCount, discarded, totalCount);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Multilogin profile/search: некорректный JSON.", ex);
        }
    }

    private static InvalidOperationException SearchCatalogError(string reason) =>
        new("Multilogin profile/search: " + reason);

    private readonly record struct ParsedSearchPage(
        IReadOnlyList<MultiloginProfileSummary> Profiles,
        int RawCount,
        int DiscardedCount,
        int TotalCount);

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null
        };
    }

    private static string FormatHttpError(string operation, int statusCode, string body, string? token) =>
        $"{operation}: {statusCode} {Truncate(SanitizeErrorBody(body, token), 500)}";

    private static string SanitizeErrorBody(string? body, string? token)
    {
        var result = body ?? string.Empty;
        result = Regex.Replace(
            result,
            "\"(password|username|proxy|token)\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"",
            "\"$1\":\"\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

    private sealed record ProfileSearchRequestBody(
        bool IsRemoved,
        int Limit,
        int Offset,
        string SearchText,
        string StorageType,
        string OrderBy,
        string Sort);
}
