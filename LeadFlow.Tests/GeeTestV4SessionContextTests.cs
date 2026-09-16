using System.Text.Json;
using LeadFlow.Core.Services.Captcha;
using Xunit;

namespace LeadFlow.Tests;

public sealed class GeeTestV4SessionContextTests
{
    [Fact]
    public void Parse_CombinesQueryFormAndJsonp()
    {
        var capturedAt = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        var context = GeeTestV4SessionContextParser.Parse(
            "https://gcaptcha4.geetest.com/load?captcha_id=live-id",
            "challenge=challenge-value",
            "geetest_1({\"data\":{\"risk_type\":\"slide|dynamic-value\"}})",
            "response",
            capturedAt);

        Assert.NotNull(context);
        Assert.Equal("live-id", context.CaptchaId);
        Assert.Equal("challenge-value", context.Challenge);
        Assert.Equal("slide|dynamic-value", context.RiskType);
        Assert.Equal(64, context.Fingerprint.Length);
        Assert.DoesNotContain("challenge-value", context.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic-value", context.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsNestedFirewallJson()
    {
        var context = GeeTestV4SessionContextParser.Parse(
            "https://www.avito.ru/web/5/firewallCaptcha/get",
            null,
            """
            {"success":{"result":{"captcha":{"geeTest":{"captcha_id":"abc","challenge":"def","risk_type":"slide|ghi"}}}}}
            """,
            "firewall-response",
            DateTime.UtcNow);

        Assert.Equal("abc", context?.CaptchaId);
        Assert.True(context?.HasChallenge);
        Assert.True(context?.HasRiskType);
    }

    [Fact]
    public void Diagnostics_NeverContainsRawDynamicValues()
    {
        var capturedAt = DateTime.UtcNow.AddSeconds(-2);
        var context = new GeeTestV4SessionContext("id", "secret-challenge", "secret-risk", "request", capturedAt);

        var json = JsonSerializer.Serialize(context.ToDiagnostics(DateTime.UtcNow));

        Assert.DoesNotContain("secret-challenge", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-risk", json, StringComparison.Ordinal);
        Assert.Contains("Fingerprint", json, StringComparison.Ordinal);
        Assert.Contains("ChallengePresent", json, StringComparison.Ordinal);
        Assert.Contains("RiskTypePresent", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_PrefersFreshNonEmptyValues()
    {
        var first = new GeeTestV4SessionContext("id-1", "challenge-1", null, "request", DateTime.UtcNow.AddSeconds(-1));
        var second = new GeeTestV4SessionContext("id-2", null, "risk-2", "response", DateTime.UtcNow);

        var merged = GeeTestV4SessionContextParser.Merge(first, second);

        Assert.Equal("id-2", merged.CaptchaId);
        Assert.Equal("challenge-1", merged.Challenge);
        Assert.Equal("risk-2", merged.RiskType);
        Assert.Equal("request+response", merged.Source);
    }

    [Theory]
    [InlineData("geetest_request", false)]
    [InlineData("firewall_request", false)]
    [InlineData("fallback", false)]
    [InlineData("geetest_response", true)]
    [InlineData("geetest_request+geetest_response", true)]
    [InlineData("firewall_request+firewall_response", true)]
    [InlineData("geetest_request+geetest_response+firewall_response", true)]
    public void HasCapturedResponse_DetectsResponseStage(string source, bool expected)
    {
        var context = new GeeTestV4SessionContext("id", "challenge", null, source, DateTime.UtcNow);

        Assert.Equal(expected, context.HasCapturedResponse);
    }

    [Fact]
    public void AttemptTracker_RejectsDuplicateAndDetectsChangedContext()
    {
        var tracker = new GeeTestV4AttemptContextTracker();
        var first = new GeeTestV4SessionContext("id", "challenge-1", "risk-1", "request", DateTime.UtcNow);
        var duplicate = first with { Source = "response", CapturedAtUtc = DateTime.UtcNow.AddSeconds(1) };
        var changed = first with { Challenge = "challenge-2", CapturedAtUtc = DateTime.UtcNow.AddSeconds(2) };

        Assert.True(tracker.TryUse(first));
        Assert.False(tracker.TryUse(duplicate));
        Assert.True(tracker.TryUse(changed));
        Assert.True(GeeTestV4AttemptContextTracker.IsCurrent(first, duplicate));
        Assert.False(GeeTestV4AttemptContextTracker.IsCurrent(first, changed));
    }
}
