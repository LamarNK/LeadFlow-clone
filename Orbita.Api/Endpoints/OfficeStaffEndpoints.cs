using System.Security.Claims;
using Orbita.Api.Services;
using Orbita.Contracts;
using static Orbita.Api.Endpoints.EndpointRequestContext;

namespace Orbita.Api.Endpoints;

public static class OfficeStaffEndpoints
{
    public static void Map(WebApplication app)
    {
        var staff = app.MapGroup("/api/v1/office-staff").RequireAuthorization("OfficeStaff");

        staff.MapGet("/users", async (
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (users, forbidden, error) = await officeStaff.ListAsync(principal, officeId, ct);
            if (forbidden)
            {
                return Results.Forbid();
            }

            return error is null
                ? Results.Ok(users)
                : Results.BadRequest(new { error });
        });

        staff.MapGet("/telephony-users", async (
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (users, forbidden, error) = await officeStaff.ListTelephonyUsersAsync(principal, officeId, ct);
            if (forbidden)
            {
                return Results.Forbid();
            }

            return error is null
                ? Results.Ok(users)
                : Results.BadRequest(new { error });
        });

        staff.MapPost("/users", async (
            CreatePanelUserRequest request,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.CreateAsync(
                principal,
                request.OfficeId,
                request.Email,
                request.Password,
                request.Role,
                request.FullName,
                GetActor(principal, http),
                ct);
            if (forbidden)
            {
                return Results.Forbid();
            }

            if (error is not null)
            {
                return error.Contains("уже существует", StringComparison.OrdinalIgnoreCase)
                    ? Results.Conflict(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(user);
        });

        staff.MapPut("/users/{id}/full-name", async (
            string id,
            UpdatePanelUserFullNameRequest request,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.SetFullNameAsync(
                principal,
                officeId,
                id,
                request.FullName,
                GetActor(principal, http),
                ct);
            return ToUserResult(user, forbidden, error);
        });

        staff.MapPut("/users/{id}/email", async (
            string id,
            UpdatePanelUserEmailRequest request,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.SetEmailAsync(
                principal,
                officeId,
                id,
                request.Email,
                GetActor(principal, http),
                ct);
            return ToUserResult(user, forbidden, error);
        });

        staff.MapPut("/users/{id}/role", async (
            string id,
            UpdatePanelUserRoleRequest request,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.SetRoleAsync(
                principal,
                officeId,
                id,
                request.Role,
                GetActor(principal, http),
                ct);
            return ToUserResult(user, forbidden, error);
        });

        staff.MapPost("/users/{id}/password", async (
            string id,
            ResetPanelUserPasswordRequest request,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (success, forbidden, error) = await officeStaff.ResetPasswordAsync(
                principal,
                officeId,
                id,
                request.Password,
                GetActor(principal, http),
                ct);
            if (forbidden)
            {
                return Results.Forbid();
            }

            if (success)
            {
                return Results.NoContent();
            }

            return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        staff.MapPost("/users/{id}/lock", async (
            string id,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.LockAsync(
                principal,
                officeId,
                id,
                GetActor(principal, http),
                ct);
            return ToUserResult(user, forbidden, error);
        });

        staff.MapPost("/users/{id}/unlock", async (
            string id,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (user, forbidden, error) = await officeStaff.UnlockAsync(
                principal,
                officeId,
                id,
                GetActor(principal, http),
                ct);
            return ToUserResult(user, forbidden, error);
        });

        staff.MapDelete("/users/{id}", async (
            string id,
            OfficeStaffService officeStaff,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var (success, forbidden, error) = await officeStaff.DeleteAsync(
                principal,
                officeId,
                id,
                GetActor(principal, http),
                ct);
            if (forbidden)
            {
                return Results.Forbid();
            }

            if (success)
            {
                return Results.NoContent();
            }

            return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });
    }

    private static IResult ToUserResult(PanelUserDto? user, bool forbidden, string? error)
    {
        if (forbidden)
        {
            return Results.Forbid();
        }

        if (error is null)
        {
            return Results.Ok(user);
        }

        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : error.Contains("уже существует", StringComparison.OrdinalIgnoreCase)
                ? Results.Conflict(new { error })
                : Results.BadRequest(new { error });
    }
}
