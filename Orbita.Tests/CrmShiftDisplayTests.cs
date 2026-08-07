using Orbita.Web.Formatting;

namespace Orbita.Tests;

public sealed class CrmShiftDisplayTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FormatStatusLine_OnShift_ShowsDurationAndSince()
    {
        var started = Now.AddHours(-2).AddMinutes(-15);
        var line = CrmShiftDisplay.FormatStatusLine(true, started, lastShiftEndedAtUtc: null, Now);
        Assert.Contains("на смене", line, StringComparison.Ordinal);
        Assert.Contains("2 ч 15 мин", line, StringComparison.Ordinal);
        Assert.Contains("с ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStatusLine_OffShift_ShowsWhenLeft()
    {
        var ended = Now.AddHours(-3);
        var line = CrmShiftDisplay.FormatStatusLine(false, null, ended, Now);
        Assert.Contains("не на смене", line, StringComparison.Ordinal);
        Assert.Contains("вышел", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStatusLine_NeverWorked_IsPlainOff()
    {
        Assert.Equal("не на смене", CrmShiftDisplay.FormatStatusLine(false, null, null, Now));
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
