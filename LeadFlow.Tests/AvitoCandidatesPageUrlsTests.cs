using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesPageUrlsTests
{
    [Theory]
    [InlineData("https://www.avito.ru/profile/candidates", true)]
    [InlineData("https://www.avito.ru/profile/candidates?foo=1", true)]
    [InlineData("https://www.avito.ru/profile/job/responses", true)]
    [InlineData("https://www.avito.ru/profile/job/responses#tab=new", true)]
    [InlineData("https://www.avito.ru/profile/pro/items", false)]
    [InlineData(null, false)]
    public void IsCandidatesResponsesUrl_DetectsKnownLayouts(string? url, bool expected)
    {
        Assert.Equal(expected, AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(url));
    }
}