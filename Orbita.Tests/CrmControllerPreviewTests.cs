using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
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
        var board = Assert.IsType<CrmBoardDto>(view.Model);
        Assert.True(board.RequireStageComment);
        Assert.Equal(CrmBoardScopes.Team, board.Scope);
    }

    [Fact]
    public async Task Index_SeniorManagerDefaultsToOwnCards()
    {
        var principal = CreateOfficePrincipal(
            "preview-manager-elena",
            PanelRoles.SeniorManager,
            DesignPreviewData.PreviewOfficeId);
        var (controller, _) = CreateController(previewEnabled: true, principal);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: null,
            city: null,
            vacancy: null);

        var board = Assert.IsType<CrmBoardDto>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(CrmBoardScopes.Mine, board.Scope);
        Assert.All(
            board.Stages.SelectMany(x => x.Cards),
            card => Assert.Equal("preview-manager-elena", card.ManagerUserId));
    }

    [Fact]
    public async Task Index_ElevatedManagerFilterShowsSelectedManagersCards()
    {
        var (controller, _) = CreateController(previewEnabled: true);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: CrmBoardScopes.Team,
            city: null,
            vacancy: null,
            managerUserId: "preview-manager-igor");

        var board = Assert.IsType<CrmBoardDto>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(CrmBoardScopes.Team, board.Scope);
        Assert.Equal("preview-manager-igor", board.ManagerUserId);
        Assert.All(
            board.Stages.SelectMany(x => x.Cards),
            card => Assert.Equal("preview-manager-igor", card.ManagerUserId));
    }

    [Fact]
    public void DesignPreviewHistory_IsIsolatedPerCard()
    {
        var firstCardId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var secondCardId = Guid.Parse("90000000-0000-0000-0000-000000000002");
        var firstMarker = $"first-{Guid.NewGuid():N}";
        var secondMarker = $"second-{Guid.NewGuid():N}";

        Assert.True(DesignPreviewData.MoveCrmCard(firstCardId, CrmStages.Ndz73, firstMarker).Success);
        Assert.True(DesignPreviewData.MoveCrmCard(secondCardId, CrmStages.Ndz26, secondMarker).Success);

        var firstCard = DesignPreviewData.GetCrmCard(firstCardId);
        var secondCard = DesignPreviewData.GetCrmCard(secondCardId);
        Assert.NotNull(firstCard);
        Assert.NotNull(secondCard);
        Assert.Contains(firstCard.History, item => item.Details?.Contains(firstMarker, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(firstCard.History, item => item.Details?.Contains(secondMarker, StringComparison.Ordinal) == true);
        Assert.Contains(secondCard.History, item => item.Details?.Contains(secondMarker, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(secondCard.History, item => item.Details?.Contains(firstMarker, StringComparison.Ordinal) == true);
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
    public async Task Snapshot_OpenedAsDocument_RedirectsToFullCrmPageAndPreservesFilters()
    {
        var (controller, _) = CreateController(previewEnabled: true);

        var result = await controller.Snapshot(
            officeId: DesignPreviewData.PreviewOfficeId,
            search: "Николаев",
            scope: CrmBoardScopes.Team,
            city: "Пермь",
            vacancy: "Сварщик",
            managerUserId: "preview-manager-elena",
            overdueOnly: true,
            includeClosed: true);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CrmController.Index), redirect.ActionName);
        Assert.Equal("Николаев", redirect.RouteValues!["search"]);
        Assert.Equal(CrmBoardScopes.Team, redirect.RouteValues["scope"]);
        Assert.Equal("Пермь", redirect.RouteValues["city"]);
        Assert.Equal("Сварщик", redirect.RouteValues["vacancy"]);
        Assert.Equal("preview-manager-elena", redirect.RouteValues["managerUserId"]);
        Assert.Equal(true, redirect.RouteValues["overdueOnly"]);
        Assert.Equal(true, redirect.RouteValues["includeClosed"]);
    }

    [Fact]
    public async Task Snapshot_RequestedByLiveRefresh_ReturnsWorkspacePartial()
    {
        var (controller, _) = CreateController(previewEnabled: true);
        controller.Request.Headers["X-Orbita-Content-Only"] = "1";
        controller.Request.Headers["X-Orbita-Snapshot"] = "crm";

        var result = await controller.Snapshot(
            officeId: DesignPreviewData.PreviewOfficeId,
            search: null,
            scope: CrmBoardScopes.Team,
            city: null,
            vacancy: null);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_CrmWorkspace", partial.ViewName);
        Assert.IsType<CrmBoardDto>(partial.Model);
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
        Assert.True(model.CanManageStaff);
        Assert.NotNull(model.Staff);
        Assert.NotEmpty(model.Staff);
        Assert.All(model.Staff, x => Assert.True(OfficeStaffRules.IsAssignableRole(x.Role)));
    }

    [Fact]
    public async Task Tasks_SeniorManagerDefaultsToOwnTasksAndCanFilterOffice()
    {
        var principal = CreateOfficePrincipal(
            "preview-manager-elena",
            PanelRoles.SeniorManager,
            DesignPreviewData.PreviewOfficeId);
        var (controller, _) = CreateController(previewEnabled: true, principal);

        var ownResult = await controller.Tasks(scope: null);
        var ownModel = Assert.IsType<CrmTasksViewModel>(Assert.IsType<ViewResult>(ownResult).Model);
        Assert.True(ownModel.CanFilterResponsible);
        Assert.Equal("preview-manager-elena", ownModel.ManagerUserId);
        Assert.All(ownModel.Tasks, task => Assert.Equal("preview-manager-elena", task.AssigneeUserId));

        var allResult = await controller.Tasks(scope: null, managerUserId: "all");
        var allModel = Assert.IsType<CrmTasksViewModel>(Assert.IsType<ViewResult>(allResult).Model);
        Assert.Null(allModel.ManagerUserId);
        Assert.True(allModel.Tasks.Count >= ownModel.Tasks.Count);
    }

    [Fact]
    public async Task Tasks_ManagerSeesOnlyOwnTasksWithoutResponsibleFilter()
    {
        var principal = CreateOfficePrincipal(
            "preview-manager-elena",
            PanelRoles.Manager,
            DesignPreviewData.PreviewOfficeId);
        var (controller, _) = CreateController(previewEnabled: true, principal);

        var result = await controller.Tasks(scope: null, managerUserId: "all");
        var model = Assert.IsType<CrmTasksViewModel>(Assert.IsType<ViewResult>(result).Model);

        Assert.False(model.CanFilterResponsible);
        Assert.Equal("preview-manager-elena", model.ManagerUserId);
        Assert.All(model.Tasks, task => Assert.Equal("preview-manager-elena", task.AssigneeUserId));
    }

    [Fact]
    public async Task Tasks_OfficeLeadAndAdminDefaultToWholeOffice()
    {
        foreach (var role in new[] { PanelRoles.OfficeLead, PanelRoles.Admin })
        {
            var principal = CreateOfficePrincipal(
                $"preview-{role.ToLowerInvariant()}",
                role,
                DesignPreviewData.PreviewOfficeId);
            var (controller, _) = CreateController(previewEnabled: true, principal);

            var result = await controller.Tasks(scope: null);
            var model = Assert.IsType<CrmTasksViewModel>(Assert.IsType<ViewResult>(result).Model);

            Assert.True(model.CanFilterResponsible);
            Assert.Null(model.ManagerUserId);
        }
    }

    [Theory]
    [InlineData(CrmTaskTypes.Decision, "Что решил")]
    [InlineData(CrmTaskTypes.Documents, "Документы")]
    public async Task CreateTask_InDesignPreview_UsesTaskTypeAsTitle(string taskType, string expectedTitle)
    {
        var (controller, _) = CreateController(previewEnabled: true);
        var cardId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var card = DesignPreviewData.GetCrmCard(cardId);
        Assert.NotNull(card);
        var description = $"Решение кандидата {Guid.NewGuid():N}";

        await controller.CreateTask(
            cardId,
            description,
            card.Managers[0].UserId,
            DateTime.UtcNow.AddHours(1),
            importance: null,
            taskType: taskType,
            returnUrl: null,
            stage: null);

        var updatedCard = DesignPreviewData.GetCrmCard(cardId);
        Assert.NotNull(updatedCard);
        var task = Assert.Single(updatedCard.Tasks, item => item.Description == description);
        Assert.Equal(taskType, task.TaskType);
        Assert.Equal(expectedTitle, task.Title);
        Assert.Equal(CrmTaskImportances.Medium, task.Importance);
    }

    [Fact]
    public async Task CreateManual_ManagerIsAllowed()
    {
        var principal = CreateOfficePrincipal(
            "preview-manager-elena",
            PanelRoles.Manager,
            DesignPreviewData.PreviewOfficeId);
        var (controller, _) = CreateController(previewEnabled: true, principal);

        var result = await controller.CreateManual(
            fullName: "Новый Кандидат",
            phoneRaw: "+7 900 123-45-67",
            city: "Пермь",
            vacancy: "Сварщик",
            age: 35,
            citizenship: "Россия",
            source: "Ручной ввод",
            sourceResponseId: null,
            stage: CrmStages.Lead,
            assignToMe: false);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CrmController.Card), redirect.ActionName);
    }

    [Fact]
    public async Task BulkActions_ManagerIsForbidden()
    {
        var principal = CreateOfficePrincipal(
            "preview-manager-elena",
            PanelRoles.Manager,
            DesignPreviewData.PreviewOfficeId);
        var (controller, _) = CreateController(previewEnabled: true, principal);
        var cardIds = new[] { Guid.Parse("90000000-0000-0000-0000-000000000001") };

        var assignResult = await controller.BulkAssign(
            cardIds,
            managerUserId: "preview-manager-igor",
            returnUrl: null);
        var transitionResult = await controller.BulkTransition(
            cardIds,
            operation: CrmBulkTransitionOperations.Move,
            stage: CrmStages.Negotiations,
            closeReason: null,
            comment: "РџСЂРёС‡РёРЅР° СЃРјРµРЅС‹ СЌС‚Р°РїР°",
            returnUrl: null);

        Assert.IsType<ForbidResult>(assignResult);
        Assert.IsType<ForbidResult>(transitionResult);
    }

    private static (CrmController Controller, HttpClient Http) CreateController(
        bool previewEnabled,
        ClaimsPrincipal? principal = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = principal ?? TestPrincipalFactory.Admin("preview-admin", "Администратор")
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

    private static ClaimsPrincipal CreateOfficePrincipal(string userId, string role, Guid officeId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, role),
            new Claim(OfficeClaims.OfficeId, officeId.ToString("D"))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CRM controller test must not call the API.");
    }
}
