using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class PanelUserPresenceRulesTests
{
    [Fact]
    public void IsOnline_WhenLastActivityIsWithinThreshold()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(PanelUserPresenceRules.IsOnline(now.AddMinutes(-5), now));
    }

    [Fact]
    public void IsOnline_WhenLastActivityIsStaleOrMissing_ReturnsFalse()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(PanelUserPresenceRules.IsOnline(now.AddMinutes(-5).AddTicks(-1), now));
        Assert.False(PanelUserPresenceRules.IsOnline(null, now));
        Assert.False(PanelUserPresenceRules.IsOnline(now.AddMinutes(1), now));
    }
}
