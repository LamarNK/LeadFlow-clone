using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixValidationStatusesTests
{
    [Theory]
    [InlineData(BitrixValidationStatuses.Ok, true)]
    [InlineData(BitrixValidationStatuses.Warning, true)]
    [InlineData(BitrixValidationStatuses.Error, false)]
    [InlineData(BitrixValidationStatuses.NotConfigured, false)]
    [InlineData(null, false)]
    public void AllowsWebhookUsage_MatchesExpected(string? status, bool expected)
    {
        Assert.Equal(expected, BitrixValidationStatuses.AllowsWebhookUsage(status));
    }
}