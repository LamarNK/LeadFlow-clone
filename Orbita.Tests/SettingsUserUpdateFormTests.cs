using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web;

namespace Orbita.Tests;

public sealed class SettingsUserUpdateFormTests : IClassFixture<WebApplicationFactory<WebApplicationEntryPoint>>
{
    private readonly WebApplicationFactory<WebApplicationEntryPoint> factory;

    public SettingsUserUpdateFormTests(WebApplicationFactory<WebApplicationEntryPoint> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Users_tab_renders_the_update_form_as_a_post()
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

        using var loginForm = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(antiforgeryToken)),
            new KeyValuePair<string, string>("Email", "admin@orbita.local"),
            new KeyValuePair<string, string>("Password", "OrbitaAdmin1!")
        ]);
        var login = await client.PostAsync("/Account/Login", loginForm);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var users = await client.GetAsync("/Settings?tab=users");
        var usersHtml = await users.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var updateFormTag = Regex.Match(usersHtml, "<form\\b[^>]*id=\"editUserForm\"[^>]*>");
        Assert.True(updateFormTag.Success, "The user update form was not rendered.");
        Assert.Contains("action=\"/Settings/UpdateUser\"", updateFormTag.Value);
        Assert.Contains("method=\"post\"", updateFormTag.Value);
        Assert.DoesNotMatch("id=\"resetPasswordForm\"", usersHtml);
        Assert.Contains("data-settings-user-presence", usersHtml);
        Assert.Contains("data-user-can-delete-crm-cards=\"false\"", usersHtml);
        Assert.Contains("id=\"editUserCanDeleteCrmCards\"", usersHtml);
        Assert.Contains("id=\"editUserCanDeleteCrmCardsFalse\"", usersHtml);
        Assert.Contains("Разрешить удаление карточек CRM", usersHtml);
        Assert.Contains("Пик обычно", usersHtml);

        var updateToken = Regex.Match(
            usersHtml,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")
            .Groups["token"].Value;
        using var updateForm = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(updateToken)),
            new KeyValuePair<string, string>("FullName", string.Empty)
        ]);

        var update = await client.PostAsync("/Settings/UpdateUser", updateForm);

        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Equal("/Settings?tab=users", update.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(PanelRoles.Manager, "true")]
    [InlineData(PanelRoles.SeniorManager, "true")]
    [InlineData(PanelRoles.OfficeLead, "false")]
    public async Task NonAdministrator_WithSettingsAccess_CannotSeeOrSubmitDeletionGrant(string role, string value)
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var authentication = app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = authentication.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "settings-only-user"),
                new Claim(ClaimTypes.Name, "settings-only@example.test"),
                new Claim(ClaimTypes.Role, role),
                new Claim(PanelPermissions.ClaimType, PanelPermissions.Administration)
            ], CookieAuthenticationDefaults.AuthenticationScheme)),
            CookieAuthenticationDefaults.AuthenticationScheme));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{authentication.Cookie.Name}={ticket}");

        var page = await client.GetAsync("/Settings?tab=users");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"editUserCanDeleteCrmCards\"", html);
        Assert.DoesNotContain("id=\"editUserCanDeleteCrmCardsFalse\"", html);
        var token = Regex.Match(html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")
            .Groups["token"].Value;
        Assert.NotEmpty(token);
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
            new KeyValuePair<string, string>("UserId", "target-user"),
            new KeyValuePair<string, string>("FullName", "Тестовый сотрудник"),
            new KeyValuePair<string, string>("CanDeleteCrmCards", value)
        ]);

        var update = await client.PostAsync("/Settings/UpdateUser", form);
        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Equal("/Account/AccessDenied", update.Headers.Location?.AbsolutePath);
    }
}
