using System.Net.Http;

namespace LeadFlow.Services;

/// <summary>
/// Фоновая проверка удалённого флага: недоступность URL или тело не равное <c>0</c> — работа продолжается;
/// при успешном ответе с телом <c>0</c> приложение должно завершиться.
/// </summary>
internal static class RemoteReleaseGate
{
    private static readonly Uri CheckUri = new(
        "https://24pablo.ru/share/uploads/eyJQYXRoIjoicmVwb3MvTGVhZEZsb3cudHh0IiwiRXhwaXJlc0F0VXRjIjoiMjAyNi0wNS0xN1QxMjoyOToxNC42MzI2OTE1WiJ9");

    /// <returns><see langword="true"/>, если нужно завершить приложение.</returns>
    public static async Task<bool> IsRevokedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var request = new HttpRequestMessage(HttpMethod.Get, CheckUri);
            request.Headers.TryAddWithoutValidation("User-Agent", "LeadFlow");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(body.Trim(), "0", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
