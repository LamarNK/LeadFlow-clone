using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class TableSortTests
{
    [Fact]
    public void Parse_InvalidColumn_ReturnsDefault()
    {
        var result = TableSort.Parse("unknown", "asc", TableSort.Events.Default, TableSort.Events.Columns);

        Assert.Equal("time", result.Column);
        Assert.True(result.Descending);
    }

    [Fact]
    public void Parse_ValidColumnWithDir_ReturnsRequestedSort()
    {
        var result = TableSort.Parse("worker", "asc", TableSort.Accounts.Default, TableSort.Accounts.Columns);

        Assert.Equal("worker", result.Column);
        Assert.False(result.Descending);
    }

    [Fact]
    public void Parse_ValidColumnWithoutDir_UsesDefaultDirectionForColumn()
    {
        var result = TableSort.Parse("time", null, TableSort.Events.Default, TableSort.Events.Columns);

        Assert.Equal("time", result.Column);
        Assert.True(result.Descending);
    }

    [Fact]
    public void AccountsApply_SortsByBalanceDescending()
    {
        var rows = new[]
        {
            new AccountRowViewModel { AccountName = "a", Balance = 10m },
            new AccountRowViewModel { AccountName = "b", Balance = 30m },
            new AccountRowViewModel { AccountName = "c", Balance = 20m }
        };

        var sorted = TableSort.Accounts.Apply(rows, TableSortState.Create("balance", descending: true)).ToList();

        Assert.Equal(["b", "c", "a"], sorted.Select(x => x.AccountName).ToArray());
    }

    [Fact]
    public void EventsApply_SortsByDescriptionAscending()
    {
        var rows = new[]
        {
            new EventRowViewModel { Description = "gamma", OccurredAtUtc = DateTime.UtcNow },
            new EventRowViewModel { Description = "alpha", OccurredAtUtc = DateTime.UtcNow },
            new EventRowViewModel { Description = "beta", OccurredAtUtc = DateTime.UtcNow }
        };

        var sorted = TableSort.Events.Apply(rows, TableSortState.Create("description", descending: false)).ToList();

        Assert.Equal(["alpha", "beta", "gamma"], sorted.Select(x => x.Description).ToArray());
    }

    [Fact]
    public void Toggle_ActiveAscendingColumn_ReturnsDescending()
    {
        var current = TableSortState.Create("account", descending: false);

        var (column, dir) = TableSort.Toggle("account", current);

        Assert.Equal("account", column);
        Assert.Equal("desc", dir);
    }

    [Fact]
    public void ResponsesApply_SortsByCityAscending()
    {
        var rows = new[]
        {
            new ResponseRowViewModel { FullName = "a", City = "Казань", CreatedAtUtc = DateTime.UtcNow },
            new ResponseRowViewModel { FullName = "b", City = "Москва", CreatedAtUtc = DateTime.UtcNow },
            new ResponseRowViewModel { FullName = "c", City = "Екатеринбург", CreatedAtUtc = DateTime.UtcNow }
        };

        var sorted = TableSort.Responses
            .Apply(rows, TableSortState.Create("city", descending: false))
            .ToList();

        Assert.Equal(["Екатеринбург", "Казань", "Москва"], sorted.Select(x => x.City).ToArray());
    }

    [Fact]
    public void WorkerAccountsApply_SortsByResponsesDescending()
    {
        var rows = new[]
        {
            new WorkerAccountRowViewModel { DisplayName = "a", Responses = 5 },
            new WorkerAccountRowViewModel { DisplayName = "b", Responses = 15 },
            new WorkerAccountRowViewModel { DisplayName = "c", Responses = 10 }
        };

        var sorted = TableSort.WorkerAccounts
            .Apply(rows, TableSortState.Create("responses", descending: true))
            .ToList();

        Assert.Equal(["b", "c", "a"], sorted.Select(x => x.DisplayName).ToArray());
    }
}