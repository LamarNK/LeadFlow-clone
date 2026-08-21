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
    public void ScopeKey_UsesEffectiveOffice_NotRawFilterForManagers()
    {
        var officeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var manager = OfficeScope.ForOffice(officeId);
        var withNullFilter = PanelAggregateCache.SummaryKey(manager, officeFilter: null, timeZoneOffsetMinutes: 0);
        var withOwnFilter = PanelAggregateCache.SummaryKey(manager, officeFilter: officeId, timeZoneOffsetMinutes: 0);

        Assert.Equal(withNullFilter, withOwnFilter);
    }
}
