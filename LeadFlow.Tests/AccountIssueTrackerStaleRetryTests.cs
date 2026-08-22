using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AccountIssueTrackerStaleRetryTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RefreshAccountIssueMessage_CaptchaOnSubProfile_DoesNotLockAccount()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.Authorized
        };
        var sub = new AvitoSubProfile { Id = "sp-1", Name = "Sub A" };
        account.SetSubProfiles([sub]);
        AccountIssueTracker.ApplySubProfileIssue(account, sub, AvitoSubProfileIssueKind.Captcha, "нужна проверка");

        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
        Assert.True(account.HasSubProfileIssues);
    }

    [Fact]
    public void RefreshAccountIssueMessage_IpBlockOnSubProfile_LocksAccount()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.Authorized
        };
        var sub = new AvitoSubProfile { Id = "sp-1", Name = "Sub A" };
        account.SetSubProfiles([sub]);
        AccountIssueTracker.ApplySubProfileIssue(account, sub, AvitoSubProfileIssueKind.IpBlock, "блок IP");

        Assert.Equal(AvitoAccountStatus.RequiresManualAction, account.Status);
    }

    [Fact]
    public void TryClearStaleBlockingState_FreshCaptchaIssue_StaysBlocked()
    {
        var account = BuildCaptchaAccount(Now.AddHours(-2));

        Assert.False(AccountIssueTracker.TryClearStaleBlockingState(account, Now));
        Assert.Equal(AvitoAccountStatus.RequiresManualAction, account.Status);
        Assert.True(account.HasSubProfileIssues);
    }

    [Fact]
    public void TryClearStaleBlockingState_OldCaptchaIssue_ClearsAndAuthorizes()
    {
        var account = BuildCaptchaAccount(Now.AddHours(-8));

        Assert.True(AccountIssueTracker.TryClearStaleBlockingState(account, Now));
        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
        Assert.False(account.HasSubProfileIssues);
        Assert.Equal(string.Empty, account.LastErrorMessage);
    }

    [Fact]
    public void TryClearStaleBlockingState_OldLoginIssue_ClearsAndAuthorizes()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.RequiresLogin,
            LastErrorMessage = "требуется вход",
            LastMonitoringAt = Now.AddHours(-10)
        };

        Assert.True(AccountIssueTracker.TryClearStaleBlockingState(account, Now));
        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
        Assert.Equal(string.Empty, account.LastErrorMessage);
    }

    [Fact]
    public void TryClearStaleBlockingState_PausedStatus_IsNotCleared()
    {
        var account = new AvitoAccount
        {
            Status = AvitoAccountStatus.Paused,
            LastMonitoringAt = Now.AddDays(-2)
        };

        Assert.False(AccountIssueTracker.TryClearStaleBlockingState(account, Now));
        Assert.Equal(AvitoAccountStatus.Paused, account.Status);
    }

    [Fact]
    public void TryClearStaleAccountErrorMessage_OldTransientError_OnAuthorizedAccount_ClearsMessage()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.Authorized,
            LastErrorMessage = "AdsPower: rate limit",
            LastMonitoringAt = Now.AddDays(-4)
        };

        Assert.True(AccountIssueTracker.TryClearStaleAccountErrorMessage(account, Now));
        Assert.Equal(string.Empty, account.LastErrorMessage);
        Assert.Equal(AvitoAccountStatus.Authorized, account.Status);
    }

    [Fact]
    public void TryClearStaleAccountErrorMessage_FreshError_StaysVisible()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.Authorized,
            LastErrorMessage = "AdsPower: rate limit",
            LastMonitoringAt = Now.AddDays(-2)
        };

        Assert.False(AccountIssueTracker.TryClearStaleAccountErrorMessage(account, Now));
        Assert.Equal("AdsPower: rate limit", account.LastErrorMessage);
    }

    [Fact]
    public void TryClearStaleBlockingState_NoTimestamp_StaysBlocked()
    {
        var account = new AvitoAccount
        {
            Status = AvitoAccountStatus.RequiresManualAction,
            LastErrorMessage = "капча"
        };

        Assert.False(AccountIssueTracker.TryClearStaleBlockingState(account, Now));
        Assert.Equal(AvitoAccountStatus.RequiresManualAction, account.Status);
    }

    private static AvitoAccount BuildCaptchaAccount(DateTime issueAtUtc)
    {
        var account = new AvitoAccount
        {
            DisplayName = "Cabinet",
            Status = AvitoAccountStatus.RequiresManualAction,
            LastMonitoringAt = issueAtUtc
        };
        var sub = new AvitoSubProfile
        {
            Id = "sp-1",
            Name = "Sub A",
            LastIssueKind = AvitoSubProfileIssueKind.Captcha,
            LastIssueMessage = "нужна проверка",
            LastIssueAt = issueAtUtc
        };
        account.SetSubProfiles([sub]);
        AccountIssueTracker.RefreshAccountIssueMessage(account);
        return account;
    }
}