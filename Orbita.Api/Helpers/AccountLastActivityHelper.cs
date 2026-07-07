namespace Orbita.Api.Helpers;

internal static class AccountLastActivityHelper
{
    public static DateTime? Resolve(params DateTime?[] candidates)
    {
        DateTime? latest = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var utc = DateTimeUtcHelper.EnsureUtc(candidate.Value);
            if (latest is null || utc > latest)
            {
                latest = utc;
            }
        }

        return latest;
    }
}