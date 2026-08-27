using System.Net.Http.Headers;
using System.Text.Json;

namespace Orbita.Api.Services;

/// <summary>
/// Обменивает короткоживущий API token из приложения Multilogin на automation token.
/// Исходный API token не сохраняется.
/// </summary>
public interface IMultiloginAutomationTokenIssuer
{
    Task<string> IssueAsync(
        string? cloudApiUrl,
        string apiToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Получает долгоживущий automation token для workspace владельца Multilogin.
/// </summary>
public sealed class MultiloginAutomationTokenIssuer(HttpClient httpClient)
    : IMultiloginAutomationTokenIssuer
{
    public const string ExpirationPeriod = "no_exp";
    private const string DefaultCloudApiUrl = "https://api.multilogin.com";

    public async Task<string> IssueAsync(
        string? cloudApiUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var origin = string.IsNullOrWhiteSpace(cloudApiUrl)
            ? DefaultCloudApiUrl
            : cloudApiUrl.Trim().TrimEnd('/');
        var url = $"{origin}/workspace/automation_token?expiration_period={ExpirationPeriod}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());

        using var response = await httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Multilogin не выдал automation token (HTTP {(int)response.StatusCode}).");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(token.GetString()))
            {
                return token.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // The user-facing error below must not include the response body or source token.
        }

        throw new InvalidOperationException("Multilogin вернул automation token в неизвестном формате.");
    }
}
