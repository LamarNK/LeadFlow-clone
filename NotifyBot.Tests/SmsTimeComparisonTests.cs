using System.Globalization;

namespace NotifyBot.Tests;

public sealed class SmsTimeComparisonTests
{
    [Fact]
    public void RealPlusofonSms_AgeFilter_UsesUtcCorrectly()
    {
        var smsRaw = "2026-06-29 17:18:22+03:00";
        var serverUtcNow = new DateTimeOffset(2026, 6, 29, 15, 28, 20, TimeSpan.Zero);

        var parsedUtc = ParseLikeClient(smsRaw);
        Assert.NotNull(parsedUtc);

        var ageMinutes = (serverUtcNow - parsedUtc.Value).TotalMinutes;
        var passesFilter = parsedUtc.Value >= serverUtcNow.AddMinutes(-15);

        Assert.Equal(new DateTimeOffset(2026, 6, 29, 14, 18, 22, TimeSpan.Zero), parsedUtc.Value);
        Assert.InRange(ageMinutes, 69, 71);
        Assert.False(passesFilter);
    }

    [Fact]
    public void RealPlusofonSms_OnUtcServer_DisplayDiffersFromPlusofonLocal()
    {
        var smsRaw = "2026-06-29 17:18:22+03:00";
        var parsedUtc = ParseLikeClient(smsRaw);
        Assert.NotNull(parsedUtc);

        // Сервер/контейнер в UTC: ToLocalTime() покажет 14:18, не 17:18 из Plusofon
        var displayOnUtcServer = parsedUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal("29.06.2026 14:18:22", displayOnUtcServer);
    }

    private static DateTimeOffset? ParseLikeClient(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }
}