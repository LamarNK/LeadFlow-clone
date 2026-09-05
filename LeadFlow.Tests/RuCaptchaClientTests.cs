using System.Net;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class RuCaptchaClientTests
{
    [Fact]
    public async Task Report_V1Correct_UsesReportGood()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Contains("/res.php", request.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("action=reportgood", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("id=81", request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(StubHttpMessageHandler.Ok("OK_REPORT_RECORDED"));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") });

        await client.ReportAsync("key", new RuCaptchaTask("81", RuCaptchaApiVersion.V1), isCorrect: true);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Report_V2Incorrect_UsesReportIncorrect()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            Assert.Contains("reportIncorrect", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("\"clientKey\":\"key\"", body, StringComparison.Ordinal);
            Assert.Contains("\"taskId\":81", body, StringComparison.Ordinal);
            return Task.FromResult(StubHttpMessageHandler.Ok("""{"errorId":0,"status":"success"}"""));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") });

        await client.ReportAsync("key", new RuCaptchaTask("81", RuCaptchaApiVersion.V2), isCorrect: false);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task SolveClickCaptcha_SendsImageAndHintThenPreservesPointOrder()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/in.php", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("method=base64", body, StringComparison.Ordinal);
                Assert.Contains("coordinatescaptcha=1", body, StringComparison.Ordinal);
                Assert.Contains("body=aW1hZ2U%3D", body, StringComparison.Ordinal);
                Assert.Contains("imginstructions=aGludA%3D%3D", body, StringComparison.Ordinal);
                Assert.Contains("textinstructions=Click+the+symbols+in+order", body, StringComparison.Ordinal);
                Assert.Contains("lang=ru", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("OK|81"));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("OK|x=43,y=87;x=120,y=15"));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") })
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            SolveTimeout = TimeSpan.FromSeconds(5)
        };

        var solution = await client.SolveClickCaptchaAsync(
            "key",
            "data:image/png;base64,aW1hZ2U=",
            "data:image/png;base64,aGludA==",
            "Click the symbols in order",
            "ru");

        Assert.Collection(
            solution.Points,
            point => Assert.Equal(new ClickCaptchaPoint(43, 87), point),
            point => Assert.Equal(new ClickCaptchaPoint(120, 15), point));
        Assert.Equal(new RuCaptchaTask("81", RuCaptchaApiVersion.V1), solution.ProviderTask);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData("x=43,y=87;x=120,y=15")]
    [InlineData("43,87;120,15")]
    [InlineData("coordinates:x=43,y=87;x=120,y=15")]
    public void ParseClickCaptchaCoordinates_PreservesOrder(string raw)
    {
        var points = RuCaptchaResponseParser.ParseClickCaptchaCoordinates(raw);

        Assert.Collection(
            points,
            point => Assert.Equal(new ClickCaptchaPoint(43, 87), point),
            point => Assert.Equal(new ClickCaptchaPoint(120, 15), point));
    }

    [Fact]
    public void ParseClickCaptchaCoordinates_RejectsMalformedAnswer()
    {
        Assert.Throws<RuCaptchaException>(() => RuCaptchaResponseParser.ParseClickCaptchaCoordinates("not-a-point"));
    }

    [Fact]
    public async Task SolveGeeTestV4_CreateThenReady()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/in.php", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("method=geetest_v4", body, StringComparison.Ordinal);
                Assert.Contains("2d9c743cf7d63dbc9db578a608196bcd", body, StringComparison.Ordinal);
                Assert.Contains("pageurl=https%3A%2F%2Fwww.avito.ru%2Fprofile%2Fcandidates", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("OK|42"));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("""
                OK|{"captcha_id":"2d9c743cf7d63dbc9db578a608196bcd","lot_number":"ln","pass_token":"pt","gen_time":"1","captcha_output":"co"}
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
        Assert.Equal(new RuCaptchaTask("42", RuCaptchaApiVersion.V1), solution.ProviderTask);
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

    [Fact]
    public async Task SolveGeeTestV4_ProfileContext_UsesMatchingProxyAndUserAgent()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/in.php", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("proxytype=SOCKS5", body, StringComparison.Ordinal);
                Assert.Contains("proxy=login%3Asecret%40203.0.113.10%3A1080", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("OK|77"));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("""
                OK|{"captcha_id":"id","lot_number":"ln","pass_token":"pt","gen_time":"1","captcha_output":"co"}
                """));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") })
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            SolveTimeout = TimeSpan.FromSeconds(5)
        };
        var options = GeeTestV4TaskOptions.FromBrowserProfile(
            "Mozilla/5.0 test", "socks5", "203.0.113.10:1080", "login", "secret");

        await client.SolveGeeTestV4Async("key", "https://www.avito.ru/profile/candidates", "id", options);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task SolveHCaptcha_ProfileContext_UsesMatchingProxyAndReturnsToken()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("createTask", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("\"type\":\"HCaptchaTask\"", body, StringComparison.Ordinal);
                Assert.Contains("\"websiteKey\":\"070db171-ddb9-4c93-b7f6-d25d3c9d7e28\"", body, StringComparison.Ordinal);
                Assert.Contains("\"proxyAddress\":\"203.0.113.10\"", body, StringComparison.Ordinal);
                Assert.Contains("\"userAgent\":\"Mozilla/5.0 test\"", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("""{"errorId":0,"taskId":78}"""));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("""
                {"errorId":0,"status":"ready","solution":{"gRecaptchaResponse":"hcaptcha-token"}}
                """));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") })
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            SolveTimeout = TimeSpan.FromSeconds(5)
        };
        var options = GeeTestV4TaskOptions.FromBrowserProfile(
            "Mozilla/5.0 test", "socks5", "203.0.113.10:1080", "login", "secret");

        var solution = await client.SolveHCaptchaAsync(
            "key", "https://www.avito.ru/profile/candidates", "070db171-ddb9-4c93-b7f6-d25d3c9d7e28", options);

        Assert.Equal("hcaptcha-token", solution.Token);
        Assert.Equal(new RuCaptchaTask("78", RuCaptchaApiVersion.V2), solution.ProviderTask);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task SolveImageToText_StripsDataUrlPrefixAndReturnsRecognizedText()
    {
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("createTask", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("\"type\":\"ImageToTextTask\"", body, StringComparison.Ordinal);
                Assert.Contains("\"body\":\"aGVsbG8=\"", body, StringComparison.Ordinal);
                Assert.DoesNotContain("data:image", body, StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Ok("""{"errorId":0,"taskId":79}"""));
            }

            return Task.FromResult(StubHttpMessageHandler.Ok("""
                {"errorId":0,"status":"ready","solution":{"text":"aB72"}}
                """));
        });
        var client = new RuCaptchaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.rucaptcha.com/") })
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            SolveTimeout = TimeSpan.FromSeconds(5)
        };

        var solution = await client.SolveImageToTextAsync("key", "data:image/png;base64,aGVsbG8=");

        Assert.Equal("aB72", solution.Text);
        Assert.Equal(new RuCaptchaTask("79", RuCaptchaApiVersion.V2), solution.ProviderTask);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(":1080")]
    [InlineData("ftp://127.0.0.1:21")]
    public void TaskOptions_InvalidProxy_FallsBackToProxyless(string address)
    {
        var options = GeeTestV4TaskOptions.FromBrowserProfile("UA", "http", address, null, null);

        Assert.False(options.UsesSuppliedProxy);
    }

    [Fact]
    public void PreferProxy_AdsPowerProfileHostPort_OverridesEmptyAccountFallback()
    {
        var options = new GeeTestV4TaskOptions("Mozilla/5.0 AdsPower")
            .PreferProxy("socks5", "203.0.113.10:1080", "login", "secret");

        Assert.True(options.UsesSuppliedProxy);
        Assert.Equal("socks5", options.Proxy!.Type);
        Assert.Equal("203.0.113.10", options.Proxy.Address);
        Assert.Equal(1080, options.Proxy.Port);
        Assert.Equal("login", options.Proxy.Login);
        Assert.Equal("Mozilla/5.0 AdsPower", options.UserAgent);
    }

    [Fact]
    public void PreferProxy_MissingAdsPowerProfile_KeepsAccountProxy()
    {
        var account = GeeTestV4TaskOptions.FromBrowserProfile(
            "UA", "http", "198.51.100.20:8080", "acc", "pwd");

        var merged = account.PreferProxy("socks5", null, "login", "secret");

        Assert.True(merged.UsesSuppliedProxy);
        Assert.Equal("http", merged.Proxy!.Type);
        Assert.Equal("198.51.100.20", merged.Proxy.Address);
        Assert.Equal(8080, merged.Proxy.Port);
    }

    [Fact]
    public void PreferProxy_UnparsedAdsPowerProfile_KeepsAccountProxy()
    {
        var account = GeeTestV4TaskOptions.FromBrowserProfile(
            "UA", "http", "198.51.100.20:8080", null, null);

        var merged = account.PreferProxy("ssh", "203.0.113.10:22", null, null);

        Assert.Equal("http", merged.Proxy!.Type);
        Assert.Equal("198.51.100.20", merged.Proxy.Address);
    }
}
