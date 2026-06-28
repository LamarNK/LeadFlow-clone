using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ActiveAdsSnapshotAggregatorTests
{
    [Fact]
    public void AggregateFromAccounts_CombinesPerAccountSnapshots_23_100_77()
    {
        var id1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var id2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var id3 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var acc1 = new AvitoAccount
        {
            Id = id1,
            DisplayName = "Acc1",
            ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
                Enumerable.Range(1, 23).Select(i => new AvitoAdStatus { Id = $"a{i}", Title = "x" }).ToList())
        };
        var acc2 = new AvitoAccount
        {
            Id = id2,
            DisplayName = "Acc2",
            ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
                Enumerable.Range(1, 100).Select(i => new AvitoAdStatus { Id = $"b{i}", Title = "y" }).ToList())
        };
        var acc3 = new AvitoAccount
        {
            Id = id3,
            DisplayName = "Acc3",
            ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
                Enumerable.Range(1, 77).Select(i => new AvitoAdStatus { Id = $"c{i}", Title = "z" }).ToList())
        };

        var merged = ActiveAdsSnapshotAggregator.AggregateFromAccounts([acc1, acc2, acc3]);

        Assert.Equal(200, merged.Count);
        Assert.Equal(23, merged.Count(ad => ad.AccountId == id1));
        Assert.Equal(100, merged.Count(ad => ad.AccountId == id2));
        Assert.Equal(77, merged.Count(ad => ad.AccountId == id3));
    }

    [Fact]
    public void AggregateFromAccounts_UpdatingOneAccountJson_DoesNotShrinkOthers()
    {
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var acc1 = new AvitoAccount
        {
            Id = id1,
            ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
                Enumerable.Range(1, 25).Select(i => new AvitoAdStatus { Id = $"u{i}", Title = "p" }).ToList())
        };
        var acc2 = new AvitoAccount
        {
            Id = id2,
            ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(
                Enumerable.Range(1, 100).Select(i => new AvitoAdStatus { Id = $"v{i}", Title = "q" }).ToList())
        };

        var merged = ActiveAdsSnapshotAggregator.AggregateFromAccounts([acc1, acc2]);

        Assert.Equal(125, merged.Count);
    }
}
