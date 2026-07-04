using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class AccountsIndexBuilderTests
{
    [Fact]
    public void Build_ErrorsKpiCountsAccountsWithLastErrorMessage_EvenWhenStatusIsActive()
    {
        var rows = new List<AccountRowViewModel>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "active-with-error",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "active",
                Errors = 1
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "healthy",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "active",
                Errors = 0
            }
        };

        var model = AccountsIndexBuilder.Build(rows, searchQuery: null, tab: "all", page: 1);

        var errorsKpi = model.KpiCards.Single(k => k.Key == "errors");
        Assert.Equal(1, errorsKpi.CountValue);
    }

    [Fact]
    public void Build_ErrorsTabMatchesErrorsKpiCount()
    {
        var rows = new List<AccountRowViewModel>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "status-error",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "error",
                Errors = 0
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "active-with-error",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "active",
                Errors = 2
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "healthy",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "active",
                Errors = 0
            }
        };

        var model = AccountsIndexBuilder.Build(rows, searchQuery: null, tab: "errors", page: 1);
        var errorsKpi = model.KpiCards.Single(k => k.Key == "errors");

        Assert.Equal(2, errorsKpi.CountValue);
        Assert.Equal(2, model.Pagination.TotalItems);
        Assert.Equal(2, model.Accounts.Count);
    }
}