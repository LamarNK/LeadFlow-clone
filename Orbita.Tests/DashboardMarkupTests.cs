namespace Orbita.Tests;

public sealed class DashboardMarkupTests
{
    [Fact]
    public void DashboardJs_InitializesWorkerControlsWhenChartLibraryIsStillLoading()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");

        var configureDefaults = js.IndexOf("function configureChartDefaults()", StringComparison.Ordinal);
        var chartDefaults = js.IndexOf("Chart.defaults.font.family", StringComparison.Ordinal);
        var chartGuard = js.IndexOf("if (!hasChart()) return;", configureDefaults, StringComparison.Ordinal);

        Assert.True(configureDefaults >= 0);
        Assert.True(chartGuard > configureDefaults);
        Assert.True(chartDefaults > chartGuard);
        Assert.Contains("initDashboardWorkerToolbar();", js);
        Assert.Contains("configureChartDefaults();", js);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
