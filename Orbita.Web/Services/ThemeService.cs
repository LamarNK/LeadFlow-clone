namespace Orbita.Web.Services;

public sealed class ThemeService(IHttpContextAccessor httpContextAccessor)
{
    public const string CookieName = "orbita_theme";

    public string Theme { get; private set; } = "light";

    public void LoadFromRequest()
    {
        if (httpContextAccessor.HttpContext?.Request.Cookies.TryGetValue(CookieName, out var value) == true
            && value is "light" or "dark")
        {
            Theme = value;
        }
    }

    public void Toggle() => Theme = Theme == "light" ? "dark" : "light";

    public bool IsLight => Theme == "light";
}