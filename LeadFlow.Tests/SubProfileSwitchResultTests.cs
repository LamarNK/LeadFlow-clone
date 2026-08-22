using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class SubProfileSwitchResultTests
{
    [Fact]
    public void Classify_Firewall_IsIpBlock()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Captcha,
            "https://www.avito.ru/",
            "Доступ ограничен",
            false,
            0,
            null,
            null,
            0,
            false,
            true,
            HasFirewallIp: true);
        Assert.Equal(SubProfileSwitchStatus.IpBlock, SubProfileSwitchResult.Classify(state, null));
    }

    [Fact]
    public void Classify_ModalStep_IsModalNotReady() =>
        Assert.Equal(
            SubProfileSwitchStatus.ModalNotReady,
            SubProfileSwitchResult.Classify(null, "switch_modal_not_ready"));

    [Fact]
    public void ShouldDefer_ModalAndClick_NotLogin()
    {
        Assert.True(SubProfileSwitchResult.ShouldDeferRetryStatus(SubProfileSwitchStatus.ModalNotReady));
        Assert.True(SubProfileSwitchResult.ShouldDeferRetryStatus(SubProfileSwitchStatus.ClickFailed));
        Assert.False(SubProfileSwitchResult.ShouldDeferRetryStatus(SubProfileSwitchStatus.Login));
        Assert.False(SubProfileSwitchResult.ShouldDeferRetryStatus(SubProfileSwitchStatus.IpBlock));
        Assert.False(new SubProfileSwitchResult(SubProfileSwitchStatus.Captcha).ShouldDeferRetry);
    }

    [Fact]
    public void Journal_DeferredRetry_MarksRepeat()
    {
        var result = new SubProfileSwitchResult(SubProfileSwitchStatus.ModalNotReady);
        Assert.Equal("switch-failed", result.JournalErrorType);
        Assert.Contains("повтор", result.JournalMessage(deferredRetry: false), StringComparison.Ordinal);
        Assert.DoesNotContain("повтор", result.JournalMessage(deferredRetry: true), StringComparison.Ordinal);
    }
}
