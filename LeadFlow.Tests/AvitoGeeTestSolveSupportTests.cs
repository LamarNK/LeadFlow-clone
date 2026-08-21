using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoGeeTestSolveSupportTests
{
    [Fact]
    public void CanAutoSolve_RequiresKeyAndWidget()
    {
        const string html = """<div class="geetest_widget" data-geetest="2d9c743cf7d63dbc9db578a608196bcd"></div>""";

        Assert.False(AvitoGeeTestSolveSupport.CanAutoSolve(html, apiKey: null));
        Assert.False(AvitoGeeTestSolveSupport.CanAutoSolve(html: null, apiKey: "key"));
        Assert.True(AvitoGeeTestSolveSupport.CanAutoSolve(html, "key"));
    }

    [Fact]
    public void BuildVerifyScript_ContainsAllSolutionFields()
    {
        var solution = new GeeTestV4Solution(
            CaptchaId: "2d9c743cf7d63dbc9db578a608196bcd",
            LotNumber: "lot-abc",
            PassToken: "pass-xyz",
            GenTime: "1693924478",
            CaptchaOutput: "out==");

        var script = AvitoGeeTestSolveSupport.BuildVerifyScript(solution);

        Assert.Contains("/web/1/firewallCaptcha/verify", script, StringComparison.Ordinal);
        Assert.Contains("lot-abc", script, StringComparison.Ordinal);
        Assert.Contains("pass-xyz", script, StringComparison.Ordinal);
        Assert.Contains("1693924478", script, StringComparison.Ordinal);
        Assert.Contains("out==", script, StringComparison.Ordinal);
        Assert.Contains("2d9c743cf7d63dbc9db578a608196bcd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractGeeTestCaptchaId_FromDataAttribute()
    {
        const string html = """<div class="geetest_widget" data-geetest="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"></div>""";

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", AvitoCaptchaDetector.ExtractGeeTestCaptchaId(html));
    }

    [Fact]
    public void ExtractGeeTestCaptchaId_FallbackToAvitoConstant()
    {
        Assert.Equal(
            AvitoCaptchaDetector.AvitoGeeTestCaptchaId,
            AvitoCaptchaDetector.ExtractGeeTestCaptchaId("<div id=\"geetest_captcha\"></div>"));
    }
}
