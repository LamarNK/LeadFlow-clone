using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerScheduleRulesTests
{
    [Fact]
    public void IsActiveNow_ReturnsTrue_InsideWeekdayWindow()
    {
        var utc = new DateTime(2026, 7, 27, 4, 30, 0, DateTimeKind.Utc);

        var active = WorkerScheduleRules.IsActiveNow(true, "Mon,Tue,Wed,Thu,Fri", "07:00", "19:00", utc);

        Assert.True(active);
    }

    [Fact]
    public void IsActiveNow_ReturnsFalse_OutsideWeekdayWindow()
    {
        var utc = new DateTime(2026, 7, 27, 17, 0, 0, DateTimeKind.Utc);

        var active = WorkerScheduleRules.IsActiveNow(true, "Mon,Tue,Wed,Thu,Fri", "07:00", "19:00", utc);

        Assert.False(active);
    }
}
