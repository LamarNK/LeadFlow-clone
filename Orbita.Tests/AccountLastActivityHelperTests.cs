using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class AccountLastActivityHelperTests
{
    [Fact]
    public void Resolve_ReturnsLatestUtcCandidate()
    {
        var monitoringAt = new DateTime(2026, 7, 5, 19, 55, 0, DateTimeKind.Utc);
        var eventAt = new DateTime(2026, 7, 6, 1, 10, 0, DateTimeKind.Utc);
        var responseAt = new DateTime(2026, 7, 5, 23, 0, 0, DateTimeKind.Utc);

        var resolved = AccountLastActivityHelper.Resolve(monitoringAt, eventAt, responseAt);

        Assert.Equal(eventAt, resolved);
        Assert.Equal(DateTimeKind.Utc, resolved!.Value.Kind);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoCandidates()
    {
        Assert.Null(AccountLastActivityHelper.Resolve(null, null));
    }
}