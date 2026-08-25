using System.Reflection;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerMultiloginSettingsTests
{
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void WorkerDetailsViewModel_DoesNotExposeAutomationToken()
    {
        var names = typeof(WorkerDetailsViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
        Assert.Contains(nameof(WorkerDetailsViewModel.HasMultiloginAutomationToken), names);
        Assert.Contains(nameof(WorkerDetailsViewModel.MultiloginLauncherUrl), names);
        Assert.Contains(nameof(WorkerDetailsViewModel.MultiloginCloudApiUrl), names);
    }

    [Fact]
    public void WorkerDetailsBuilder_CopiesLauncherWithoutTokenValue()
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
            [],
            MultiloginLauncherUrl: "https://launcher.mlx.yt:45001",
            MultiloginCloudApiUrl: "https://api.multilogin.com",
            HasMultiloginAutomationToken: true);

        var model = WorkerDetailsBuilder.Build(worker, [], []);

        Assert.Equal("https://launcher.mlx.yt:45001", model.MultiloginLauncherUrl);
        Assert.Equal("https://api.multilogin.com", model.MultiloginCloudApiUrl);
        Assert.True(model.HasMultiloginAutomationToken);
        Assert.Equal(WorkerDetailsViewModel.DefaultMultiloginLauncherUrl, model.EffectiveMultiloginLauncherUrl);
        Assert.Null(model.GetType().GetProperty("MultiloginAutomationToken"));
    }

    [Fact]
    public void WorkerDetailsLiveSnapshot_DoesNotExposeAutomationToken()
    {
        var names = typeof(WorkerDetailsLiveSnapshotViewModel).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
    }
}
