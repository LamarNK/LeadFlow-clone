using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Orbita.Api.Auth;
using Orbita.Api.Endpoints;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class JwtTokenFactoryTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "OrbitaTestSigningKey_AtLeast32Characters!",
            ["Jwt:Issuer"] = "Orbita.Tests",
            ["Jwt:Audience"] = "Orbita.Tests.Web"
        })
        .Build();

    [Fact]
    public void Token_CarriesAccessVersion_AndCanBeValidatedForRefresh()
    {
        var user = new IdentityUser
        {
            Id = "user-1",
            Email = "operator@example.test",
            UserName = "Operator",
            SecurityStamp = "stamp-1"
        };

        var token = JwtTokenFactory.CreateToken(
            user,
            [PanelRoles.Operator],
            [PanelPermissions.Dashboard, PanelPermissions.Accounts],
            Config,
            Guid.NewGuid(),
            accessVersion: 7,
            rememberMe: true);

        Assert.True(JwtTokenFactory.TryValidate(token, Config, out var principal));
        Assert.NotNull(principal);
        Assert.Equal("user-1", principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("7", principal.FindFirstValue(JwtTokenFactory.AccessVersionClaimType));
        Assert.Equal("stamp-1", principal.FindFirstValue(JwtSecurityStampValidator.SecurityStampClaimType));
    }
}
