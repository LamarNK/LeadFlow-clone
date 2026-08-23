using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringPassFailurePolicyTests
{
    [Theory]
    [InlineData(AvitoSubProfileIssueKind.Captcha, false)]
    [InlineData(AvitoSubProfileIssueKind.ProxyFailure, false)]
    [InlineData(AvitoSubProfileIssueKind.SwitchFailed, false)]
    [InlineData(AvitoSubProfileIssueKind.IpBlock, true)]
    [InlineData(AvitoSubProfileIssueKind.AuthRequired, true)]
    public void IsAccountBlockingIssueKind_MatchesPolicy(string kind, bool blocking) =>
        Assert.Equal(blocking, MonitoringPassFailurePolicy.IsAccountBlockingIssueKind(kind));

    [Fact]
    public void AccountStatusForIssueKind_Captcha_IsNull() =>
        Assert.Null(MonitoringPassFailurePolicy.AccountStatusForIssueKind(AvitoSubProfileIssueKind.Captcha));

    [Fact]
    public void AccountStatusForIssueKind_IpBlock_RequiresManualAction() =>
        Assert.Equal(
            AvitoAccountStatus.RequiresManualAction,
            MonitoringPassFailurePolicy.AccountStatusForIssueKind(AvitoSubProfileIssueKind.IpBlock));

    [Fact]
    public void ShouldStopRemainingAfterCaptcha_AfterTwo_IsTrue()
    {
        Assert.False(MonitoringPassFailurePolicy.ShouldStopRemainingAfterCaptcha(1));
        Assert.True(MonitoringPassFailurePolicy.ShouldStopRemainingAfterCaptcha(2));
    }
}
