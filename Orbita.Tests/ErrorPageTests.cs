using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orbita.Web;

namespace Orbita.Tests;

public sealed class ErrorPageTests : IClassFixture<WebApplicationFactory<WebApplicationEntryPoint>>
{
    private readonly WebApplicationFactory<WebApplicationEntryPoint> factory;

    public ErrorPageTests(WebApplicationFactory<WebApplicationEntryPoint> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Browser_navigation_to_a_missing_page_renders_the_branded_404_screen()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/unknown-orbita-sector");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Страница не найдена", html);
        Assert.Contains("orbita-error-frame", html);
    }

    [Fact]
    public async Task Gateway_error_route_preserves_the_status_code_and_uses_the_same_screen()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = app.CreateClient();

        var response = await client.GetAsync("/error/502");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Сервис временно недоступен", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }
}
