using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmClientTimeResolverTests
{
    [Theory]
    [InlineData("Москва", 180, 15)]
    [InlineData("Екатеринбург", 300, 17)]
    [InlineData("посёлок городского типа Болотное", 420, 19)]
    [InlineData("Тында", 540, 21)]
    [InlineData("Владивосток", 600, 22)]
    public void Resolve_KnownRussianCity_ReturnsCivilTime(
        string city,
        int expectedOffsetMinutes,
        int expectedHour)
    {
        var utcNow = new DateTime(2026, 8, 10, 12, 34, 0, DateTimeKind.Utc);

        var result = CrmClientTimeResolver.Resolve(city, utcNow);

        Assert.NotNull(result);
        Assert.Equal(expectedOffsetMinutes, result.UtcOffsetMinutes);
        Assert.Equal(expectedHour, result.LocalTime.Hour);
        Assert.Equal(34, result.LocalTime.Minute);
        Assert.Equal(DateTimeKind.Unspecified, result.LocalTime.Kind);
    }

    [Fact]
    public void Resolve_UnknownCity_ReturnsNull()
    {
        Assert.Null(CrmClientTimeResolver.Resolve("Неизвестный населённый пункт", DateTime.UtcNow));
    }

    [Fact]
    public void Resolve_LegacyDefaultFunnel_UpgradesToCurrentDefault()
    {
        string[] legacy =
        [
            CrmStages.Lead,
            CrmStages.Ndz73,
            CrmStages.Ndz26,
            CrmStages.Substitution,
            CrmStages.Negotiations,
            CrmStages.Questionnaire,
            CrmStages.Ticket
        ];

        var resolved = CrmStages.Resolve(CrmStages.Serialize(legacy));

        Assert.Equal(CrmStages.Default, resolved);
        Assert.Contains(CrmStages.PreparingToSend, resolved);
        Assert.Contains(CrmStages.InTransit, resolved);
        Assert.Contains(CrmStages.Signing, resolved);
        Assert.Contains(CrmStages.DealSuccessful, resolved);
    }
}
