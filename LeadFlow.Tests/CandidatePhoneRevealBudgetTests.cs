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
}
