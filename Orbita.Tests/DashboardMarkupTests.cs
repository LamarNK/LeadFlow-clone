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
        var initAll = js.IndexOf("function initDashboardAll()", StringComparison.Ordinal);
        var toolbarInit = js.IndexOf("initDashboardWorkerToolbar();", initAll, StringComparison.Ordinal);
        var chartEarlyReturn = js.IndexOf("if (typeof Chart ===", initAll, StringComparison.Ordinal);

        Assert.True(configureDefaults >= 0);
        Assert.True(chartGuard > configureDefaults);
        Assert.True(chartDefaults > chartGuard);
        Assert.Contains("initDashboardWorkerToolbar();", js);
        Assert.Contains("configureChartDefaults();", js);
        Assert.True(initAll >= 0);
        Assert.True(toolbarInit > initAll);
        Assert.True(chartEarlyReturn > toolbarInit);
    }

    [Fact]
    public void Dashboard_UsesSingleServerSortControl_WithoutClientDomSorting()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");
        var view = ReadRepoFile("Orbita.Web/Views/Dashboard/Index.cshtml");

        Assert.DoesNotContain("function sortWorkerRows", js);
        Assert.DoesNotContain("orbita-dashboard-worker-sort-key", js);
        Assert.DoesNotContain("orbita-dashboard-worker-sort-direction", js);
        Assert.Contains("initDashboardWorkerToolbar();", js);
        Assert.Contains("data-dashboard-worker-sort-form", js);

        Assert.DoesNotContain("_TableSortTh", view);
        Assert.DoesNotContain("data-table-sort", view);
        Assert.DoesNotContain("По откликам", view);
        Assert.DoesNotContain("По ошибкам", view);
        Assert.Contains("data-dashboard-worker-sort-form", view);
        Assert.Contains("data-dashboard-worker-sort-key", view);
        Assert.Contains("data-dashboard-worker-sort", view);
        Assert.Contains("name=\"sort\"", view);
        Assert.Contains("name=\"dir\"", view);
        Assert.Contains("По активности", view);
        Assert.Contains("По имени", view);
        Assert.Contains("По статусу", view);
        Assert.Contains("<th>Воркер</th>", view);
        Assert.Contains("<th>Статус</th>", view);
        Assert.Contains("<th title=\"Последняя активность\">Активность</th>", view);
        Assert.Equal(1, CountOccurrences(view, "data-dashboard-worker-sort-key"));
        Assert.Equal(1, CountOccurrences(view, "data-dashboard-worker-sort-form"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
