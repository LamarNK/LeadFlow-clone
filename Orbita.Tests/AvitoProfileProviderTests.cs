using LeadFlow.Core.Data;
using LeadFlow.Core.Models;

namespace Orbita.Tests;

public sealed class AvitoProfileProviderTests
{
    [Fact]
    public void Local_IsDefaultValue()
    {
        Assert.Equal(AvitoProfileProvider.Local, default(AvitoProfileProvider));
        Assert.Equal(AvitoProfileProvider.Local, new AvitoAccount().ProfileProvider);
        Assert.Equal(nameof(AvitoProfileProvider.Local), new AvitoAccountEntity().ProfileProvider);
    }

    [Fact]
    public void NumericValues_KeepLocalAndAdsPowerStable_AndAppendMultilogin()
    {
        Assert.Equal(0, (int)AvitoProfileProvider.Local);
        Assert.Equal(1, (int)AvitoProfileProvider.AdsPower);
        Assert.Equal(2, (int)AvitoProfileProvider.Multilogin);
    }

    [Fact]
    public void Multilogin_IsDistinctFromAdsPowerAndLocal()
    {
        Assert.NotEqual(AvitoProfileProvider.Multilogin, AvitoProfileProvider.AdsPower);
        Assert.NotEqual(AvitoProfileProvider.Multilogin, AvitoProfileProvider.Local);
        Assert.Equal("Multilogin", AvitoProfileProvider.Multilogin.ToString());
        Assert.Equal("AdsPower", AvitoProfileProvider.AdsPower.ToString());
    }

    [Theory]
    [InlineData("AdsPower", AvitoProfileProvider.AdsPower)]
    [InlineData("Local", AvitoProfileProvider.Local)]
    [InlineData("Multilogin", AvitoProfileProvider.Multilogin)]
    public void TryParse_KnownValues_RoundTrip(string stored, AvitoProfileProvider expected)
    {
        Assert.True(Enum.TryParse<AvitoProfileProvider>(stored, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("adspower")]
    public void TryParse_UnknownOrEmpty_DoesNotMatchMultilogin(string stored)
    {
        var parsed = Enum.TryParse<AvitoProfileProvider>(stored, out var provider)
            ? provider
            : AvitoProfileProvider.Local;
        Assert.Equal(AvitoProfileProvider.Local, parsed);
        Assert.NotEqual(AvitoProfileProvider.Multilogin, parsed);
        Assert.NotEqual(AvitoProfileProvider.AdsPower, parsed);
    }
}
