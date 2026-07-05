using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class AccountDashboardStatusClassifierTests
{
    [Theory]
    [InlineData("Active", true, AccountDashboardCategory.Active)]
    [InlineData("Monitoring", true, AccountDashboardCategory.Active)]
    [InlineData("Paused", true, AccountDashboardCategory.Inactive)]
    [InlineData("Inactive", true, AccountDashboardCategory.Inactive)]
    [InlineData("Offline", true, AccountDashboardCategory.Inactive)]
    [InlineData("Active", false, AccountDashboardCategory.Inactive)]
    [InlineData("", false, AccountDashboardCategory.Inactive)]
    [InlineData("Blocked", true, AccountDashboardCategory.Inactive)]
    [InlineData("Error", true, AccountDashboardCategory.Error)]
    [InlineData("RequiresLogin", true, AccountDashboardCategory.Error)]
    [InlineData("RequiresManualAction", true, AccountDashboardCategory.Error)]
    public void Classify_MatchesAccountsPageRules(string status, bool isEnabledInPanel, AccountDashboardCategory expected)
    {
        Assert.Equal(expected, AccountDashboardStatusClassifier.Classify(status, isEnabledInPanel));
    }

    [Fact]
    public void Summarize_CountsNewWorkerAccountsAsInactive()
    {
        var accounts = Enumerable.Repeat(("Paused", false), 5)
            .Concat(Enumerable.Repeat(("", false), 2));

        var breakdown = AccountDashboardStatusClassifier.Summarize(accounts);

        Assert.Equal(7, breakdown.Total);
        Assert.Equal(0, breakdown.Active);
        Assert.Equal(7, breakdown.Inactive);
        Assert.Equal(0, breakdown.Blocked);
        Assert.Equal(0, breakdown.Errors);
    }

    [Fact]
    public void Summarize_SplitsMixedStatuses()
    {
        var accounts = new (string Status, bool IsEnabledInPanel)[]
        {
            ("Active", true),
            ("Monitoring", true),
            ("Paused", true),
            ("Blocked", true),
            ("Error", true),
            ("RequiresLogin", true)
        };

        var breakdown = AccountDashboardStatusClassifier.Summarize(accounts);

        Assert.Equal(6, breakdown.Total);
        Assert.Equal(4, breakdown.Active);
        Assert.Equal(2, breakdown.Inactive);
        Assert.Equal(0, breakdown.Blocked);
        Assert.Equal(2, breakdown.Errors);
    }

    [Theory]
    [InlineData("Active", true, true)]
    [InlineData("Monitoring", true, true)]
    [InlineData("Error", true, true)]
    [InlineData("RequiresLogin", true, true)]
    [InlineData("RequiresManualAction", true, true)]
    [InlineData("Paused", true, false)]
    [InlineData("Inactive", true, false)]
    [InlineData("Blocked", true, false)]
    [InlineData("Active", false, false)]
    [InlineData("Error", false, false)]
    public void IsActiveInPanel_MatchesAccountsPageActiveTab(
        string status,
        bool isEnabledInPanel,
        bool expected)
    {
        Assert.Equal(expected, AccountDashboardStatusClassifier.IsActiveInPanel(status, isEnabledInPanel));
    }
}