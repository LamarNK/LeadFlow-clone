using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbita.Web;

namespace Orbita.Tests;

public sealed class AuthorizationRedirectTests : IClassFixture<WebApplicationFactory<WebApplicationEntryPoint>>
{
    private readonly WebApplicationFactory<WebApplicationEntryPoint> factory;

    public AuthorizationRedirectTests(WebApplicationFactory<WebApplicationEntryPoint> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Dashboard_without_permission_redirects_once_to_anonymous_access_denied_page()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var authentication = app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = authentication.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "limited@example.test")],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            CookieAuthenticationDefaults.AuthenticationScheme));

        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{authentication.Cookie.Name}={ticket}");

        var deniedRedirect = await client.GetAsync("/Dashboard");

        Assert.Equal(HttpStatusCode.Redirect, deniedRedirect.StatusCode);
        Assert.Equal("/Account/AccessDenied", deniedRedirect.Headers.Location?.AbsolutePath);
        Assert.Equal("?ReturnUrl=%2FDashboard", deniedRedirect.Headers.Location?.Query);

        var accessDenied = await client.GetAsync(deniedRedirect.Headers.Location!);

        Assert.Equal(HttpStatusCode.OK, accessDenied.StatusCode);
    }

    [Fact]
    public async Task Design_preview_user_can_open_dashboard()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var loginPage = await client.GetAsync("/Account/Login");
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        var antiforgeryToken = Regex.Match(
            loginHtml,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")
            .Groups["token"].Value;

        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(antiforgeryToken));

        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(antiforgeryToken)),
            new KeyValuePair<string, string>("Email", "admin@orbita.local"),
            new KeyValuePair<string, string>("Password", "OrbitaAdmin1!")
        ]);
        var login = await client.PostAsync("/Account/Login", form);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);

        var dashboard = await client.GetAsync(login.Headers.Location!);

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

}
