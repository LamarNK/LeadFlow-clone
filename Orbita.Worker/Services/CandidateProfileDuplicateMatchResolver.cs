using Orbita.Contracts;

namespace Orbita.Worker.Services;

internal static class CandidateProfileDuplicateMatchResolver
{
    public static async Task<HashSet<int>> ResolveAsync(
        IReadOnlyList<CandidateLookupProfileDto> profiles,
        WorkerCandidateLookupResponse? apiResult,
        Func<Task<IReadOnlySet<string>>> resolveCachedPhonesAsync)
    {
        if (apiResult is not null)
        {
            return ResolveFromApi(apiResult);
        }

        return ResolveFromOfflineCache(
            profiles,
            await resolveCachedPhonesAsync().ConfigureAwait(false));
    }

    private static HashSet<int> ResolveFromApi(WorkerCandidateLookupResponse apiResult) =>
        apiResult.MatchedProfileIndexes.ToHashSet();

    private static HashSet<int> ResolveFromOfflineCache(
        IReadOnlyList<CandidateLookupProfileDto> profiles,
        IReadOnlySet<string> cachedPhones)
    {
        var matched = new HashSet<int>();
        for (var i = 0; i < profiles.Count; i++)
        {
            var phone = profiles[i].PhoneNormalized;
            if (!string.IsNullOrWhiteSpace(phone) && cachedPhones.Contains(phone))
            {
                matched.Add(i);
            }
        }

        return matched;
    }
}
