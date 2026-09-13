using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class TopUpSessionRulesTests
{
    [Theory]
    [InlineData(0, 300)]
    [InlineData(1, 300)]
    [InlineData(5, 300)]
    [InlineData(6, 900)]
    [InlineData(10, 900)]
    [InlineData(11, 2000)]
    [InlineData(50, 2000)]
    public void ResolveTargetBalance_ByDailyResponses(int responses, decimal expected)
    {
        Assert.Equal(expected, TopUpSessionRules.ResolveTargetBalance(responses));
    }

    [Theory]
    [InlineData(0, 0, 300)]
    [InlineData(0, 299.99, 300)]
    [InlineData(0, 300, 900)]
    [InlineData(0, 899.99, 900)]
    [InlineData(0, 900, 2000)]
    [InlineData(6, 0, 900)]
    [InlineData(11, 0, 2000)]
    public void ResolveTargetBalance_UsesTheHigherOfDailyVolumeAndHourlySpend(
        int responses,
        decimal spentLastHour,
        decimal expected)
    {
        Assert.Equal(expected, TopUpSessionRules.ResolveTargetBalance(responses, spentLastHour));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(149.99, 300)]
    [InlineData(149, 300)]
    public void IsEligible_BelowThreshold(decimal balance, decimal _)
    {
        Assert.True(TopUpSessionRules.IsEligible(balance));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(150.01)]
    [InlineData(5000)]
    public void IsEligible_AtOrAboveThreshold_ReturnsFalse(decimal balance)
    {
        Assert.False(TopUpSessionRules.IsEligible(balance));
    }

    [Theory]
    [InlineData(0, 0, 300)]
    [InlineData(100, 0, 200)]
    [InlineData(100, 6, 800)]
    [InlineData(100, 11, 1900)]
    [InlineData(0, 11, 2000)]
    public void ResolveRequestedAmount_TargetMinusCurrent(decimal balance, int responses, decimal expected)
    {
        Assert.Equal(expected, TopUpSessionRules.ResolveRequestedAmount(balance, responses));
    }

    [Fact]
    public void ResolveRequestedAmount_AboveTarget_ClampsToZero()
    {
        Assert.Equal(0m, TopUpSessionRules.ResolveRequestedAmount(500m, 0));
    }

    [Fact]
    public void GetMoscowDayRange_ReturnsFullDaySpan()
    {
        // 12:00 UTC == 15:00 MSK (UTC+3, no DST).
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var (start, end) = TopUpSessionRules.GetMoscowDayRange(now);

        Assert.Equal(DateTimeKind.Utc, start.Kind);
        Assert.Equal(DateTimeKind.Utc, end.Kind);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
        // Moscow midnight 2026-09-03 == 2026-09-02 21:00 UTC.
        Assert.Equal(new DateTime(2026, 9, 2, 21, 0, 0, DateTimeKind.Utc), start);
    }

    [Theory]
    [InlineData(TopUpSessionStatuses.Requested, TopUpSessionStatuses.Started, true)]
    [InlineData(TopUpSessionStatuses.Requested, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.Requested, TopUpSessionStatuses.Expired, true)]
    [InlineData(TopUpSessionStatuses.Requested, TopUpSessionStatuses.QrReady, false)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.QrReady, true)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.Expired, true)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.Requested, false)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.PaymentClaimed, true)]
    [InlineData(TopUpSessionStatuses.PaymentClaimed, TopUpSessionStatuses.QrReady, true)]
    [InlineData(TopUpSessionStatuses.PaymentClaimed, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.PaymentClaimed, TopUpSessionStatuses.Expired, true)]
    [InlineData(TopUpSessionStatuses.PaymentClaimed, TopUpSessionStatuses.Started, false)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Expired, true)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Paid, true)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Completed, true)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.VerificationRequired, true)]
    [InlineData(TopUpSessionStatuses.AwaitingBalance, TopUpSessionStatuses.Completed, true)]
    [InlineData(TopUpSessionStatuses.AwaitingBalance, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.AwaitingBalance, TopUpSessionStatuses.VerificationRequired, false)]
    [InlineData(TopUpSessionStatuses.VerificationRequired, TopUpSessionStatuses.Completed, true)]
    [InlineData(TopUpSessionStatuses.VerificationRequired, TopUpSessionStatuses.Failed, true)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Started, false)]
    [InlineData(TopUpSessionStatuses.QrReady, TopUpSessionStatuses.Requested, false)]
    [InlineData(TopUpSessionStatuses.Started, TopUpSessionStatuses.Paid, false)]
    [InlineData(TopUpSessionStatuses.Failed, TopUpSessionStatuses.Started, false)]
    [InlineData(TopUpSessionStatuses.Expired, TopUpSessionStatuses.Started, false)]
    [InlineData(TopUpSessionStatuses.Cancelled, TopUpSessionStatuses.Started, false)]
    [InlineData(TopUpSessionStatuses.Paid, TopUpSessionStatuses.Started, false)]
    public void CanTransition_EnforcesForwardOnly(string from, string to, bool expected)
    {
        Assert.Equal(expected, TopUpSessionStatuses.CanTransition(from, to));
    }

    [Fact]
    public void PauseLeaseTtl_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), TopUpSessionRules.PauseLeaseTtl);
    }

    [Fact]
    public void QueueTtl_IsTwoHours()
    {
        Assert.Equal(TimeSpan.FromHours(2), TopUpSessionRules.QueueTtl);
    }

    [Fact]
    public void BalanceConfirmationTtl_Is24Hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), TopUpSessionRules.BalanceConfirmationTtl);
    }

    [Theory]
    [InlineData(100, 200, 300, 300, true)]
    [InlineData(100, 200, 300, 299, true)]
    [InlineData(100, 200, 300, 298.99, false)]
    [InlineData(100, 200, 300, 101, false)]
    [InlineData(100, 200, 300, 100, false)]
    [InlineData(100, 200, 300, 99, false)]
    public void IsExpectedBalanceIncrease_RequiresRequestedAmount(
        decimal current,
        decimal requested,
        decimal target,
        decimal actual,
        bool expected)
    {
        Assert.Equal(expected, TopUpSessionRules.IsExpectedBalanceIncrease(current, requested, target, actual));
    }

    [Fact]
    public void CanTransition_SameStatus_IsIdempotent()
    {
        Assert.True(TopUpSessionStatuses.CanTransition(TopUpSessionStatuses.Started, TopUpSessionStatuses.Started));
    }

    [Fact]
    public void SanitizeProgressMessage_TrimsAndLimits()
    {
        Assert.Null(TopUpSessionRules.SanitizeProgressMessage(null));
        Assert.Null(TopUpSessionRules.SanitizeProgressMessage("   "));
        Assert.Equal("Открываем браузер", TopUpSessionRules.SanitizeProgressMessage("  Открываем браузер \n"));
        var longMessage = new string('x', 250);
        var sanitized = TopUpSessionRules.SanitizeProgressMessage(longMessage);
        Assert.NotNull(sanitized);
        Assert.True(sanitized!.Length <= 201);
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void QrValidator_AcceptsValidPngBase64()
    {
        // Минимальный валидный PNG: сигнатура + IHDR (без полной структуры — валидатор проверяет только сигнатуру).
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        };
        var base64 = Convert.ToBase64String(png);

        var (valid, error) = TopUpSessionQrValidator.Validate(base64, null);

        Assert.True(valid, error);
        Assert.Null(error);
    }

    [Fact]
    public void QrValidator_RejectsNonPngBase64()
    {
        var notPng = Convert.ToBase64String(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }); // GIF89a

        var (valid, error) = TopUpSessionQrValidator.Validate(notPng, null);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void QrValidator_RejectsInvalidBase64()
    {
        var (valid, error) = TopUpSessionQrValidator.Validate("not-valid-base64!!!", null);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void QrValidator_RejectsEmptyPayload()
    {
        var (valid, error) = TopUpSessionQrValidator.Validate(null, null);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void QrValidator_AcceptsHttpsAvitoUrl()
    {
        var (valid, error) = TopUpSessionQrValidator.Validate(null, "https://www.avito.ru/qr/abc");

        Assert.True(valid, error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://www.avito.ru/qr")]
    [InlineData("https://evil.example/qr")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://avito.ru/qr")]
    public void QrValidator_RejectsNonHttpsAvitoUrl(string url)
    {
        var (valid, error) = TopUpSessionQrValidator.Validate(null, url);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void QrValidator_RejectsOversizedBase64()
    {
        var oversized = new string('A', TopUpSessionQrValidator.MaxBase64Length + 1);

        var (valid, error) = TopUpSessionQrValidator.Validate(oversized, null);

        Assert.False(valid);
        Assert.NotNull(error);
    }
}
