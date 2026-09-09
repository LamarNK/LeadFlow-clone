using Microsoft.AspNetCore.WebUtilities;

namespace Orbita.Web.Helpers;

public static class OfficeSwitchReturnUrl
{
    public static string Build(HttpRequest? request)
    {
        if (request is null) return "/";
        var path = request.PathBase + request.Path;
        if (!request.Path.Equals(new PathString("/Crm/Analytics"), StringComparison.OrdinalIgnoreCase))
            return path + request.QueryString;

        // A manager selected in the previous office need not exist in the new one.
        // This changes navigation only; the API still validates the actual office scope.
        var query = request.Query.Where(x => !string.Equals(x.Key, "managerUserId", StringComparison.OrdinalIgnoreCase));
        return QueryHelpers.AddQueryString(path, query);
    }
}
