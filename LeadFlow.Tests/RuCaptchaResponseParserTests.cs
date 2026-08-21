using LeadFlow.Core.Services.Captcha;
using Xunit;

namespace LeadFlow.Tests;

public sealed class RuCaptchaResponseParserTests
{
    [Fact]
    public void ParseCreateTaskId_ReturnsTaskId()
    {
        const string json = """{"errorId":0,"taskId":748392}""";

        Assert.Equal(748392, RuCaptchaResponseParser.ParseCreateTaskId(json));
    }

    [Fact]
    public void ParseCreateTaskId_ApiError_ThrowsWithCode()
    {
        const string json = """
            {"errorId":1,"errorCode":"ERROR_ZERO_BALANCE","errorDescription":"Нулевой баланс"}
            """;

        var ex = Assert.Throws<RuCaptchaException>(() => RuCaptchaResponseParser.ParseCreateTaskId(json));
        Assert.Equal("ERROR_ZERO_BALANCE", ex.ErrorCode);
        Assert.Contains("баланс", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTaskResult_Processing_IsPending()
    {
        const string json = """{"errorId":0,"status":"processing"}""";

        var solution = RuCaptchaResponseParser.ParseTaskResult(json, out var pending);

        Assert.True(pending);
        Assert.Null(solution);
    }

    [Fact]
    public void ParseTaskResult_Ready_ReturnsFourFields()
    {
        const string json = """
            {
              "errorId": 0,
              "status": "ready",
              "solution": {
                "captcha_id": "2d9c743cf7d63dbc9db578a608196bcd",
                "lot_number": "lot-1",
                "pass_token": "pass-1",
                "gen_time": "1693924478",
                "captcha_output": "out-1"
              }
            }
            """;

        var solution = RuCaptchaResponseParser.ParseTaskResult(json, out var pending);

        Assert.False(pending);
        Assert.NotNull(solution);
        Assert.Equal("2d9c743cf7d63dbc9db578a608196bcd", solution!.CaptchaId);
        Assert.Equal("lot-1", solution.LotNumber);
        Assert.Equal("pass-1", solution.PassToken);
        Assert.Equal("1693924478", solution.GenTime);
        Assert.Equal("out-1", solution.CaptchaOutput);
    }

    [Fact]
    public void ParseTaskResult_MissingField_Throws()
    {
        const string json = """
            {"errorId":0,"status":"ready","solution":{"lot_number":"x","pass_token":"y"}}
            """;

        Assert.Throws<RuCaptchaException>(() => RuCaptchaResponseParser.ParseTaskResult(json, out _));
    }

    [Fact]
    public void ParseHCaptchaTaskResult_Ready_ReturnsResponseToken()
    {
        const string json = """
            {"errorId":0,"status":"ready","solution":{"gRecaptchaResponse":"hcaptcha-token"}}
            """;

        var solution = RuCaptchaResponseParser.ParseHCaptchaTaskResult(json, out var pending);

        Assert.False(pending);
        Assert.NotNull(solution);
        Assert.Equal("hcaptcha-token", solution!.Token);
    }

    [Fact]
    public void ParseImageToTextTaskResult_Ready_ReturnsRecognizedText()
    {
        const string json = """
            {"errorId":0,"status":"ready","solution":{"text":"aB72"}}
            """;

        var solution = RuCaptchaResponseParser.ParseImageToTextTaskResult(json, out var pending);

        Assert.False(pending);
        Assert.NotNull(solution);
        Assert.Equal("aB72", solution!.Text);
    }

    [Fact]
    public void IsVerifyAccepted_VerifiedTrue()
    {
        const string raw = """{"ok":true,"status":200,"text":"{\"verified\":true}"}""";

        Assert.True(RuCaptchaResponseParser.IsVerifyAccepted(raw));
    }

    [Fact]
    public void IsVerifyAccepted_WrapperVerifiedFlag()
    {
        const string raw = """{"ok":true,"status":200,"verified":true,"text":"{}"}""";

        Assert.True(RuCaptchaResponseParser.IsVerifyAccepted(raw));
    }

    [Fact]
    public void IsVerifyAccepted_AvitoSuccessResult()
    {
        const string raw = """{"ok":true,"status":200,"text":"{\"success\":{\"result\":{\"verified\":true}}}"}""";

        Assert.True(RuCaptchaResponseParser.IsVerifyAccepted(raw));
    }

    [Fact]
    public void IsVerifyAccepted_VerifiedFalse()
    {
        const string raw = """{"ok":true,"status":200,"text":"{\"verified\":false}"}""";

        Assert.False(RuCaptchaResponseParser.IsVerifyAccepted(raw));
    }
}
