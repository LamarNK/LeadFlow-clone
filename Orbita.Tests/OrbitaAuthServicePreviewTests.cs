using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class OrbitaAuthServicePreviewTests
{
    [Fact]
    public async Task SignInPreviewAsync_GrantsAdminRoleAndAllPanelPermissions()
    {
        var authentication = new CapturingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var session = new AuthSession(accessor);
        var service = new OrbitaAuthService(accessor, session);

        await service.SignInPreviewAsync("admin@orbita.local", "Администратор");

        var principal = Assert.IsType<ClaimsPrincipal>(authentication.Principal);
        Assert.True(principal.IsInRole(PanelRoles.Admin));
        var permissions = principal.FindAll(PanelPermissions.ClaimType)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var expectedPermissions = PanelPermissions.All
            .Select(permission => permission.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedPermissions.SetEquals(permissions));
        Assert.Equal(AuthSession.DesignPreviewToken, session.Token);
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? Principal { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            Principal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
