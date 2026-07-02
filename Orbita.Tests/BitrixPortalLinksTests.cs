using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class BitrixPortalLinksTests
{
    [Theory]
    [InlineData("demo.bitrix24.ru", "Deal", "42", "https://demo.bitrix24.ru/crm/deal/details/42/")]
    [InlineData("https://demo.bitrix24.ru/rest/1/secret/", "Lead", "7", "https://demo.bitrix24.ru/crm/lead/details/7/")]
    [InlineData("https://portal.example.com", "Contact", "99", "https://portal.example.com/crm/contact/details/99/")]
    public void TryBuildEntityDetailsUrl_BuildsExpectedUrl(
        string portalHost,
        string entityType,
        string entityId,
        string expected)
    {
        var url = BitrixPortalLinks.TryBuildEntityDetailsUrl(portalHost, entityType, entityId);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null, "Deal", "1")]
    [InlineData("demo.bitrix24.ru", "Deal", "")]
    [InlineData("demo.bitrix24.ru", "Deal", null)]
    public void TryBuildEntityDetailsUrl_ReturnsNull_WhenInputMissing(
        string? portalHost,
        string entityType,
        string? entityId)
    {
        Assert.Null(BitrixPortalLinks.TryBuildEntityDetailsUrl(portalHost, entityType, entityId));
    }
}