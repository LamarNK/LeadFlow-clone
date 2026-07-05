using NotifyBot.Application.Services;

namespace NotifyBot.Tests;

public sealed class NotifyBotTimeTests
{
    [Fact]
    public void FormatMoscow_ConvertsUtcToMsk()
    {
        var utc = new DateTimeOffset(2026, 7, 5, 8, 11, 15, TimeSpan.Zero);
        Assert.Equal("05.07.2026 11:11:15", NotifyBotTime.FormatMoscow(utc));
    }

    [Fact]
    public void FormatMoscow_TimeOnly()
    {
        var utc = new DateTimeOffset(2026, 7, 5, 8, 11, 15, TimeSpan.Zero);
        Assert.Equal("11:11:15", NotifyBotTime.FormatMoscow(utc, "HH:mm:ss"));
    }
}