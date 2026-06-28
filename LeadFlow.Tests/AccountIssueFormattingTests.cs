using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AccountIssueFormattingTests
{
    [Fact]
    public void FormatIssue_IncludesSubProfileAndKind()
    {
        var account = new AvitoAccount { DisplayName = "Кабинет 1" };
        var sub = new AvitoSubProfile { Id = "sp-1", Name = "Служба 3" };

        var message = AccountIssueFormatting.FormatIssue(
            account,
            sub,
            AvitoSubProfileIssueKind.Captcha,
            "нужна проверка.");

        Assert.Contains("Служба 3", message);
        Assert.Contains("Кабинет 1", message);
        Assert.Contains("капча", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("нужна проверка", message);
    }

    [Fact]
    public void SubProfileIssue_PersistsInJsonRoundTrip()
    {
        var account = new AvitoAccount { DisplayName = "Test" };
        var sub = new AvitoSubProfile { Id = "a", Name = "Sub A" };
        account.SetSubProfiles([sub]);

        AccountIssueTracker.ApplySubProfileIssue(
            account,
            sub,
            AvitoSubProfileIssueKind.Timeout,
            "таймаут загрузки.");

        var restored = new AvitoAccount { SubProfilesJson = account.SubProfilesJson };
        var restoredSub = restored.SubProfiles.Single();

        Assert.True(restoredSub.HasIssue);
        Assert.Equal(AvitoSubProfileIssueKind.Timeout, restoredSub.LastIssueKind);
        Assert.Contains("таймаут", restoredSub.LastIssueMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(restoredSub.LastIssueAt);
    }

    [Fact]
    public void RefreshAccountIssueMessage_UpdatesSummary_WhenMultipleSubProfilesHaveIssues()
    {
        var account = new AvitoAccount { DisplayName = "Cabinet" };
        var subA = new AvitoSubProfile { Id = "a", Name = "Sub A" };
        var subB = new AvitoSubProfile { Id = "b", Name = "Sub B" };
        account.SetSubProfiles([subA, subB]);

        AccountIssueTracker.ApplySubProfileIssue(account, subA, AvitoSubProfileIssueKind.Timeout, "таймаут A.");
        AccountIssueTracker.ApplySubProfileIssue(account, subB, AvitoSubProfileIssueKind.SwitchFailed, "не переключился.");

        Assert.Contains("Sub A", account.LastErrorMessage);
        Assert.Contains("Sub B", account.LastErrorMessage);
    }

    [Fact]
    public void RefreshAccountIssueMessage_ClearsMessage_WhenAllSubProfilesBecomeHealthy()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.Authorized
        };
        var sub = new AvitoSubProfile { Id = "a", Name = "Sub A" };
        account.SetSubProfiles([sub]);

        AccountIssueTracker.ApplySubProfileIssue(account, sub, AvitoSubProfileIssueKind.Timeout, "таймаут.");
        AccountIssueTracker.ClearSubProfileIssue(sub);
        account.SetSubProfiles([sub]);
        AccountIssueTracker.RefreshAccountIssueMessage(account);

        Assert.False(account.HasSubProfileIssues);
        Assert.Equal(string.Empty, account.LastErrorMessage);
    }
}