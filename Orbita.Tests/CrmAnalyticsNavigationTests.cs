using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Orbita.Web.Helpers;

namespace Orbita.Tests;

public sealed class CrmAnalyticsNavigationTests
{
    [Theory]
    [InlineData("/Crm/Analytics")]
    [InlineData("/crm/analytics")]
    public void OfficeChangeClearsOnlyManagerAndPreservesDates(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/panel";
        context.Request.Path = path;
        context.Request.QueryString = new("?from=2026-09-01&to=2026-09-20&tz=-300&managerUserId=former-manager");
        var result = OfficeSwitchReturnUrl.Build(context.Request);
        Assert.StartsWith("/panel" + path + "?", result);
        var query = QueryHelpers.ParseQuery(result[(result.IndexOf('?') + 1)..]);
        Assert.Equal("2026-09-01", query["from"]);
        Assert.Equal("2026-09-20", query["to"]);
        Assert.Equal("-300", query["tz"]);
        Assert.False(query.ContainsKey("managerUserId"));
    }

    [Fact]
    public void OfficeChangeDoesNotAlterOtherPages()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/Crm";
        context.Request.QueryString = new("?managerUserId=manager&stage=НДЗ");
        Assert.Equal("/Crm" + context.Request.QueryString, OfficeSwitchReturnUrl.Build(context.Request));
        Assert.Equal("/", OfficeSwitchReturnUrl.Build(null));
    }
}
