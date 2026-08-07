using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class AccountMetricLinksTests
{
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void Responses_ReturnsLink_WhenCountPositive()
    {
        var link = AccountMetricLinks.Responses(WorkerId, AccountId, 3);

        Assert.Equal(3, link.Value);
        Assert.NotNull(link.Href);
        Assert.Contains($"workerId={WorkerId}", link.Href, StringComparison.Ordinal);
        Assert.Contains($"accountId={AccountId}", link.Href, StringComparison.Ordinal);
        var today = DashboardPeriod.GetLocalCalendarDate(DateTime.UtcNow, 0);
        Assert.Contains($"from={today:yyyy-MM-dd}", link.Href, StringComparison.Ordinal);
        Assert.Contains("tz=0", link.Href, StringComparison.Ordinal);
    }

    [Fact]
    public void Responses_ReturnsPlainValue_WhenZero()
    {
        var link = AccountMetricLinks.Responses(WorkerId, AccountId, 0);

        Assert.Equal(0, link.Value);
        Assert.Null(link.Href);
    }

    [Fact]
    public void Errors_LinksToFilteredJournal()
    {
        var link = AccountMetricLinks.Errors(WorkerId, AccountId, 2);

        Assert.Equal("/Events?level=errors&workerId=" + WorkerId + "&accountId=" + AccountId, link.Href);
    }
}