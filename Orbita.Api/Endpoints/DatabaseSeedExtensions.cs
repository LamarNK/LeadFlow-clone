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

namespace Orbita.Api.Endpoints;

public static class DatabaseSeedExtensions
{
    public static async Task SeedAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
        await db.Database.MigrateAsync();

        var legacyMigration = scope.ServiceProvider.GetRequiredService<BitrixLegacyMigrationService>();
        await legacyMigration.MigrateAsync();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var offices = scope.ServiceProvider.GetRequiredService<OfficeAdminService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var email = config["Admin:Email"] ?? "admin@orbita.local";
        var password = config["Admin:Password"] ?? "OrbitaAdmin1!";
        var registrationSecret = config["RegistrationSecret"];

        foreach (var roleName in PanelRoles.All)
        {
            if (!await roles.RoleExistsAsync(roleName))
            {
                await roles.CreateAsync(new IdentityRole(roleName));
            }
        }

        var accessProfiles = scope.ServiceProvider.GetRequiredService<AccessProfileService>();
        await accessProfiles.EnsureDefaultsAsync();

        var defaultOffice = await offices.EnsureDefaultOfficeAsync(registrationSecret);

        var workersWithoutOffice = await db.Workers.Where(x => x.OfficeId == Guid.Empty).ToListAsync();
        foreach (var worker in workersWithoutOffice)
        {
            worker.OfficeId = defaultOffice.Id;
        }

        if (workersWithoutOffice.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        IdentityUser? adminUser = null;
        if (await users.FindByEmailAsync(email) is null)
        {
            adminUser = new IdentityUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };
            await users.CreateAsync(adminUser, password);
            await users.AddToRoleAsync(adminUser, PanelRoles.Admin);

            await GlobalLogger.Instance.LogAsync(
                $"Admin user seeded ({email}).",
                DeskLinkAuditLogLevel.Info);
        }
        else if (await users.FindByEmailAsync(email) is { } existingAdmin)
        {
            adminUser = existingAdmin;
            if (!await users.IsInRoleAsync(existingAdmin, PanelRoles.Admin))
            {
                await users.AddToRoleAsync(existingAdmin, PanelRoles.Admin);
            }
        }

        if (adminUser is not null
            && !await db.PanelUserProfiles.AnyAsync(x => x.UserId == adminUser.Id))
        {
            db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = adminUser.Id,
                OfficeId = null
            });
            await db.SaveChangesAsync();
        }

        foreach (var user in await users.Users.ToListAsync())
        {
            if (await db.PanelUserProfiles.AnyAsync(x => x.UserId == user.Id))
            {
                continue;
            }

            var userRoles = await users.GetRolesAsync(user);
            var isAdmin = userRoles.Contains(PanelRoles.Admin, StringComparer.OrdinalIgnoreCase);
            db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = user.Id,
                OfficeId = isAdmin ? null : defaultOffice.Id
            });
        }

        await db.SaveChangesAsync();

        // Operators must be office-bound: rebind any left without an office.
        var operatorUserIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in await users.Users.ToListAsync())
        {
            if (await users.IsInRoleAsync(user, PanelRoles.Operator))
            {
                operatorUserIds.Add(user.Id);
            }
        }

        if (operatorUserIds.Count > 0)
        {
            var unboundOperators = await db.PanelUserProfiles
                .Where(x => x.OfficeId == null && operatorUserIds.Contains(x.UserId))
                .ToListAsync();
            foreach (var profile in unboundOperators)
            {
                profile.OfficeId = defaultOffice.Id;
            }

            if (unboundOperators.Count > 0)
            {
                await db.SaveChangesAsync();
                await GlobalLogger.Instance.LogAsync(
                    $"Rebound {unboundOperators.Count} operator(s) without office to default office.",
                    DeskLinkAuditLogLevel.Info);
            }
        }
    }

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token, string Email);
}
