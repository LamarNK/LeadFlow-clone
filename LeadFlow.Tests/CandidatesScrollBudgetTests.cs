using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidatesScrollBudgetTests
{
    [Theory]
    [InlineData(false, 48)]
    [InlineData(true, 96)]
    public void Resolve_ExtendsOnlyActivePhoneWatchScans(bool hasOpenPhoneWatches, int expected) =>
        Assert.Equal(expected, CandidatesScrollBudget.Resolve(hasOpenPhoneWatches));
}
