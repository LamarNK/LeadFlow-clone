using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AppRepositoryUnpublishedSnapshotTests
{
    [Fact]
    public async Task SaveAndLoadAccount_PreservesUnpublishedSnapshotJson()
    {
        var db = new EfInMemoryDatabase();
        var repository = new AppRepository(db.Factory);
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Avito",
            IsEnabled = true,
            UnpublishedAdsSnapshotJson = AvitoAdSnapshots.Serialize(
            [
                new AvitoAdStatus
                {
                    Id = "100",
                    SourceTab = AvitoAdStatus.UnpublishedTab,
                    Status = "Ожидает публикации",
                    CanPublish = true
                }
            ])
        };

        await repository.SaveAccountAsync(account, CancellationToken.None);
        var loaded = Assert.Single(await repository.GetAccountsAsync(CancellationToken.None));

        Assert.Equal(account.UnpublishedAdsSnapshotJson, loaded.UnpublishedAdsSnapshotJson);
        var ad = Assert.Single(AvitoAdSnapshots.Deserialize(loaded.UnpublishedAdsSnapshotJson, loaded.Id));
        Assert.Equal("100", ad.Id);
        Assert.True(ad.CanPublish);
    }
}
