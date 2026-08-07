using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Controllers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class CrmControllerPreviewTests
{
    [Fact]
    public async Task Index_InDesignPreview_UsesDemoOfficeWhenAdminHasNoOfficeCookie()
    {
        var (controller, _) = CreateController(previewEnabled: true);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: null,
            city: null,
            vacancy: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName);
        Assert.IsType<CrmBoardDto>(view.Model);
    }

    [Fact]
    public async Task Index_OutsideDesignPreview_StillRequiresOfficeForAdmin()
    {
        var (controller, _) = CreateController(previewEnabled: false);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: null,
            city: null,
            vacancy: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Unavailable", view.ViewName);
    }

    [Fact]
    public async Task Analytics_InDesignPreview_UsesLocalCalendarHalfOpenUtcRange()
    {
        var (controller, _) = CreateController(previewEnabled: true);
        // Controller has no browser tz cookie → offset 0 (UTC calendar days).
        var today = Orbita.Api.Helpers.LocalCalendarDateRange.GetLocalCalendarDate(DateTime.UtcNow, 0);
        var from = today.AddDays(-2);
        var to = today.AddDays(-1);
        var expected = Orbita.Api.Helpers.LocalCalendarDateRange.Normalize(from, to, 0);

        var result = await controller.Analytics(
            from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"),
            managerUserId: null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CrmAnalyticsViewModel>(view.Model);
        Assert.NotNull(model.Analytics);
        Assert.Equal(expected.UtcStartInclusive, model.Analytics.FromUtc);
        Assert.Equal(expected.UtcEndExclusive, model.Analytics.ToUtc);
        Assert.Equal(DateTimeKind.Utc, model.Analytics.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, model.Analytics.ToUtc.Kind);
    }

    [Fact]
    public void LocalCalendarDateRange_WithUtcPlusFive_UsesPreviousUtcDateAndExclusiveEnd()
    {
        // UTC+5 → JS getTimezoneOffset = -300
        var period = new DashboardPeriod(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 8, 2),
            TimeZoneOffsetMinutes: -300);

        var range = Orbita.Web.Services.LocalCalendarDateRange.ToUtcRange(period);

        Assert.Equal(new DateTime(2026, 7, 31, 19, 0, 0, DateTimeKind.Utc), range.UtcStartInclusive);
        Assert.Equal(new DateTime(2026, 8, 2, 19, 0, 0, DateTimeKind.Utc), range.UtcEndExclusive);
        Assert.Equal(TimeSpan.FromDays(2), range.UtcEndExclusive - range.UtcStartInclusive);
    }

    [Fact]
    public async Task Team_InDesignPreview_ReturnsTeamTasksForSelectedOffice()
    {
        var (controller, _) = CreateController(previewEnabled: true);

        var result = await controller.Team(taskScope: "overdue");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CrmTeamViewModel>(view.Model);
        Assert.True(model.Board.IsAdmin);
        Assert.NotEmpty(model.Tasks);
        Assert.Equal("overdue", model.SelectedTaskScope);
    }

    private static (CrmController Controller, HttpClient Http) CreateController(bool previewEnabled)
    {
        var httpContext = new DefaultHttpContext
        {
            User = TestPrincipalFactory.Admin("preview-admin", "Администратор")
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var officeContext = new OfficeContext();
        officeContext.Bind(httpContext);
        var session = new AuthSession(accessor);
        var previewOptions = Options.Create(new DesignPreviewOptions { Enabled = previewEnabled });
        var http = new HttpClient(new ThrowingHttpMessageHandler())
        {
            BaseAddress = new Uri("https://orbita.test/")
        };
        var api = new OrbitaApiClient(http, session, officeContext, previewOptions);
        var auth = new OrbitaAuthService(accessor, session);
        var controller = new CrmController(api, officeContext, auth, previewOptions)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return (controller, http);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CRM controller test must not call the API.");
    }
}
