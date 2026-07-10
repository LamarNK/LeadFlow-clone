using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoResponseCardFingerprintTests
{
    [Fact]
    public void Build_WithMessengerUrl_UsesChannelKey()
    {
        var fp = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            "https://www.avito.ru/profile/messenger/channel/abc-123");

        Assert.Equal("avito-card:msg:abc-123", fp);
    }

    [Fact]
    public void Build_WithoutMessenger_UsesStableHashFromVacancyId()
    {
        var first = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            null,
            "42 лет");

        var second = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Другой текст вакансии",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            null,
            "42 лет");

        Assert.Equal(first, second);
        Assert.StartsWith("avito-card:", first, StringComparison.Ordinal);
        Assert.DoesNotContain("msg:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAgeText_MatchesJsAndDatabaseAge()
    {
        Assert.Equal("42 лет", AvitoResponseCardFingerprint.NormalizeAgeText("42 лет"));
        Assert.Equal("42 лет", AvitoResponseCardFingerprint.NormalizeAgeText("42"));
        Assert.Equal("42 лет", AvitoResponseCardFingerprint.NormalizeAgeText("42 года"));
        Assert.Equal("42 лет", AvitoResponseCardFingerprint.NormalizeAgeText(null, 42));

        var fromJs = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            null,
            "42 лет");

        var fromDbAge = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            null,
            AvitoResponseCardFingerprint.NormalizeAgeText(null, 42));

        Assert.Equal(fromJs, fromDbAge);
    }

    [Fact]
    public void Build_NormalizesWhitespace()
    {
        var a = AvitoResponseCardFingerprint.Build(
            "  Иван   Иванов ",
            "Слесарь",
            " Москва ",
            null,
            null,
            "42 лет");

        var b = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            null,
            null,
            "42 лет");

        Assert.Equal(a, b);
    }
}