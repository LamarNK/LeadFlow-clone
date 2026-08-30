using LeadFlow.Core.Services.Worker;

namespace Orbita.Tests;

public sealed class BrowserProviderProbeSanitizerTests
{
    private const string Token = "mlx-secret-token-value";
    private const string ApiKey = "ads-power-api-key-9f3a";

    [Fact]
    public void Sanitize_StripsBearerSecretAndCredentialUrl()
    {
        var raw =
            $"GET failed Authorization: Bearer {Token} from https://user:{ApiKey}@launcher.mlx.yt:45001/api token={Token}";
        var sanitized = BrowserProviderProbeSanitizer.Sanitize(raw, Token, ApiKey);

        Assert.DoesNotContain(Token, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user:", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("launcher.mlx.yt", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_TruncatesLongMessage()
    {
        var raw = new string('a', BrowserProviderProbeSanitizer.MaxMessageLength + 40);
        var sanitized = BrowserProviderProbeSanitizer.Sanitize(raw);
        Assert.True(sanitized.Length <= BrowserProviderProbeSanitizer.MaxMessageLength + 1);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_DoesNotLeakSecretOrStack()
    {
        var inner = new InvalidOperationException($"stack at {Token}");
        var ex = new InvalidOperationException($"Multilogin token {Token} rejected", inner);
        var message = BrowserProviderProbeSanitizer.FromException(ex, Token);

        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
        Assert.DoesNotContain("at ", message, StringComparison.Ordinal);
        Assert.DoesNotContain(ex.ToString(), message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MapsConnectionError()
    {
        var ex = new HttpRequestException(
            "No connection could be made",
            inner: null,
            statusCode: null,
            httpRequestError: HttpRequestError.ConnectionError);
        Assert.Equal("Сервис недоступен.", BrowserProviderProbeSanitizer.FromException(ex));
    }

    [Fact]
    public void Sanitize_EmptyAfterStripping_UsesSafeFallback()
    {
        Assert.Equal(
            "Не удалось проверить подключение.",
            BrowserProviderProbeSanitizer.Sanitize(Token, Token));
    }
}
