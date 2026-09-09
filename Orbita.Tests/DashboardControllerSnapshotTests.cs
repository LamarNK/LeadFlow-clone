using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Orbita.Web.Controllers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class DashboardControllerSnapshotTests
{
    [Fact]
    public async Task Snapshot_SerializesMonitoringPausedWorkerUsingCamelCaseLiveField()
    {
        var workerId = Guid.NewGuid();
        var dashboard = new StubDashboardService(new DashboardViewModel
        {
            Workers =
            [
                new DashboardWorkerRowViewModel
                {
                    Id = workerId,
                    DisplayName = "Paused worker",
                    IsMonitoringPaused = true
                }
            ]
        });
        var httpContextAccessor = new HttpContextAccessor();
        var session = new AuthSession(httpContextAccessor);
        var controller = new DashboardController(
            dashboard,
            null!,
            session,
            new OrbitaAuthService(httpContextAccessor, session));
        using var services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .Services
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var actionResult = await controller.Snapshot(null, null);

        var jsonResult = Assert.IsType<JsonResult>(actionResult);
        var snapshot = Assert.IsType<DashboardLiveSnapshotViewModel>(jsonResult.Value);
        var worker = Assert.Single(snapshot.Workers);
        Assert.IsType<DashboardWorkerRowViewModel>(worker);
        Assert.Equal(workerId, worker.Id);

        await jsonResult.ExecuteResultAsync(controller.ControllerContext);
        httpContext.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var serializedWorker = Assert.Single(document.RootElement.GetProperty("workers").EnumerateArray());

        Assert.True(serializedWorker.GetProperty("isMonitoringPaused").GetBoolean());
    }

    private sealed class StubDashboardService(DashboardViewModel model) : IDashboardService
    {
        public Task<DashboardViewModel> GetDashboardAsync(
            DashboardPeriod period,
            int page = 1,
            int? pageSize = null,
            string? sort = null,
            string? sortDir = null,
            CancellationToken ct = default,
            string? workerFilter = null,
            string? workerSearch = null) => Task.FromResult(model);
    }
}
