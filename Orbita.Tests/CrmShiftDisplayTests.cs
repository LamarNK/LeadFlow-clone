using Orbita.Web.Formatting;

namespace Orbita.Tests;

public sealed class CrmShiftDisplayTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FormatDurationSince_UsesUtcInstants()
    {
        var started = Now.AddHours(-2).AddMinutes(-15);
        Assert.Equal("2 ч 15 мин", CrmShiftDisplay.FormatDurationSince(started, Now));
    }

    [Theory]
    [InlineData(0, "меньше минуты")]
    [InlineData(45, "45 мин")]
    [InlineData(60, "1 ч")]
    [InlineData(90, "1 ч 30 мин")]
    public void FormatDuration_HumanReadable(int totalMinutes, string expected)
    {
        Assert.Equal(expected, CrmShiftDisplay.FormatDuration(TimeSpan.FromMinutes(totalMinutes)));
    }
}
