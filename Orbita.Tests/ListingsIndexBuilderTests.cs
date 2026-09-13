using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class ListingsIndexBuilderTests
{
    [Fact]
    public void Build_ShowsTableFiltersAndStateCounts()
    {
        var now = DateTime.UtcNow;
        var items = new List<AvitoAdListingListItem>
        {
            Item("1", AvitoAdListingStates.Active, true, now.AddDays(20)),
            Item("2", AvitoAdListingStates.ApproachingExpiry, true, now.AddDays(3)),
            Item("3", AvitoAdListingStates.ExpiresToday, true, now),
            Item("4", AvitoAdListingStates.UnknownPublicationDate, true, null),
            Item("5", AvitoAdListingStates.ParseFailed, true, null),
            Item("6", AvitoAdListingStates.NotActive, false, now.AddDays(-2))
        };
        var summary = new AvitoAdListingSummary(5, 1, 1, 1, 0);

        var all = ListingsIndexBuilder.Build(
            items,
            summary,
            searchQuery: null,
            tab: "all",
            page: 1,
            pageSize: 10,
            sort: "title",
            sortDir: "asc",
            workerIds: null,
            accountIds: null,
            subProfileIds: null,
            workers: [new EventFilterOptionViewModel { Value = "", Label = "Все воркеры" }],
            accounts: [new EventFilterOptionViewModel { Value = "", Label = "Все аккаунты" }],
            subProfiles: [new EventFilterOptionViewModel { Value = "", Label = "Все субпрофили" }]);

        Assert.Equal(6, all.Pagination.TotalItems);
        Assert.Equal(5, all.KpiCards.Single(x => x.Key == "active").CountValue);
        Assert.Equal(1, all.KpiCards.Single(x => x.Key == "unknown").CountValue);
        Assert.Equal(1, all.KpiCards.Single(x => x.Key == "expiring").CountValue);
        Assert.Equal(1, all.KpiCards.Single(x => x.Key == "today").CountValue);

        var expiring = ListingsIndexBuilder.Build(
            items, summary, null, "expiring", 1, 10, null, null, null, null, null,
            all.Workers, all.Accounts, all.SubProfiles);
        Assert.Equal(1, expiring.Pagination.TotalItems);
        Assert.Equal("Срок близко", expiring.Rows[0].StateLabel);

        var parseFailed = ListingsIndexBuilder.Build(
            items, summary, null, "parsefailed", 1, 10, null, null, null, null, null,
            all.Workers, all.Accounts, all.SubProfiles);
        Assert.Equal(1, parseFailed.Pagination.TotalItems);

        var workerA = items[0].WorkerId;
        var workerFiltered = ListingsIndexBuilder.Build(
            items, summary, null, "all", 1, 10, null, null, [workerA], null, null,
            all.Workers, all.Accounts, all.SubProfiles);
        Assert.Equal(items.Count(x => x.WorkerId == workerA), workerFiltered.Pagination.TotalItems);
        Assert.StartsWith("/Workers/Details/", workerFiltered.Rows[0].WorkerUrl);
        Assert.StartsWith("/Accounts?q=", workerFiltered.Rows[0].AccountUrl);
        Assert.Contains(workerFiltered.ActiveFilterChips, chip => chip.Label.StartsWith("Воркер:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithServerPagedItems_UsesFullSummaryAndTotal()
    {
        var now = DateTime.UtcNow;
        var currentPage = new List<AvitoAdListingListItem>
        {
            Item("101", AvitoAdListingStates.Active, true, now.AddDays(10)),
            Item("102", AvitoAdListingStates.ApproachingExpiry, true, now.AddDays(2))
        };
        var summary = new AvitoAdListingSummary(23_738, 569, 4_181, 0, 0);

        var model = ListingsIndexBuilder.Build(
            currentPage,
            summary,
            searchQuery: null,
            tab: "active",
            page: 2,
            pageSize: 2,
            sort: "expires",
            sortDir: "asc",
            workerIds: null,
            accountIds: null,
            subProfileIds: null,
            workers: [],
            accounts: [],
            subProfiles: [],
            totalItems: 23_738,
            itemsArePaged: true);

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(23_738, model.Pagination.TotalItems);
        Assert.Equal(23_738, model.KpiCards.Single(x => x.Key == "active").CountValue);
        Assert.Equal(569, model.KpiCards.Single(x => x.Key == "unknown").CountValue);
        Assert.Equal(4_181, model.KpiCards.Single(x => x.Key == "expiring").CountValue);
    }

    private static AvitoAdListingListItem Item(string id, string state, bool active, DateTime? expires) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker",
            Guid.NewGuid(),
            "Account",
            "sp",
            "Sub",
            id,
            "Title " + id,
            "https://www.avito.ru/perm/vakansii/title_" + id,
            active ? "Активно" : "",
            DateTime.UtcNow.AddDays(-10),
            expires,
            10,
            20,
            state,
            AvitoAdPublicationDateSources.Exact,
            DateTime.UtcNow,
            DateTime.UtcNow,
            active,
            state == AvitoAdListingStates.ParseFailed ? "bad" : null);
}
