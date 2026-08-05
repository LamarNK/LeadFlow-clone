using Orbita.Web.Formatting;

namespace Orbita.Tests;

public sealed class ResponseDisplayTests
{
    [Fact]
    public void FormatAverageResponseMinutes_FormatsHoursAndMinutes()
    {
        var actual = ResponseDisplay.FormatAverageResponseMinutes(305);

        Assert.Equal("5 ч 5 мин", actual);
    }

    [Fact]
    public void FormatResponseCollectionDuration_UsesResponseAndCollectionTimes()
    {
        var responseTime = new DateTime(2026, 8, 5, 9, 35, 0, DateTimeKind.Utc);
        var collectionTime = new DateTime(2026, 8, 5, 11, 33, 0, DateTimeKind.Utc);

        var actual = ResponseDisplay.FormatResponseCollectionDuration(responseTime, collectionTime);

        Assert.Equal("1 ч 58 мин", actual);
    }
}
