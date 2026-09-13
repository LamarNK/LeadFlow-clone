using Orbita.Web.Models.ViewModels;

namespace Orbita.Tests;

public sealed class TopUpSessionViewModelTests
{
    [Fact]
    public void TierLabel_UsesPersistedAmountForExistingQr()
    {
        var model = new TopUpSessionViewModel
        {
            DailyResponseCount = 6,
            RequestedAmount = 800m
        };

        Assert.Contains("+800 ₽", model.TierLabel);
        Assert.DoesNotContain("+750", model.TierLabel);
    }
}
