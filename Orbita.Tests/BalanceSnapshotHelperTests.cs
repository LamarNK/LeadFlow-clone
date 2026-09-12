using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BalanceSnapshotHelperTests
{
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void CountLowBalanceSubProfiles_IgnoresAccountTotalWithoutSubprofileAdvance()
    {
        var balance = new WorkerBalanceDto(AccountId, "acc-1", 20m, []);

        Assert.Equal(0, BalanceSnapshotHelper.CountLowBalanceSubProfiles(balance));
    }

    [Fact]
    public void CountLowBalanceSubProfiles_SkipsExcludedSubProfiles()
    {
        var balance = new WorkerBalanceDto(
            AccountId,
            "acc-1",
            100m,
            [
                new SubProfileBalanceDto("Первый", 40m, SubProfileId: "one"),
                new SubProfileBalanceDto("Второй", 50m, SubProfileId: "two")
            ]);

        Assert.Equal(1, BalanceSnapshotHelper.CountLowBalanceSubProfiles(
            balance,
            subProfile => subProfile.SubProfileId == "one"));
    }

    [Fact]
    public void HasMeaningfulBalanceData_ReturnsFalse_ForPlaceholderBalance()
    {
        var balance = new WorkerBalanceDto(
            AccountId,
            "acc-1",
            0m,
            [new SubProfileBalanceDto("—", null)]);

        Assert.False(BalanceSnapshotHelper.HasMeaningfulBalanceData(balance));
    }

    [Fact]
    public void MergeWithPersisted_SkipsPlaceholderBalance_WhenNoPersistedDataExists()
    {
        var incoming = new[]
        {
            new WorkerBalanceDto(AccountId, "acc-1", 0m, [new SubProfileBalanceDto("—", null)])
        };

        var merged = BalanceSnapshotHelper.MergeWithPersisted(
            incoming,
            [],
            new Dictionary<Guid, WorkerAccountEntity>());

        Assert.Empty(merged);
    }

    [Fact]
    public void MergeWithPersisted_UsesPersistedAccount_WhenIncomingBalanceIsEmpty()
    {
        var incoming = new[]
        {
            new WorkerBalanceDto(AccountId, "acc-1", 0m, [new SubProfileBalanceDto("Alpha", null)])
        };
        var existingAccounts = new Dictionary<Guid, WorkerAccountEntity>
        {
            [AccountId] = new()
            {
                AccountId = AccountId,
                DisplayName = "acc-1",
                TotalBalance = 4200m,
                SubProfilesJson = """[{"Id":"sp-1","Name":"Alpha","Balance":4200,"WalletBalance":100}]"""
            }
        };

        var merged = BalanceSnapshotHelper.MergeWithPersisted(incoming, [], existingAccounts);

        Assert.Single(merged);
        Assert.Equal(4200m, merged[0].TotalBalance);
        Assert.Equal(100m, merged[0].TotalWalletBalance);
    }
}