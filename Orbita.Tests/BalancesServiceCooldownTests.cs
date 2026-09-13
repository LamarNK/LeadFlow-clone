using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class BalancesServiceCooldownTests
{
    [Theory]
    [InlineData(100, false, true)]
    [InlineData(100, true, false)]
    [InlineData(150, false, false)]
    [InlineData(300, false, false)]
    public void ShouldOfferTopUp_HidesLowBalanceDuringCooldown(
        decimal balance,
        bool cooldownActive,
        bool expected)
    {
        Assert.Equal(expected, BalancesService.ShouldOfferTopUp(balance, cooldownActive));
    }
}
