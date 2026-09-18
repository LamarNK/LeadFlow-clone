using System.Security.Claims;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

/// <summary>
/// Office-scoped staff CRUD for OfficeLead (and Admin). Wraps <see cref="PanelUserService"/>
/// with role/office guards so leads cannot touch Admin/OfficeLead/Operator or other offices.
/// </summary>
public sealed class OfficeStaffService(
    PanelUserService panelUsers,
    OfficeScopeService officeScope)
{
    public async Task<(IReadOnlyList<PanelUserDto>? Users, bool Forbidden, string? Error)> ListAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        CancellationToken ct = default)
    {
        var (officeId, forbidden, error) = await ResolveManagedOfficeAsync(actor, requestedOfficeId, ct);
        if (forbidden || error is not null || officeId is not Guid oid)
        {
            return (null, forbidden, error);
        }

        return (await panelUsers.ListStaffByOfficeAsync(oid, ct), false, null);
    }

    public async Task<(IReadOnlyList<PanelUserDto>? Users, bool Forbidden, string? Error)> ListTelephonyUsersAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        CancellationToken ct = default)
    {
        var (officeId, forbidden, error) = await ResolveManagedOfficeAsync(actor, requestedOfficeId, ct);
        if (forbidden || error is not null || officeId is not Guid oid)
        {
            return (null, forbidden, error);
        }

        return (await panelUsers.ListTelephonyUsersByOfficeAsync(oid, ct), false, null);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> CreateAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string email,
        string password,
        string? role,
        string? fullName,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var (officeId, forbidden, error) = await ResolveManagedOfficeAsync(actor, requestedOfficeId, ct);
        if (forbidden || error is not null || officeId is not Guid oid)
        {
            return (null, forbidden, error);
        }

        var roleError = OfficeStaffRules.ValidateAssignableRole(role);
        if (roleError is not null)
        {
            return (null, false, roleError);
        }

        var (user, createError) = await panelUsers.CreateAsync(
            email,
            password,
            PanelRoles.Normalize(role),
            oid,
            fullName,
            auditActor,
            ct);
        return (user, false, createError);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> SetFullNameAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        string? fullName,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (null, gate.Forbidden, gate.Error);
        }

        var (user, error) = await panelUsers.SetFullNameAsync(targetUserId, fullName, auditActor, ct);
        return (user, false, error);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> SetEmailAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        string? email,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (null, gate.Forbidden, gate.Error);
        }

        var (user, error) = await panelUsers.SetEmailAsync(targetUserId, email, auditActor, ct);
        return (user, false, error);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> SetRoleAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        string? role,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var roleError = OfficeStaffRules.ValidateAssignableRole(role);
        if (roleError is not null)
        {
            return (null, false, roleError);
        }

        var desired = PanelRoles.Normalize(role);
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desired, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (null, gate.Forbidden, gate.Error);
        }

        var actorUserId = actor.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? actor.FindFirstValue("sub");
        var (user, error) = await panelUsers.SetRoleAsync(targetUserId, desired, actorUserId, auditActor, ct);
        if (error is not null)
        {
            return (null, false, error);
        }

        // SetRoleAsync may auto-assign a default office when demoting admin; re-assert office for staff roles.
        if (user is not null && user.OfficeId != gate.OfficeId)
        {
            var (updated, officeError) = await panelUsers.SetOfficeAsync(targetUserId, gate.OfficeId, auditActor, ct);
            return (updated, false, officeError);
        }

        return (user, false, null);
    }

    public async Task<(bool Success, bool Forbidden, string? Error)> ResetPasswordAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        string password,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (false, gate.Forbidden, gate.Error);
        }

        var error = await panelUsers.ResetPasswordAsync(targetUserId, password, auditActor, ct);
        return (error is null, false, error);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> LockAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (null, gate.Forbidden, gate.Error);
        }

        var actorUserId = actor.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? actor.FindFirstValue("sub");
        var (user, error) = await panelUsers.LockAsync(targetUserId, actorUserId, auditActor, ct);
        return (user, false, error);
    }

    public async Task<(PanelUserDto? User, bool Forbidden, string? Error)> UnlockAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (null, gate.Forbidden, gate.Error);
        }

        var (user, error) = await panelUsers.UnlockAsync(targetUserId, auditActor, ct);
        return (user, false, error);
    }

    public async Task<(bool Success, bool Forbidden, string? Error)> DeleteAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        AuditActor auditActor,
        CancellationToken ct = default)
    {
        var gate = await GateTargetAsync(actor, requestedOfficeId, targetUserId, desiredRole: null, ct);
        if (gate.Forbidden || gate.Error is not null)
        {
            return (false, gate.Forbidden, gate.Error);
        }

        var actorUserId = actor.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? actor.FindFirstValue("sub");
        var error = await panelUsers.DeleteAsync(targetUserId, actorUserId, auditActor, ct);
        return (error is null, false, error);
    }

    private async Task<(Guid? OfficeId, bool Forbidden, string? Error)> ResolveManagedOfficeAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        CancellationToken ct)
    {
        if (!OfficeStaffRules.CanManageStaff(actor))
        {
            return (null, true, null);
        }

        var scope = await officeScope.ResolveAsync(actor, ct);
        if (!scope.HasAccess)
        {
            return (null, true, null);
        }

        var officeId = scope.ResolveFilter(requestedOfficeId);
        if (officeId is not Guid oid)
        {
            return (null, false, PanelRoles.IsGlobalAdmin(actor)
                ? "Выберите офис, чтобы управлять сотрудниками."
                : "Не назначен офис. Обратитесь к администратору.");
        }

        if (!scope.CanAccessOffice(oid))
        {
            return (null, true, null);
        }

        return (oid, false, null);
    }

    private async Task<(Guid OfficeId, bool Forbidden, string? Error)> GateTargetAsync(
        ClaimsPrincipal actor,
        Guid? requestedOfficeId,
        string targetUserId,
        string? desiredRole,
        CancellationToken ct)
    {
        var (officeId, forbidden, error) = await ResolveManagedOfficeAsync(actor, requestedOfficeId, ct);
        if (forbidden || error is not null || officeId is not Guid oid)
        {
            return (default, forbidden, error);
        }

        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return (oid, false, "Пользователь не найден.");
        }

        var target = await panelUsers.GetByIdAsync(targetUserId, ct);
        if (target is null)
        {
            return (oid, false, "Пользователь не найден.");
        }

        var actorUserId = actor.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? actor.FindFirstValue("sub");
        var targetError = OfficeStaffRules.ValidateManagedTarget(
            actorUserId,
            target.Id,
            target.Role,
            target.OfficeId,
            oid);
        if (targetError is not null)
        {
            // Cross-office or elevated target: treat as forbidden so we don't leak existence across offices.
            if (target.OfficeId != oid || !OfficeStaffRules.IsAssignableRole(target.Role))
            {
                return (oid, true, null);
            }

            return (oid, false, targetError);
        }

        if (desiredRole is not null)
        {
            var roleError = OfficeStaffRules.ValidateAssignableRole(desiredRole);
            if (roleError is not null)
            {
                return (oid, false, roleError);
            }
        }

        return (oid, false, null);
    }
}
