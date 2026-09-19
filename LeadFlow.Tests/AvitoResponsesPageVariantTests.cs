using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoResponsesPageVariantTests
{
    [Theory]
    [InlineData(AvitoResponsesPageVariant.JobCrmDetailed, true)]
    [InlineData(AvitoResponsesPageVariant.JobCrmCompact, true)]
    [InlineData(AvitoResponsesPageVariant.JobCrm, true)]
    [InlineData(AvitoResponsesPageVariant.Legacy, false)]
    [InlineData(AvitoResponsesPageVariant.Unknown, false)]
    [InlineData(null, false)]
    public void IsJobCrm_RecognizesCrmLayouts(string? variant, bool expected) =>
        Assert.Equal(expected, AvitoResponsesPageVariant.IsJobCrm(variant));

    [Fact]
    public void Describe_PrefersDomVariantOverCandidatesUrl()
    {
        Assert.Equal(
            "CRM подробный вид",
            AvitoResponsesPageVariant.Describe("https://www.avito.ru/profile/candidates", AvitoResponsesPageVariant.JobCrmDetailed));
        Assert.Equal(
            "CRM компактный список",
            AvitoResponsesPageVariant.Describe("https://www.avito.ru/profile/candidates", AvitoResponsesPageVariant.JobCrmCompact));
    }
}
