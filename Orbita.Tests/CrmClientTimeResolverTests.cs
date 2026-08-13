using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmClientTimeResolverTests
{
    [Theory]
    [InlineData("Калининград", 120, 14)]
    [InlineData("Москва", 180, 15)]
    [InlineData("Сыктывкар", 180, 15)]
    [InlineData("Тихвин", 180, 15)]
    [InlineData("Махачкала", 180, 15)]
    [InlineData("посёлок городского типа Апастово", 180, 15)]
    [InlineData("Самара", 240, 16)]
    [InlineData("Екатеринбург", 300, 17)]
    [InlineData("Омск", 360, 18)]
    [InlineData("посёлок городского типа Болотное", 420, 19)]
    [InlineData("Бородино", 420, 19)]
    [InlineData("Иркутск", 480, 20)]
    [InlineData("Тында", 540, 21)]
    [InlineData("Владивосток", 600, 22)]
    [InlineData("Магадан", 660, 23)]
    [InlineData("Анадырь", 720, 0)]
    public void Resolve_KnownRussianLocality_ReturnsCivilTime(
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

    [Theory]
    [InlineData("г. Екатеринбург", 300)]
    [InlineData("пгт Болотное", 420)]
    [InlineData("Одинцово, Московская область", 180)]
    [InlineData("Пермский край, Соликамск", 300)]
    public void Resolve_CommonCardValueVariants_ReturnsCivilTime(string city, int expectedOffsetMinutes)
    {
        var result = CrmClientTimeResolver.Resolve(city, new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(result);
        Assert.Equal(expectedOffsetMinutes, result.UtcOffsetMinutes);
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
