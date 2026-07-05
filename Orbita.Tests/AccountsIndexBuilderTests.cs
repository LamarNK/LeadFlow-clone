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

    [Fact]
    public void Build_WorkerFilter_LimitsRowsAndKpi()
    {
        var workerA = Guid.NewGuid();
        var workerB = Guid.NewGuid();
        var rows = new List<AccountRowViewModel>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "worker-a",
                WorkerId = workerA,
                WorkerName = "Worker A",
                StatusTone = "active",
                IsEnabledInPanel = true
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "worker-b",
                WorkerId = workerB,
                WorkerName = "Worker B",
                StatusTone = "active",
                IsEnabledInPanel = true
            }
        };

        var model = AccountsIndexBuilder.Build(
            rows,
            searchQuery: null,
            tab: "all",
            page: 1,
            workerId: workerA);

        Assert.Single(model.Accounts);
        Assert.Equal("worker-a", model.Accounts[0].AccountName);
        Assert.Equal(1, model.KpiCards.Single(k => k.Key == "total").CountValue);
        Assert.True(model.HasActiveFilters);
    }

    [Fact]
    public void Build_ActiveTab_IncludesEnabledAccountWithErrorStatus()
    {
        var rows = new List<AccountRowViewModel>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "enabled-error",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "error",
                IsEnabledInPanel = true,
                Errors = 1
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "disabled-error",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "error",
                IsEnabledInPanel = false,
                Errors = 1
            },
            new()
            {
                Id = Guid.NewGuid(),
                AccountName = "healthy",
                WorkerId = Guid.NewGuid(),
                WorkerName = "Worker-1",
                StatusTone = "active",
                IsEnabledInPanel = true
            }
        };

        var model = AccountsIndexBuilder.Build(rows, searchQuery: null, tab: "active", page: 1);
        var activeKpi = model.KpiCards.Single(k => k.Key == "active");

        Assert.Equal(2, activeKpi.CountValue);
        Assert.Equal(2, model.Pagination.TotalItems);
        Assert.Contains(model.Accounts, a => a.AccountName == "enabled-error");
        Assert.Contains(model.Accounts, a => a.AccountName == "healthy");
        Assert.DoesNotContain(model.Accounts, a => a.AccountName == "disabled-error");
    }
}