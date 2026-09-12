using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdExpiryCalculatorTests
{
    [Fact]
    public void AddMonths_January31_GoesToFebruaryEnd()
    {
        var publishedLocal = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Unspecified);
        var publishedUtc = AvitoAdBusinessTime.ToUtc(publishedLocal);
        var expiresLocal = AvitoAdBusinessTime.ToLocal(AvitoAdExpiryCalculator.ComputeExpiresAtUtc(publishedUtc));

        Assert.Equal(2026, expiresLocal.Year);
        Assert.Equal(2, expiresLocal.Month);
        Assert.Equal(DateTime.DaysInMonth(2026, 2), expiresLocal.Day);
    }

    [Fact]
    public void AddMonths_February29_GoesToMarch29()
    {
        var publishedLocal = new DateTime(2024, 2, 29, 10, 0, 0, DateTimeKind.Unspecified);
        var publishedUtc = AvitoAdBusinessTime.ToUtc(publishedLocal);
        var expiresLocal = AvitoAdBusinessTime.ToLocal(AvitoAdExpiryCalculator.ComputeExpiresAtUtc(publishedUtc));

        Assert.Equal(2024, expiresLocal.Year);
        Assert.Equal(3, expiresLocal.Month);
        Assert.Equal(29, expiresLocal.Day);
    }

    [Fact]
    public void AddMonths_August31_GoesToSeptember30()
    {
        var publishedLocal = new DateTime(2026, 8, 31, 9, 0, 0, DateTimeKind.Unspecified);
        var publishedUtc = AvitoAdBusinessTime.ToUtc(publishedLocal);
        var expiresLocal = AvitoAdBusinessTime.ToLocal(AvitoAdExpiryCalculator.ComputeExpiresAtUtc(publishedUtc));

        Assert.Equal(2026, expiresLocal.Year);
        Assert.Equal(9, expiresLocal.Month);
        Assert.Equal(30, expiresLocal.Day);
    }
}
