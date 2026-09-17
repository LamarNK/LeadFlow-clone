using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidatePhoneRevealBudgetTests
{
    [Theory]
    [InlineData(8, 0, 8)]
    [InlineData(8, 6, 8)]
    [InlineData(8, 38, 38)]
    public void Resolve_CoversEveryLoadedOpenPhoneWatch(
        int regularBudget,
        int openPhoneWatchCount,
        int expected) =>
        Assert.Equal(
            expected,
            CandidatePhoneRevealBudget.Resolve(regularBudget, openPhoneWatchCount));

    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 10, 10)]
    [InlineData(10, 12, 10)]
    [InlineData(10, 13, 10)]
    [InlineData(10, 14, 12)]
    [InlineData(10, 30, 20)]
    [InlineData(25, 130, 40)]
    [InlineData(10, 500, 40)]
    [InlineData(0, 20, 10)]
    [InlineData(40, 130, 40)]
    public void ResolveAdaptive_GrowsWithMaskedBacklog_UpToHardCap(
        int currentBudget,
        int maskedPending,
        int expected) =>
        Assert.Equal(
            expected,
            CandidatePhoneRevealBudget.ResolveAdaptive(currentBudget, maskedPending));

    [Theory]
    [InlineData(50, 130, 50)]
    [InlineData(50, 60, 50)]
    [InlineData(45, 10, 45)]
    public void ResolveAdaptive_NeverReducesWatchDrivenBudget(
        int currentBudget,
        int maskedPending,
        int expected) =>
        Assert.Equal(
            expected,
            CandidatePhoneRevealBudget.ResolveAdaptive(currentBudget, maskedPending));

    [Fact]
    public void ResolveAdaptive_NeverReturnsLessThanCurrent()
    {
        Assert.Equal(7, CandidatePhoneRevealBudget.ResolveAdaptive(7, 1));
        Assert.Equal(0, CandidatePhoneRevealBudget.ResolveAdaptive(0, 0));
    }
}
