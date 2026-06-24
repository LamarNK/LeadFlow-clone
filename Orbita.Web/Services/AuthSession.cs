namespace Orbita.Web.Services;

public sealed class AuthSession(IHttpContextAccessor httpContextAccessor)
{
    public const string TokenCookieName = "orbita_token";

    private string? _token;

    public string? Token
    {
        get => _token ?? httpContextAccessor.HttpContext?.Request.Cookies[TokenCookieName];
        set => _token = value;
    }

    public string? Email { get; set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);
}