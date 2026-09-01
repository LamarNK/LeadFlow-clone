using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerAccountCatalogFilterTests
{
    [Fact]
    public void NormalizeProvider_AcceptsAliases()
    {
        Assert.Null(WorkerAccountCatalogFilter.NormalizeProvider(null));
        Assert.Null(WorkerAccountCatalogFilter.NormalizeProvider(" "));
        Assert.Equal(WorkerAccountCatalogFilter.AdsPowerProvider, WorkerAccountCatalogFilter.NormalizeProvider("ads"));
        Assert.Equal(WorkerAccountCatalogFilter.AdsPowerProvider, WorkerAccountCatalogFilter.NormalizeProvider("AdsPower"));
        Assert.Equal(WorkerAccountCatalogFilter.MultiloginProvider, WorkerAccountCatalogFilter.NormalizeProvider("mlx"));
        Assert.Equal(WorkerAccountCatalogFilter.MultiloginProvider, WorkerAccountCatalogFilter.NormalizeProvider("Multilogin"));
        Assert.Equal(WorkerAccountCatalogFilter.LocalProvider, WorkerAccountCatalogFilter.NormalizeProvider("local"));
        Assert.Equal(WorkerAccountCatalogFilter.LocalProvider, WorkerAccountCatalogFilter.NormalizeProvider("chrome"));
        Assert.Equal(WorkerAccountCatalogFilter.LocalProvider, WorkerAccountCatalogFilter.NormalizeProvider("Обычный браузер"));
    }

    [Fact]
    public void MatchesProvider_FiltersBySource()
    {
        Assert.True(WorkerAccountCatalogFilter.MatchesProvider(null, "mlx-1"));
        Assert.True(WorkerAccountCatalogFilter.MatchesProvider("multilogin", "mlx-1"));
        Assert.False(WorkerAccountCatalogFilter.MatchesProvider("multilogin", null));
        Assert.True(WorkerAccountCatalogFilter.MatchesProvider("adspower", null));
        Assert.False(WorkerAccountCatalogFilter.MatchesProvider("adspower", "mlx-1"));
        Assert.False(WorkerAccountCatalogFilter.MatchesProvider("adspower", null, @"D:\profiles\a"));
        Assert.True(WorkerAccountCatalogFilter.MatchesProvider("local", null, @"D:\profiles\a"));
        Assert.False(WorkerAccountCatalogFilter.MatchesProvider("local", "mlx-1", @"D:\profiles\a"));
        Assert.False(WorkerAccountCatalogFilter.MatchesProvider("multilogin", null, @"D:\profiles\a"));
        Assert.Equal("Обычный браузер", WorkerAccountCatalogFilter.ProviderLabel("local"));
    }

    [Fact]
    public void MatchesLocation_KeepsAdsPowerGroupsAndMultiloginFolders()
    {
        Assert.True(WorkerAccountCatalogFilter.MatchesLocation(null, "1001", "folder-a"));
        Assert.True(WorkerAccountCatalogFilter.MatchesLocation("1001", "1001", null));
        Assert.False(WorkerAccountCatalogFilter.MatchesLocation("1001", null, "folder-a"));
        Assert.True(WorkerAccountCatalogFilter.MatchesLocation("mlx:folder-a", null, "folder-a"));
        Assert.False(WorkerAccountCatalogFilter.MatchesLocation("mlx:folder-a", "1001", "folder-b"));
        Assert.True(WorkerAccountCatalogFilter.MatchesLocation(WorkerAccountCatalogFilter.UngroupedValue, null, null));
        Assert.False(WorkerAccountCatalogFilter.MatchesLocation(WorkerAccountCatalogFilter.UngroupedValue, "1001", null));
        Assert.False(WorkerAccountCatalogFilter.MatchesLocation(WorkerAccountCatalogFilter.UngroupedValue, null, "folder-a"));
    }

    [Fact]
    public void BuildLocationOptions_IncludesBothCatalogs()
    {
        var options = WorkerAccountCatalogFilter.BuildLocationOptions(
            [
                new WorkerAccountRowViewModel
                {
                    DisplayName = "ads",
                    AdsPowerProfileId = "k1",
                    AdsPowerGroupId = "1001",
                    AdsPowerGroupName = "Москва"
                },
                new WorkerAccountRowViewModel
                {
                    DisplayName = "mlx",
                    MultiloginProfileId = "profile-1",
                    MultiloginFolderId = "folder-pro"
                },
                new WorkerAccountRowViewModel { DisplayName = "plain" }
            ],
            [new AdsPowerGroupDto("1002", "Смена")]);

        Assert.Equal("", options[0].Value);
        Assert.Equal("Все группы и папки", options[0].Label);
        Assert.Contains(options, o => o.Value == WorkerAccountCatalogFilter.UngroupedValue);
        Assert.Contains(options, o => o.Value == "1001" && o.Label == "AdsPower · Москва");
        Assert.Contains(options, o => o.Value == "1002" && o.Label == "AdsPower · Смена");
        Assert.Contains(options, o => o.Value == "mlx:folder-pro" && o.Label == "Multilogin · folder-pro");
    }
}
