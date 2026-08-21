using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

[Collection("PanelAggregateCache")]
public sealed class PanelAggregateCacheTests
{
    public PanelAggregateCacheTests() => PanelAggregateCache.Clear();

    [Fact]
    public async Task GetOrCreateAsync_CoalescesConcurrentFactoryCalls()
    {
        var started = 0;
        var factory = async () =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(50);
            return "value";
        };

        var first = PanelAggregateCache.GetOrCreateAsync("d:test", TimeSpan.FromSeconds(5), factory);
        var second = PanelAggregateCache.GetOrCreateAsync("d:test", TimeSpan.FromSeconds(5), factory);
        var results = await Task.WhenAll(first, second);

        Assert.Equal("value", results[0]);
        Assert.Equal("value", results[1]);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task Invalidate_DropsDashboardEntriesOnResponseChange()
    {
        await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () => Task.FromResult("stale"));
        PanelAggregateCache.Invalidate([PanelChangeKind.Responses]);

        var recomputed = 0;
        var fresh = await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () =>
        {
            recomputed++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("fresh", fresh);
        Assert.Equal(1, recomputed);
    }

    [Fact]
    public async Task Invalidate_KeepsDashboardCacheOnWorkersOnlyChange()
    {
        await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () => Task.FromResult("kept"));
        PanelAggregateCache.Invalidate([PanelChangeKind.Workers]);

        var recomputed = 0;
        var cached = await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () =>
        {
            recomputed++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("kept", cached);
        Assert.Equal(0, recomputed);
    }

    [Fact]
    public async Task Invalidate_DropsAccountsEntriesOnAccountsSignal()
    {
        await PanelAggregateCache.GetOrCreateAsync("a:office", TimeSpan.FromSeconds(30), () => Task.FromResult("stale"));
        PanelAggregateCache.Invalidate([PanelChangeKind.Accounts]);

        var recomputed = 0;
        var fresh = await PanelAggregateCache.GetOrCreateAsync("a:office", TimeSpan.FromSeconds(30), () =>
        {
            recomputed++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("fresh", fresh);
        Assert.Equal(1, recomputed);
    }

    [Fact]
    public async Task Invalidate_DropsAccountsEntriesOnWorkersSignal_WithoutTouchingDashboard()
    {
        await PanelAggregateCache.GetOrCreateAsync("a:office", TimeSpan.FromSeconds(30), () => Task.FromResult("stale-accounts"));
        await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () => Task.FromResult("kept-dashboard"));
        PanelAggregateCache.Invalidate([PanelChangeKind.Workers]);

        var accountsRecomputed = 0;
        var dashboardRecomputed = 0;
        var accounts = await PanelAggregateCache.GetOrCreateAsync("a:office", TimeSpan.FromSeconds(30), () =>
        {
            accountsRecomputed++;
            return Task.FromResult("fresh-accounts");
        });
        var dashboard = await PanelAggregateCache.GetOrCreateAsync("d:s:key", TimeSpan.FromSeconds(30), () =>
        {
            dashboardRecomputed++;
            return Task.FromResult("fresh-dashboard");
        });

        Assert.Equal("fresh-accounts", accounts);
        Assert.Equal("kept-dashboard", dashboard);
        Assert.Equal(1, accountsRecomputed);
        Assert.Equal(0, dashboardRecomputed);
    }

    [Fact]
    public async Task Invalidate_AccountsSignal_DoesNotDropStatistics()
    {
        await PanelAggregateCache.GetOrCreateAsync("s:stats", TimeSpan.FromSeconds(30), () => Task.FromResult("kept-stats"));
        PanelAggregateCache.Invalidate([PanelChangeKind.Accounts]);

        var recomputed = 0;
        var cached = await PanelAggregateCache.GetOrCreateAsync("s:stats", TimeSpan.FromSeconds(30), () =>
        {
            recomputed++;
            return Task.FromResult("fresh-stats");
        });

        Assert.Equal("kept-stats", cached);
        Assert.Equal(0, recomputed);
    }

    [Fact]
    public async Task GetOrCreateAsync_DoesNotStoreValueAfterInvalidationDuringFactory()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = PanelAggregateCache.GetOrCreateAsync("a:race", TimeSpan.FromSeconds(30), async () =>
        {
            entered.SetResult();
            await release.Task;
            return "slow";
        });

        await entered.Task;
        PanelAggregateCache.Invalidate([PanelChangeKind.Accounts]);
        release.SetResult();
        Assert.Equal("slow", await slow);

        var recomputed = 0;
        var next = await PanelAggregateCache.GetOrCreateAsync("a:race", TimeSpan.FromSeconds(30), () =>
        {
            recomputed++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("fresh", next);
        Assert.Equal(1, recomputed);
    }

    [Fact]
    public void ScopeKey_UsesEffectiveOffice_NotRawFilterForManagers()
    {
        var officeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var manager = OfficeScope.ForOffice(officeId);
        var day = new DateTime(2026, 8, 21);
        var withNullFilter = PanelAggregateCache.SummaryKey(
            manager,
            officeFilter: null,
            timeZoneOffsetMinutes: 0,
            day,
            day);
        var withOwnFilter = PanelAggregateCache.SummaryKey(
            manager,
            officeFilter: officeId,
            timeZoneOffsetMinutes: 0,
            day,
            day);

        Assert.Equal(withNullFilter, withOwnFilter);
    }
}
