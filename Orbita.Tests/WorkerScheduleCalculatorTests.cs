using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerScheduleCalculatorTests
{
    [Fact]
    public void ReferenceWeek_MatchesBalancedSevenGroupMatrix()
    {
        var expectedShift1 = new[]
        {
            new[] { "OFF", "DAY", "DAY", "DAY", "DAY", "DAY", "DAY" },
            new[] { "DAY", "OFF", "NIGHT", "NIGHT", "NIGHT", "NIGHT", "NIGHT" },
            new[] { "NIGHT", "NIGHT", "OFF", "DAY", "DAY", "DAY", "DAY" },
            new[] { "DAY", "DAY", "DAY", "OFF", "NIGHT", "NIGHT", "NIGHT" },
            new[] { "NIGHT", "NIGHT", "NIGHT", "NIGHT", "OFF", "DAY", "DAY" },
            new[] { "DAY", "DAY", "DAY", "DAY", "DAY", "OFF", "NIGHT" },
            new[] { "NIGHT", "NIGHT", "NIGHT", "NIGHT", "NIGHT", "NIGHT", "OFF" }
        };

        for (var dayOff = 0; dayOff < 7; dayOff++)
        {
            var orientation = WorkerScheduleCalculator.ReferenceOrientation(dayOff, currentDay: 4);
            var calendar = WorkerScheduleCalculator.Calculate(dayOff, orientation, currentDay: 4);

            Assert.Equal(expectedShift1[dayOff], calendar.Select(x => x.Shift1));
            Assert.All(calendar.Where(x => !x.IsDayOff), day =>
                Assert.NotEqual(day.Shift1, day.Shift2));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Calculate_MarksExactlyOneFullDayOff(int dayOff)
    {
        var days = WorkerScheduleCalculator.Calculate(dayOff, WorkerScheduleShifts.DayFirst, (dayOff + 1) % 7);
        Assert.Equal(7, days.Count);
        var off = Assert.Single(days, x => x.IsDayOff);
        Assert.Equal(dayOff, off.DayOfWeek);
        Assert.Equal(WorkerScheduleShifts.Off, off.Shift1);
        Assert.Equal(WorkerScheduleShifts.Off, off.Shift2);
    }

    [Fact]
    public void Calculate_SwapsOrientationAcrossDayOff()
    {
        var days = WorkerScheduleCalculator.Calculate(2, WorkerScheduleShifts.DayFirst, 3);
        Assert.Equal(WorkerScheduleShifts.Night, days[1].Shift1);
        Assert.Equal(WorkerScheduleShifts.Off, days[2].Shift1);
        Assert.Equal(WorkerScheduleShifts.Day, days[3].Shift1);
        Assert.Equal(WorkerScheduleShifts.Night, days[3].Shift2);
    }

    [Fact]
    public void Calculate_BeforeDayOff_KeepsCurrentOrientationUntilDayOff()
    {
        var days = WorkerScheduleCalculator.Calculate(2, WorkerScheduleShifts.DayFirst, 1);

        Assert.Equal(WorkerScheduleShifts.Day, days[1].Shift1);
        Assert.Equal(WorkerScheduleShifts.Off, days[2].Shift1);
        Assert.Equal(WorkerScheduleShifts.Night, days[3].Shift1);
    }

    [Fact]
    public void IsActiveNow_OvernightShift_CannotRunDuringOrOutOfFullDayOff()
    {
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.NightFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 15, 23, 30, 0), "07:00", "19:00", "19:00", "07:00"));
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 16, 0, 1, 0), "07:00", "19:00", "19:00", "07:00"));
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.NightFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 17, 1, 0, 0), "07:00", "19:00", "19:00", "07:00"));
        Assert.True(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.NightFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 17, 19, 0, 0), "07:00", "19:00", "19:00", "07:00"));
    }

    [Fact]
    public void IsActiveNow_OvernightDayWindow_CannotCrossFullDayOff()
    {
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 15, 23, 30, 0), "19:00", "07:00", "07:00", "19:00"));
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 17, 1, 0, 0), "19:00", "07:00", "07:00", "19:00"));
        Assert.True(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift1,
            new DateTime(2026, 9, 17, 19, 0, 0), "19:00", "07:00", "07:00", "19:00"));
    }

    [Fact]
    public void IsActiveNow_DayAndNightWindows_AreMutuallyExclusive()
    {
        var noon = new DateTime(2026, 9, 17, 12, 0, 0);
        Assert.True(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift1, noon, "07:00", "19:00", "19:00", "07:00"));
        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.DayFirst, WorkerScheduleShifts.Shift2, noon, "07:00", "19:00", "19:00", "07:00"));
    }

    [Fact]
    public void IsActiveNow_BeforeWeekdayDayOff_UsesActiveCycleOrientation()
    {
        var tuesdayNoonBeforeWednesdayOff = new DateTime(2026, 9, 15, 12, 0, 0);

        Assert.False(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.NightFirst, WorkerScheduleShifts.Shift1,
            tuesdayNoonBeforeWednesdayOff, "07:00", "19:00", "19:00", "07:00"));
        Assert.True(WorkerScheduleCalculator.IsActiveNow(2, WorkerScheduleShifts.NightFirst, WorkerScheduleShifts.Shift2,
            tuesdayNoonBeforeWednesdayOff, "07:00", "19:00", "19:00", "07:00"));
    }
}
