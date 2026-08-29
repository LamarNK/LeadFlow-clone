using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerDetailsAccountsTests
{
    private static readonly Guid WorkerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AdsId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MlxId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LocalId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void MapAccount_Multilogin_SetsProviderAndCanRefresh()
    {
        var mapped = WorkerDetailsBuilder.MapAccount(
            new WorkerAccountDto(
                MlxId,
                "mlx-pro",
                "Active",
                true,
                1,
                0,
                0,
                null,
                null,
                true,
                AdsPowerProfileId: "",
                MultiloginProfileId: "mlx-profile",
                MultiloginFolderId: "folder-pro"),
            balance: null,
            WorkerId);

        Assert.True(mapped.IsMultilogin);
        Assert.Equal("Multilogin", mapped.ProfileProvider);
        Assert.Equal("mlx", mapped.ProfileProviderTone);
        Assert.Equal("folder-pro", mapped.LocationLabel);
        Assert.True(mapped.CanRefreshSubProfiles);
        Assert.Contains("mlx-profile", mapped.ProfileIdTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void MapAccount_AdsPower_KeepsLegacyProvider()
    {
        var mapped = WorkerDetailsBuilder.MapAccount(
            new WorkerAccountDto(
                AdsId,
                "ads-user",
                "Active",
                true,
                1,
                0,
                0,
                null,
                null,
                true,
                AdsPowerProfileId: "k19001",
                AdsPowerGroupId: "1001",
                AdsPowerGroupName: "Москва"),
            balance: null,
            WorkerId);

        Assert.False(mapped.IsMultilogin);
        Assert.Equal("AdsPower", mapped.ProfileProvider);
        Assert.Equal("Москва", mapped.LocationLabel);
        Assert.True(mapped.CanRefreshSubProfiles);
    }

    [Fact]
    public void MapAccount_Local_SetsProviderFolderAndCanRefresh()
    {
        var mapped = WorkerDetailsBuilder.MapAccount(
            new WorkerAccountDto(
                LocalId,
                "chrome-acc",
                "Active",
                true,
                1,
                0,
                0,
                null,
                null,
                true,
                AdsPowerProfileId: "",
                LocalUserDataDir: @"D:\Orbita\ChromeProfiles\acc-1"),
            balance: null,
            WorkerId);

        Assert.True(mapped.IsLocal);
        Assert.False(mapped.IsMultilogin);
        Assert.Equal("Local", mapped.ProfileProvider);
        Assert.Equal("Обычный браузер", mapped.ProfileProviderLabel);
        Assert.Equal("local", mapped.ProfileProviderTone);
        Assert.Contains("acc-1", mapped.LocationLabel, StringComparison.Ordinal);
        Assert.True(mapped.CanRefreshSubProfiles);
        Assert.Contains(@"D:\Orbita\ChromeProfiles\acc-1", mapped.ProfileIdTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsMultiloginAccounts_AndFiltersByProvider()
    {
        var worker = new WorkerDetail(
            WorkerId,
            "worker-1",
            "pc",
            "1.0",
            "Stopped",
            null,
            false,
            false,
            null,
            null,
            null,
            []);
        var accounts = new[]
        {
            new WorkerAccountRowViewModel
            {
                Id = AdsId,
                DisplayName = "ads-user",
                AdsPowerProfileId = "k1",
                AdsPowerGroupId = "1001",
                AdsPowerGroupName = "Москва"
            },
            new WorkerAccountRowViewModel
            {
                Id = MlxId,
                DisplayName = "mlx-user",
                MultiloginProfileId = "mlx-profile",
                MultiloginFolderId = "folder-pro"
            },
            new WorkerAccountRowViewModel
            {
                Id = LocalId,
                DisplayName = "chrome-user",
                LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1"
            }
        };

        var all = WorkerDetailsBuilder.Build(worker, accounts, []);
        Assert.Equal(3, all.Accounts.Count);
        Assert.Equal(3, all.HighlightAccounts.Count);
        Assert.Equal(1, all.AdsPowerAccountCount);
        Assert.Equal(1, all.MultiloginAccountCount);
        Assert.Equal(1, all.LocalAccountCount);

        var mlxOnly = WorkerDetailsBuilder.Build(worker, accounts, [], accountProvider: "multilogin");
        Assert.Single(mlxOnly.Accounts);
        Assert.Equal(MlxId, mlxOnly.Accounts[0].Id);
        Assert.Equal(3, mlxOnly.HighlightAccounts.Count);
        Assert.Equal("multilogin", mlxOnly.AccountProvider);

        var localOnly = WorkerDetailsBuilder.Build(worker, accounts, [], accountProvider: "local");
        Assert.Single(localOnly.Accounts);
        Assert.Equal(LocalId, localOnly.Accounts[0].Id);
        Assert.Equal("local", localOnly.AccountProvider);

        var adsOnly = WorkerDetailsBuilder.Build(worker, accounts, [], accountProvider: "adspower");
        Assert.Single(adsOnly.Accounts);
        Assert.Equal(AdsId, adsOnly.Accounts[0].Id);
        Assert.DoesNotContain(mlxOnly.ActiveAccountFilterChips, c => c.Label.StartsWith("Источник:", StringComparison.Ordinal));

        var folder = WorkerDetailsBuilder.Build(worker, accounts, [], accountGroupId: "mlx:folder-pro");
        Assert.Single(folder.Accounts);
        Assert.Equal(MlxId, folder.Accounts[0].Id);

        var adsGroup = WorkerDetailsBuilder.Build(worker, accounts, [], accountGroupId: "1001");
        Assert.Single(adsGroup.Accounts);
        Assert.Equal(AdsId, adsGroup.Accounts[0].Id);
    }

    [Fact]
    public void Build_Search_MatchesMultiloginFolder()
    {
        var worker = new WorkerDetail(
            WorkerId,
            "worker-1",
            "pc",
            "1.0",
            "Stopped",
            null,
            false,
            false,
            null,
            null,
            null,
            []);
        var accounts = new[]
        {
            new WorkerAccountRowViewModel
            {
                Id = AdsId,
                DisplayName = "ads-user",
                AdsPowerProfileId = "k1"
            },
            new WorkerAccountRowViewModel
            {
                Id = MlxId,
                DisplayName = "other",
                MultiloginProfileId = "mlx-profile",
                MultiloginFolderId = "folder-b2b"
            }
        };

        var model = WorkerDetailsBuilder.Build(worker, accounts, [], accountSearchQuery: "folder-b2b");
        Assert.Single(model.Accounts);
        Assert.Equal(MlxId, model.Accounts[0].Id);
    }

    [Fact]
    public void Build_Search_MatchesLocalUserDataDir()
    {
        var worker = new WorkerDetail(
            WorkerId,
            "worker-1",
            "pc",
            "1.0",
            "Stopped",
            null,
            false,
            false,
            null,
            null,
            null,
            []);
        var accounts = new[]
        {
            new WorkerAccountRowViewModel
            {
                Id = AdsId,
                DisplayName = "ads-user",
                AdsPowerProfileId = "k1"
            },
            new WorkerAccountRowViewModel
            {
                Id = LocalId,
                DisplayName = "other",
                LocalUserDataDir = @"D:\Orbita\ChromeProfiles\cabinet-7"
            }
        };

        var model = WorkerDetailsBuilder.Build(worker, accounts, [], accountSearchQuery: "cabinet-7");
        Assert.Single(model.Accounts);
        Assert.Equal(LocalId, model.Accounts[0].Id);
    }
}
