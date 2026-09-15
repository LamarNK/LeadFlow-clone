using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Auth;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class BalancesSearchTests
{
    [Theory]
    [InlineData("low", null)]
    [InlineData("queue", TopUpSessionStatuses.Requested)]
    [InlineData("working", TopUpSessionStatuses.PaymentClaimed)]
    [InlineData("awaiting", TopUpSessionStatuses.AwaitingBalance)]
    public async Task GetIndexAsync_SearchFiltersRowBasedTabs(string tab, string? status)
    {
        var alpha = CreateAccountWithSession("Alpha account", status);
        var beta = CreateAccountWithSession("Beta account", status);
        var accounts = new[] { alpha.Account, beta.Account };
        var sessions = new[] { alpha.Session, beta.Session }.OfType<TopUpSessionDto>().ToArray();
        using var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/balances/accounts" => JsonResponse<IReadOnlyList<OfficeBalanceListItem>>(accounts),
            "/api/v1/panel/top-up-sessions" => JsonResponse<IReadOnlyList<TopUpSessionDto>>(sessions),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            var model = await service.GetIndexAsync(tab, "Alpha");

            var row = Assert.Single(model.Rows);
            Assert.Equal(alpha.Account.AccountId, row.AccountId);
            Assert.Equal(1, model.Pagination.TotalItems);
        }
    }

    [Theory]
    [InlineData("requested", false)]
    [InlineData("history", true)]
    public async Task GetIndexAsync_SearchFiltersSessionBasedTabs(string tab, bool history)
    {
        var alpha = CreateSession("Alpha account", history ? TopUpSessionStatuses.Completed : TopUpSessionStatuses.Requested);
        var beta = CreateSession("Beta account", history ? TopUpSessionStatuses.Completed : TopUpSessionStatuses.Requested);
        using var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/balances/accounts" => JsonResponse<IReadOnlyList<OfficeBalanceListItem>>([]),
            "/api/v1/panel/top-up-sessions" => JsonResponse<IReadOnlyList<TopUpSessionDto>>([alpha, beta]),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            var model = await service.GetIndexAsync(tab, "Alpha", history);

            var session = Assert.Single(model.Sessions);
            Assert.Equal(alpha.Id, session.Id);
            Assert.Equal(1, model.Pagination.TotalItems);
        }
    }

    [Fact]
    public async Task GetIndexAsync_LowTabExcludesAccountsDisabledInPanel()
    {
        var enabled = CreateAccountWithSession("Enabled account", null);
        var disabled = CreateAccountWithSession("Disabled account", null, isEnabledInPanel: false);
        using var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/balances/accounts" => JsonResponse<IReadOnlyList<OfficeBalanceListItem>>([enabled.Account, disabled.Account]),
            "/api/v1/panel/top-up-sessions" => JsonResponse<IReadOnlyList<TopUpSessionDto>>([]),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            var model = await service.GetIndexAsync("low");

            var row = Assert.Single(model.Rows);
            Assert.Equal(enabled.Account.AccountId, row.AccountId);
            Assert.Equal(1, model.LowBalanceCount);
        }
    }

    private static TopUpSessionDto CreateSession(string accountName, string status)
    {
        var now = DateTime.UtcNow;
        return new TopUpSessionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker",
            Guid.NewGuid(),
            accountName,
            Guid.NewGuid(),
            "operator",
            "Operator",
            status,
            100m,
            500m,
            400m,
            0,
            now,
            now.AddMinutes(15),
            status == TopUpSessionStatuses.Completed ? now : null,
            null,
            null,
            null,
            null,
            null,
            null,
            "profile",
            accountName + " profile");
    }

    private static (OfficeBalanceListItem Account, TopUpSessionDto? Session) CreateAccountWithSession(
        string accountName,
        string? status,
        bool isEnabledInPanel = true)
    {
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        const string subProfileId = "profile";
        var account = new OfficeBalanceListItem(
            workerId,
            "Worker",
            "Office",
            true,
            accountId,
            accountName,
            "active",
            isEnabledInPanel,
            100m,
            DateTime.UtcNow,
            [new WorkerSubProfileDto(subProfileId, accountName + " profile", "", true, 100m, null, null, null)]);
        if (status is null)
        {
            return (account, null);
        }

        var template = CreateSession(accountName, status);
        return (account, template with
        {
            WorkerId = workerId,
            AccountId = accountId,
            SubProfileId = subProfileId
        });
    }

    private static (BalancesService Service, HttpClient Http) CreateService(HttpMessageHandler handler)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var session = new AuthSession(accessor) { Token = "header.payload.signature" };
        var officeContext = new OfficeContext();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://orbita.test/") };
        var api = new OrbitaApiClient(http, session, officeContext, Options.Create(new DesignPreviewOptions()));
        return (new BalancesService(api, officeContext), http);
    }

    private static HttpResponseMessage JsonResponse<T>(T payload) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
