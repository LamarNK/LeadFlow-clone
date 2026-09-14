using Orbita.Contracts;
using Orbita.Worker.Services;

namespace Orbita.Tests;

public sealed class CandidateProfileDuplicateMatchResolverTests
{
    [Fact]
    public async Task ResolveAsync_ApiSucceeded_DoesNotMatchAnotherPersonByPhoneOnly()
    {
        var profiles = new[]
        {
            Profile("Кузнецов Олег Валерьевич", "79000000001"),
            Profile("Кушкин Александр Юрьевич", "79000000002"),
            Profile("Терехин Сергей Дмитриевич", "79000000003")
        };
        var apiResult = new WorkerCandidateLookupResponse(
            [],
            ["79000000001", "79000000002", "79000000003"],
            [],
            [0, 1]);

        var cacheWasCalled = false;
        var matched = await CandidateProfileDuplicateMatchResolver.ResolveAsync(
            profiles,
            apiResult,
            () =>
            {
                cacheWasCalled = true;
                return Task.FromResult<IReadOnlySet<string>>(
                    new HashSet<string>(["79000000003"], StringComparer.Ordinal));
            });

        Assert.Equal([0, 1], matched.OrderBy(static x => x).ToArray());
        Assert.False(cacheWasCalled);
    }

    [Fact]
    public async Task ResolveAsync_ApiUnavailable_UsesCachedPhonesAsOfflineFallback()
    {
        var profiles = new[]
        {
            Profile("Терехин Сергей Дмитриевич", "79000000003")
        };

        var matched = await CandidateProfileDuplicateMatchResolver.ResolveAsync(
            profiles,
            apiResult: null,
            () => Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(["79000000003"], StringComparer.Ordinal)));

        Assert.Equal([0], matched);
    }

    private static CandidateLookupProfileDto Profile(string fullName, string phone) =>
        new(fullName, 26, "Джанкой", phone);
}
