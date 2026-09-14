using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Read current database grants, never a stale browser/JWT permission.</summary>
public static class CrmCardDeletionAccess
{
    public static Task<bool> IsAdministratorAsync(OrbitaDbContext db, string userId, CancellationToken ct) =>
        (from membership in db.UserRoles.AsNoTracking()
         join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
         where membership.UserId == userId && role.Name == PanelRoles.Admin
         select membership).AnyAsync(ct);

    public static Task<bool> HasExplicitGrantAsync(OrbitaDbContext db, string userId, CancellationToken ct) =>
        db.UserClaims.AsNoTracking().AnyAsync(claim => claim.UserId == userId
            && claim.ClaimType == CrmCardDeletionPermission.ClaimType
            && claim.ClaimValue == CrmCardDeletionPermission.GrantedValue, ct);

    public static async Task<bool> CanDeleteAsync(
        OrbitaDbContext db, Guid officeId, string? managerUserId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (await IsAdministratorAsync(db, userId, ct)) return true;
        if (!await HasExplicitGrantAsync(db, userId, ct)) return false;

        // A grant never opens another office, even if an assignment became stale.
        if (!await db.PanelUserProfiles.AsNoTracking()
                .AnyAsync(profile => profile.UserId == userId && profile.OfficeId == officeId, ct))
            return false;

        if (string.Equals(managerUserId, userId, StringComparison.Ordinal)) return true;
        return await (from membership in db.UserRoles.AsNoTracking()
                      join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
                      where membership.UserId == userId
                          && (role.Name == PanelRoles.OfficeLead || role.Name == PanelRoles.SeniorManager)
                      select membership).AnyAsync(ct);
    }
}
