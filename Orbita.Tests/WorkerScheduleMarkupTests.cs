namespace Orbita.Tests;

public sealed class WorkerScheduleMarkupTests
{
    [Fact]
    public void SchedulePage_ContainsSevenDayNavigationAndManagementStates()
    {
        var markup = ReadRepoFile("Orbita.Web", "Views", "Schedule", "Index.cshtml");
        Assert.Contains("schedule-days", markup);
        Assert.Contains("data-schedule-add-open", markup);
        Assert.Contains("data-schedule-auto", markup);
        Assert.Contains("data-schedule-settings-open", markup);
        Assert.Contains("data-schedule-worker-shift", markup);
        Assert.Contains("schedule-empty--inline", markup);
        Assert.Contains("Недельный цикл", markup);
        Assert.Contains("data-schedule-loading", markup);
        Assert.Contains("Сегодня выходной", markup);
        Assert.Contains("IsMonitoringPaused", markup);
        Assert.Contains("is-paused", markup);
    }

    [Fact]
    public void Layout_ExposesPermissionGatedScheduleNavigation()
    {
        var markup = ReadRepoFile("Orbita.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.Contains("PanelPermissions.Schedule", markup);
        Assert.Contains("\"Schedule\"", markup);
        Assert.Contains("Расписание", markup);
    }

    [Fact]
    public void ScheduleStyles_AlignWorkerTableWithToolbarContent()
    {
        var styles = ReadRepoFile("Orbita.Web", "wwwroot", "css", "orbita", "schedule.css");
        Assert.Contains(".schedule-table thead th:first-child,.schedule-table tbody td:first-child { padding-left:18px; }", styles);
        Assert.Contains(".schedule-table thead th:last-child,.schedule-table tbody td:last-child { padding-right:18px; }", styles);
        Assert.Contains(".schedule-calendar { min-width:0; max-width:100%; overflow-x:auto;", styles);
        Assert.Contains(".schedule-calendar__grid { display:grid; grid-template-columns:repeat(7,minmax(62px,1fr)); gap:5px; min-width:464px; }", styles);
        Assert.Contains("@media (min-width:761px) and (max-width:1200px)", styles);
        Assert.Contains(".schedule-calendar__grid{grid-template-columns:repeat(7,minmax(0,1fr));gap:4px;min-width:0}", styles);
        Assert.Contains("grid-template-columns:minmax(0,.65fr) minmax(0,.65fr) minmax(0,1.7fr)", styles);
    }

    [Fact]
    public void ScheduleStyles_HideFilteredWorkersAndCandidates()
    {
        var styles = ReadRepoFile("Orbita.Web", "wwwroot", "css", "orbita", "schedule.css");
        Assert.Contains(".schedule-candidate[hidden],[data-schedule-worker-row][hidden] { display:none; }", styles);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LeadFlow.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
