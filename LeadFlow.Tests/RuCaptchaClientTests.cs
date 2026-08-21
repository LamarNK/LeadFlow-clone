using System.Net;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class RuCaptchaClientTests
{
    [Fact]
    public async Task SolveGeeTestV4_CreateThenReady()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("createTask", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("GeeTestTaskProxyless", body, StringComparison.Ordinal);
                Assert.Contains("\"version\":4", body, StringComparison.Ordinal);
                Assert.Contains("2d9c743cf7d63dbc9db578a608196bcd", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("""{"errorId":0,"taskId":42}"""));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("""
                {"errorId":0,"status":"ready","solution":{
                  "captcha_id":"2d9c743cf7d63dbc9db578a608196bcd",
                  "lot_number":"ln","pass_token":"pt","gen_time":"1","captcha_output":"co"
                }}
                """));
        });

        var client = new RuCaptchaClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.rucaptcha.com/")
        })
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            SolveTimeout = TimeSpan.FromSeconds(5)
        };

        var solution = await client.SolveGeeTestV4Async(
            "test-key",
            "https://www.avito.ru/profile/candidates",
            "2d9c743cf7d63dbc9db578a608196bcd");

        Assert.Equal("ln", solution.LotNumber);
        Assert.Equal("pt", solution.PassToken);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task SolveGeeTestV4_EmptyKey_Throws()
    {
        var client = new RuCaptchaClient(new HttpClient());
        await Assert.ThrowsAsync<RuCaptchaException>(() =>
            client.SolveGeeTestV4Async(" ", "https://www.avito.ru/", "id"));
    }

    [Fact]
    public async Task SolveGeeTestV4_HttpError_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, "oops")));
        var client = new RuCaptchaClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.rucaptcha.com/")
        });

        await Assert.ThrowsAsync<RuCaptchaException>(() =>
            client.SolveGeeTestV4Async("key", "https://www.avito.ru/", "id"));
    }
}
