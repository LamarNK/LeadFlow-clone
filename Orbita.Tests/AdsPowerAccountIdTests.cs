using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class AdsPowerAccountIdTests
{
    [Fact]
    public void ToAccountGuid_IsDeterministic_ForSameProfileId()
    {
        var first = AdsPowerAccountId.ToAccountGuid("profile-123");
        var second = AdsPowerAccountId.ToAccountGuid("profile-123");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ToAccountGuid_Differs_ForDifferentProfileIds()
    {
        var first = AdsPowerAccountId.ToAccountGuid("profile-123");
        var second = AdsPowerAccountId.ToAccountGuid("profile-456");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ToAccountGuid_TrimsProfileId()
    {
        var trimmed = AdsPowerAccountId.ToAccountGuid("profile-123");
        var padded = AdsPowerAccountId.ToAccountGuid("  profile-123  ");
        Assert.Equal(trimmed, padded);
    }
}