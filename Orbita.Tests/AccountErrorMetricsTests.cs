using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class AccountErrorMetricsTests
{
    [Fact]
    public void ComputeErrorCount_UsesTodayEventErrors_First()
    {
        var count = AccountErrorMetrics.ComputeErrorCount(
            todayEventErrors: 2,
            lastErrorMessage: "старая ошибка",
            subProfiles: [new SubProfileRowViewModel { HasIssue = true }],
            statusTone: "error");

        Assert.Equal(2, count);
    }

    [Fact]
    public void ComputeErrorCount_IncludesSubProfileIssues_WhenNoEvents()
    {
        var count = AccountErrorMetrics.ComputeErrorCount(
            todayEventErrors: 0,
            lastErrorMessage: null,
            subProfiles:
            [
                new SubProfileRowViewModel { HasIssue = true },
                new SubProfileRowViewModel { HasIssue = true }
            ],
            statusTone: "active");

        Assert.Equal(2, count);
    }

    [Fact]
    public void ComputeErrorHint_ShowsJournalSummary_WhenOnlyEventsExist()
    {
        var hint = AccountErrorMetrics.ComputeErrorHint(
            todayEventErrors: 2,
            lastErrorMessage: null,
            subProfiles: [],
            statusTone: "inactive");

        Assert.Equal("2 ошибки в журнале за сегодня", hint);
    }

    [Fact]
    public void HasErrors_IsTrue_ForStatusError_EvenWhenCountZeroInRow()
    {
        var account = new AccountRowViewModel
        {
            StatusTone = "error",
            Errors = 0
        };

        Assert.True(AccountErrorMetrics.HasErrors(account));
    }
}