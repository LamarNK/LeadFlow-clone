using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class MultiloginAccountIdTests
{
    [Fact]
    public void ToAccountGuid_IsDeterministic_ForSameProfileId()
    {
        var first = MultiloginAccountId.ToAccountGuid("profile-123");
        var second = MultiloginAccountId.ToAccountGuid("profile-123");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ToAccountGuid_Differs_FromAdsPowerNamespace()
    {
        const string profileId = "profile-123";
        var adsPower = AdsPowerAccountId.ToAccountGuid(profileId);
        var multilogin = MultiloginAccountId.ToAccountGuid(profileId);
        Assert.NotEqual(adsPower, multilogin);
        Assert.NotEqual("orbita-adspower:", MultiloginAccountId.IdNamespace);
    }

    [Fact]
    public void ToAccountGuid_TrimsProfileId()
    {
        var trimmed = MultiloginAccountId.ToAccountGuid("profile-123");
        var padded = MultiloginAccountId.ToAccountGuid("  profile-123  ");
        Assert.Equal(trimmed, padded);
    }
}
