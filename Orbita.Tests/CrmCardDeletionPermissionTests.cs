using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmWorkspaceServiceTests
{
    [Theory]
    [InlineData(PanelRoles.Manager, false, "own", true, false)]
    [InlineData(PanelRoles.SeniorManager, false, "other", true, false)]
    [InlineData(PanelRoles.OfficeLead, false, "queue", true, false)]
    [InlineData(PanelRoles.Manager, true, "own", true, true)]
    [InlineData(PanelRoles.Manager, true, "other", true, false)]
    [InlineData(PanelRoles.Manager, true, "queue", true, false)]
    [InlineData(PanelRoles.Manager, true, "own", false, false)]
    [InlineData(PanelRoles.SeniorManager, true, "other", true, true)]
    [InlineData(PanelRoles.OfficeLead, true, "queue", true, true)]
    [InlineData(PanelRoles.SeniorManager, true, "own", false, false)]
    [InlineData(PanelRoles.OfficeLead, true, "other", false, false)]
    [InlineData(PanelRoles.Admin, false, "other", false, true)]
    public async Task DeleteCard_ExplicitGrant_RespectsCurrentRoleAndOffice(
        string role, bool granted, string assignment, bool sameOffice, bool expected)
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var user = await harness.CreateDeskUserAsync("deletion-scope@test.local", 5, true, role,
            officeId: sameOffice ? OfficeId : Guid.NewGuid());
        if (granted)
            await harness.Users.AddClaimAsync(user, new Claim(
                CrmCardDeletionPermission.ClaimType, CrmCardDeletionPermission.GrantedValue));
        var response = await SeedResponseAsync(harness.Db, "deletion-scope");
        var card = NewCard(response.Id, assignment == "own" ? user.Id : assignment == "other" ? "other-user" : null);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error, _) = await harness.Sut.DeleteCardAsync(card.Id, user.Id);

        Assert.Equal(expected, ok);
        Assert.Equal(expected ? null : CrmCardDeletionPermission.DeniedMessage, error);
        Assert.Equal(!expected, await harness.Db.CrmCandidateCards.AnyAsync(x => x.Id == card.Id));
        Assert.True(await harness.Db.CandidateResponses.AnyAsync(x => x.Id == response.Id));
    }

    [Fact]
    public async Task DeleteCard_GrantRevokedAfterOpeningCard_IsImmediatelyRejected()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("revoked@test.local", 5, true);
        var response = await SeedResponseAsync(harness.Db, "revoked");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();
        var grant = new Claim(CrmCardDeletionPermission.ClaimType, CrmCardDeletionPermission.GrantedValue);
        await harness.Users.AddClaimAsync(manager, grant);

        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.True(detail!.CanDelete);
        await harness.Users.RemoveClaimAsync(manager, grant);

        var (ok, error, _) = await harness.Sut.DeleteCardAsync(card.Id, manager.Id);
        Assert.False(ok);
        Assert.Equal(CrmCardDeletionPermission.DeniedMessage, error);
        Assert.True(await harness.Db.CrmCandidateCards.AnyAsync(x => x.Id == card.Id));
        var refreshed = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.False(refreshed!.CanDelete);
    }

    [Fact]
    public async Task CardDeletion_RoleOrSectionClaim_DoesNotGrantPersonalPermission()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("inherited@test.local", 5, true);
        var role = await harness.Db.Roles.SingleAsync(x => x.Name == PanelRoles.Manager);
        harness.Db.RoleClaims.Add(new IdentityRoleClaim<string>
        {
            RoleId = role.Id,
            ClaimType = CrmCardDeletionPermission.ClaimType,
            ClaimValue = CrmCardDeletionPermission.GrantedValue
        });
        await harness.Db.SaveChangesAsync();
        await harness.Users.AddClaimAsync(manager,
            new Claim(PanelPermissions.ClaimType, CrmCardDeletionPermission.ClaimType));

        Assert.False(await CrmCardDeletionAccess.CanDeleteAsync(
            harness.Db, OfficeId, manager.Id, manager.Id, default));
    }

    [Theory]
    [InlineData(PanelRoles.Manager)]
    [InlineData(PanelRoles.SeniorManager)]
    [InlineData(PanelRoles.OfficeLead)]
    [InlineData(PanelRoles.Operator)]
    public async Task CardDeletionPermission_AdministrationSectionAlone_CannotGrantOrRevoke(string role)
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var actor = await harness.CreateDeskUserAsync("not-admin@test.local", 5, true, role);
        var target = await harness.CreateManagerAsync("grant-target@test.local", 5, true);
        await harness.Users.AddClaimAsync(actor, new Claim(PanelPermissions.ClaimType, PanelPermissions.Administration));
        var service = new PanelUserService(harness.Users, new PanelAuditService(harness.Db), harness.Db);
        var auditActor = new AuditActor(actor.Id, actor.Email, "127.0.0.1");

        var (user, error) = await service.SetCardDeletionPermissionAsync(target.Id, true, auditActor);
        Assert.Null(user);
        Assert.Contains("только администратор", error);
        Assert.False(await CrmCardDeletionAccess.HasExplicitGrantAsync(harness.Db, target.Id, default));

        await harness.Users.AddClaimAsync(target, new Claim(
            CrmCardDeletionPermission.ClaimType, CrmCardDeletionPermission.GrantedValue));
        var (_, revokeError) = await service.SetCardDeletionPermissionAsync(target.Id, false, auditActor);
        Assert.NotNull(revokeError);
        Assert.True(await CrmCardDeletionAccess.HasExplicitGrantAsync(harness.Db, target.Id, default));
        Assert.Empty(harness.Db.PanelAuditLogs);
    }

    [Fact]
    public async Task CardDeletionPermission_Administrator_CanGrantAndRevokeWithAudit()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var admin = await harness.CreateDeskUserAsync("grant-admin@test.local", 5, false, PanelRoles.Admin);
        var manager = await harness.CreateManagerAsync("granted@test.local", 5, true);
        var service = new PanelUserService(harness.Users, new PanelAuditService(harness.Db), harness.Db);
        var actor = new AuditActor(admin.Id, admin.Email, "127.0.0.1");

        var (granted, error) = await service.SetCardDeletionPermissionAsync(manager.Id, true, actor);
        Assert.Null(error);
        Assert.True(granted!.CanDeleteCrmCards);
        var audit = Assert.Single(harness.Db.PanelAuditLogs);
        Assert.Equal(PanelAuditActions.UserCardDeletionPermissionUpdated, audit.Action);
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Equal(manager.Id, audit.TargetId);
        Assert.Equal("enabled", audit.Details);

        await service.SetCardDeletionPermissionAsync(manager.Id, true, actor);
        Assert.Single(harness.Db.PanelAuditLogs);
        Assert.Single(await harness.Db.UserClaims.Where(c => c.UserId == manager.Id
            && c.ClaimType == CrmCardDeletionPermission.ClaimType).ToListAsync());

        // Switching normal section permissions back to the role must not clear the grant.
        var (_, permissionsError) = await service.SetPermissionOverrideAsync(manager.Id, true, [], actor);
        Assert.Null(permissionsError);
        Assert.True(await CrmCardDeletionAccess.HasExplicitGrantAsync(harness.Db, manager.Id, default));

        var (revoked, revokeError) = await service.SetCardDeletionPermissionAsync(manager.Id, false, actor);
        Assert.Null(revokeError);
        Assert.False(revoked!.CanDeleteCrmCards);
        Assert.False(await CrmCardDeletionAccess.HasExplicitGrantAsync(harness.Db, manager.Id, default));
        Assert.Equal(2, await harness.Db.PanelAuditLogs.CountAsync(x =>
            x.Action == PanelAuditActions.UserCardDeletionPermissionUpdated));
        Assert.Contains(harness.Db.PanelAuditLogs, x => x.Action == PanelAuditActions.UserCardDeletionPermissionUpdated
            && x.Details == "disabled");
    }

    [Fact]
    public async Task CardDeletionPermission_DemotedAdministrator_CannotGrantOrDelete()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var admin = await harness.CreateDeskUserAsync("demoted@test.local", 5, true, PanelRoles.Admin);
        var response = await SeedResponseAsync(harness.Db, "demoted");
        var card = NewCard(response.Id, admin.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();
        var service = new PanelUserService(harness.Users, new PanelAuditService(harness.Db), harness.Db);
        var actor = new AuditActor(admin.Id, admin.Email, "127.0.0.1");
        await harness.Users.RemoveFromRoleAsync(admin, PanelRoles.Admin);
        await harness.Users.AddToRoleAsync(admin, PanelRoles.Manager);

        var (user, error) = await service.SetCardDeletionPermissionAsync(admin.Id, true, actor);
        Assert.Null(user);
        Assert.NotNull(error);
        var (ok, deleteError, _) = await harness.Sut.DeleteCardAsync(card.Id, admin.Id);
        Assert.False(ok);
        Assert.Equal(CrmCardDeletionPermission.DeniedMessage, deleteError);
    }
}
