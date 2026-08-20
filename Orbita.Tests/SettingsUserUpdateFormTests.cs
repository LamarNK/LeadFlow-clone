using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
