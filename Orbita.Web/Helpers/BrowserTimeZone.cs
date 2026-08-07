namespace Orbita.Web.Helpers;

/// <summary>
/// Browser timezone offset in the same units as JavaScript
/// <c>Date#getTimezoneOffset()</c>: minutes to add to local wall time to get UTC
/// (e.g. UTC+5 → -300).
/// </summary>
public static class BrowserTimeZone
{
    public const string CookieName = "orbita_tz";
    public const string QueryName = "tz";

    /// <summary>Valid JS offset range roughly covers all civil zones.</summary>
    public static bool IsValidOffsetMinutes(int minutes) => minutes is >= -840 and <= 840;

    public static int Resolve(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return 0;
        }

        return Resolve(httpContext.Request);
    }

    public static int Resolve(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Query.TryGetValue(QueryName, out var queryValues)
            && int.TryParse(queryValues.FirstOrDefault(), out var fromQuery)
            && IsValidOffsetMinutes(fromQuery))
        {
            return fromQuery;
        }

        if (request.Cookies.TryGetValue(CookieName, out var cookie)
            && int.TryParse(cookie, out var fromCookie)
            && IsValidOffsetMinutes(fromCookie))
        {
            return fromCookie;
        }

        // No browser signal yet: treat calendar dates as UTC days (not server-local).
        return 0;
    }
}
