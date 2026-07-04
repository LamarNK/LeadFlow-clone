using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class OfficeContextTests
{
    [Fact]
    public void Bind_AdminWithoutCookie_ShowsAllOffices()
    {
        var context = CreateContext(isAdmin: true, officeCookie: null);
        var office = new OfficeContext();
        office.Bind(context);

        Assert.True(office.IsAdmin);
        Assert.True(office.ShowAllOffices);
        Assert.True(office.ShowOfficeColumn);
        Assert.Null(office.EffectiveOfficeId);
        Assert.Equal("Все офисы", office.ContextLabel);
    }

    [Fact]
    public void Bind_AdminWithCookie_UsesSelectedOffice()
    {
        var officeId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var httpContext = CreateContext(
            isAdmin: true,
            officeCookie: officeId.ToString("D"),
            officeNameCookie: "Moscow");
        var office = new OfficeContext();
        office.Bind(httpContext);

        Assert.Equal(officeId, office.EffectiveOfficeId);
        Assert.False(office.ShowAllOffices);
        Assert.False(office.ShowOfficeColumn);
        Assert.Equal("Офис: Moscow", office.ContextLabel);
    }

    [Fact]
    public void Bind_OperatorUsesClaim_IgnoresCookie()
    {
        var operatorOffice = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var httpContext = CreateContext(
            isAdmin: false,
            officeClaim: operatorOffice,
            officeCookie: Guid.NewGuid().ToString("D"));
        var office = new OfficeContext();
        office.Bind(httpContext);

        Assert.False(office.IsAdmin);
        Assert.Equal(operatorOffice, office.EffectiveOfficeId);
        Assert.False(office.ShowAllOffices);
        Assert.False(office.ShowOfficeColumn);
    }

    private static HttpContext CreateContext(
        bool isAdmin,
        Guid? officeClaim = null,
        string? officeCookie = null,
        string? officeNameCookie = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, "user@test.local")
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, PanelRoles.Admin));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.Role, PanelRoles.Operator));
            if (officeClaim is Guid officeId)
            {
                claims.Add(new Claim(OfficeClaims.OfficeId, officeId.ToString("D")));
            }
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };

        var cookieParts = new List<string>();
        if (officeCookie is not null)
        {
            cookieParts.Add($"{OfficeSelection.CookieName}={officeCookie}");
        }

        if (officeNameCookie is not null)
        {
            cookieParts.Add($"{OfficeSelection.NameCookieName}={officeNameCookie}");
        }

        if (cookieParts.Count > 0)
        {
            context.Request.Headers.Cookie = string.Join("; ", cookieParts);
            _ = context.Request.Cookies.Count;
        }

        return context;
    }
}