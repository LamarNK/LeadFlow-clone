using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using static Orbita.Api.Endpoints.EndpointRequestContext;

namespace Orbita.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void Map(WebApplication app)
    {
        var dashboard = app.MapGroup("/api/v1/dashboard").RequireAuthorization(PanelPermissions.Dashboard);
        dashboard.MapGet("/diagnostics/{id:guid}/image", async (
            Guid id,
            WorkerDiagnosticsService diagnostics,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (stream, contentType, error) = await diagnostics.OpenForPanelAsync(id, scope, ct);
            return stream is null
                ? Results.NotFound(new { error = error ?? "Вложение не найдено." })
                : Results.File(stream, contentType ?? "image/png");
        });

        dashboard.MapGet("/summary", async (
            Guid? officeId,
            int? tz,
            DateTime? from,
            DateTime? to,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetGlobalSummaryAsync(scope, officeId, tz, from, to, ct));
        });

        dashboard.MapGet("/workers", async (
            Guid? officeId,
            int? page,
            int? pageSize,
            string? sort,
            string? dir,
            string? workerFilter,
            string? workerSearch,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetWorkersPageAsync(
                scope,
                officeId,
                page ?? 1,
                pageSize,
                sort,
                dir,
                ct,
                workerFilter,
                workerSearch));
        });

        app.MapGet("/api/v1/nav/badges", async (
            Guid? officeId,
            int? tz,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Ok(new NavBadgesDto(0, 0, 0, DateTime.UtcNow));
            }

            return Results.Ok(await query.GetNavBadgesAsync(scope, officeId, tz, ct));
        }).RequireAuthorization();

        var workerRead = app.MapGroup("/api/v1").RequireAuthorization(PanelPermissions.Workers);
        workerRead.MapGet("/workers", async (
            Guid? officeId,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetWorkersAsync(scope, officeId, ct));
        });
        workerRead.MapGet("/workers/{id:guid}", async (
            Guid id,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var detail = await query.GetWorkerDetailAsync(id, scope, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
        var accountRead = app.MapGroup("/api/v1").RequireAuthorization(PanelPermissions.Accounts);
        accountRead.MapGet("/accounts", async (
            Guid? officeId,
            Guid? workerId,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetOfficeAccountsAsync(scope, officeId, workerId, ct));
        });
        var balanceRead = app.MapGroup("/api/v1").RequireAuthorization(PanelPermissions.Balances);
        balanceRead.MapGet("/balances/accounts", async (
            Guid? officeId,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetOfficeBalancesAsync(scope, officeId, ct));
        });
        var listingsRead = app.MapGroup("/api/v1").RequireAuthorization(PanelPermissions.Listings);
        listingsRead.MapGet("/listings", async (
            Guid? officeId,
            Guid? workerId,
            Guid[]? workerIds,
            Guid? accountId,
            Guid[]? accountIds,
            string? subProfileId,
            string[]? subProfileIds,
            string? state,
            bool? isActive,
            string? q,
            string? tab,
            int? page,
            int? pageSize,
            string? sort,
            string? dir,
            AvitoAdsQueryService listings,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await listings.GetListingsAsync(
                scope,
                workerId,
                accountId,
                subProfileId,
                state,
                isActive,
                q,
                ct,
                workerIds,
                accountIds,
                subProfileIds,
                tab,
                page ?? 1,
                pageSize ?? 100,
                sort,
                dir));
        });
        accountRead.MapGet("/workers/{id:guid}/accounts", async (
            Guid id,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetWorkerAccountsAsync(id, scope, ct));
        });
        var eventsRead = app.MapGroup("/api/v1").RequireAuthorization(PanelPermissions.Events);
        eventsRead.MapGet("/events", async (
            Guid? workerId,
            Guid? officeId,
            int? limit,
            DateTime? since,
            DashboardQueryService query,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await query.GetWorkerEventsAsync(
                scope,
                workerId,
                officeId,
                Math.Clamp(limit ?? 100, 1, 500),
                since,
                ct));
        });
        workerRead.MapGet("/worker-releases/latest", async (WorkerReleaseService releases, CancellationToken ct) =>
        {
            var latest = await releases.GetLatestAsync(ct);
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        });

        app.MapGet("/api/v1/public/worker-releases/latest/download", async (WorkerReleaseService releases, CancellationToken ct) =>
        {
            var (stream, fileName, error) = await releases.OpenPackageAsync(version: null, ct);
            if (stream is null || fileName is null)
            {
                return Results.NotFound(new { error = error ?? "Релиз не найден." });
            }

            return Results.File(stream, "application/octet-stream", fileName);
        });

    }
}
