using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Tests;

public sealed class OfficeStaffServiceTests
{
    [Fact]
    public async Task OfficeLead_CanCreateManagerInOwnOffice()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        var (user, forbidden, error) = await h.Sut.CreateAsync(
            actor,
            h.OfficeA,
            "mgr@test.local",
            "Password1!",
            PanelRoles.Manager,
            "Менеджер Тест",
            Actor(lead),
            CancellationToken.None);

        Assert.False(forbidden);
        Assert.Null(error);
        Assert.NotNull(user);
        Assert.Equal(PanelRoles.Manager, user.Role);
        Assert.Equal(h.OfficeA, user.OfficeId);
    }

    [Fact]
    public async Task OfficeLead_CannotCreateAdmin()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        var (user, forbidden, error) = await h.Sut.CreateAsync(
            actor,
            h.OfficeA,
            "admin2@test.local",
            "Password1!",
            PanelRoles.Admin,
            "Админ",
            Actor(lead),
            CancellationToken.None);

        Assert.False(forbidden);
        Assert.NotNull(error);
        Assert.Null(user);
    }

    [Fact]
    public async Task OfficeLead_CannotManageOtherOffice()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var foreign = await h.CreateUserAsync("mgr-b@test.local", PanelRoles.Manager, h.OfficeB);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        var (user, forbidden, error) = await h.Sut.SetFullNameAsync(
            actor,
            h.OfficeA,
            foreign.Id,
            "Хаки",
            Actor(lead),
            CancellationToken.None);

        Assert.True(forbidden);
        Assert.Null(error);
        Assert.Null(user);
    }

    [Fact]
    public async Task SeniorManager_IsForbidden()
    {
        await using var h = await Harness.CreateAsync();
        var senior = await h.CreateUserAsync("senior@test.local", PanelRoles.SeniorManager, h.OfficeA);
        var actor = h.Principal(senior.Id, PanelRoles.SeniorManager, h.OfficeA);

        var (users, forbidden, error) = await h.Sut.ListAsync(actor, h.OfficeA, CancellationToken.None);

        Assert.True(forbidden);
        Assert.Null(users);
        Assert.Null(error);
    }

    [Fact]
    public async Task OfficeLead_CanPromoteManagerToSeniorAndListStaff()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var mgr = await h.CreateUserAsync("mgr@test.local", PanelRoles.Manager, h.OfficeA);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        var (updated, forbidden, error) = await h.Sut.SetRoleAsync(
            actor,
            h.OfficeA,
            mgr.Id,
            PanelRoles.SeniorManager,
            Actor(lead),
            CancellationToken.None);

        Assert.False(forbidden);
        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal(PanelRoles.SeniorManager, updated.Role);

        var (list, listForbidden, listError) = await h.Sut.ListAsync(actor, h.OfficeA, CancellationToken.None);
        Assert.False(listForbidden);
        Assert.Null(listError);
        Assert.NotNull(list);
        Assert.Contains(list, x => x.Id == mgr.Id && x.Role == PanelRoles.SeniorManager);
        Assert.DoesNotContain(list, x => x.Id == lead.Id);
    }

    [Fact]
    public async Task OfficeLead_TelephonyRosterIncludesLeadManagersAndSeniorManagersOnly()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var manager = await h.CreateUserAsync("mgr@test.local", PanelRoles.Manager, h.OfficeA);
        var senior = await h.CreateUserAsync("senior@test.local", PanelRoles.SeniorManager, h.OfficeA);
        var officeOperator = await h.CreateUserAsync("operator@test.local", PanelRoles.Operator, h.OfficeA);
        var foreignLead = await h.CreateUserAsync("foreign-lead@test.local", PanelRoles.OfficeLead, h.OfficeB);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        var (list, forbidden, error) = await h.Sut.ListTelephonyUsersAsync(
            actor,
            h.OfficeA,
            CancellationToken.None);

        Assert.False(forbidden);
        Assert.Null(error);
        Assert.NotNull(list);
        Assert.Equal([lead.Id, senior.Id, manager.Id], list.Select(x => x.Id));
        Assert.DoesNotContain(list, x => x.Id == officeOperator.Id);
        Assert.DoesNotContain(list, x => x.Id == foreignLead.Id);
    }

    [Fact]
    public async Task OfficeLead_CannotDeleteSelf()
    {
        await using var h = await Harness.CreateAsync();
        var lead = await h.CreateUserAsync("lead@test.local", PanelRoles.OfficeLead, h.OfficeA);
        var actor = h.Principal(lead.Id, PanelRoles.OfficeLead, h.OfficeA);

        // Self is not an assignable staff target (OfficeLead role) → forbidden.
        var (success, forbidden, error) = await h.Sut.DeleteAsync(
            actor,
            h.OfficeA,
            lead.Id,
            Actor(lead),
            CancellationToken.None);

        Assert.False(success);
        Assert.True(forbidden);
        Assert.Null(error);
    }

    private static AuditActor Actor(IdentityUser user) =>
        new(user.Id, user.Email, "127.0.0.1");

    private sealed class Harness : IAsyncDisposable
    {
        public Guid OfficeA { get; } = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public Guid OfficeB { get; } = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public required OrbitaDbContext Db { get; init; }
        public required UserManager<IdentityUser> Users { get; init; }
        public required OfficeStaffService Sut { get; init; }
        private ServiceProvider? _services;

        public static async Task<Harness> CreateAsync()
        {
            var dbName = Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddDbContext<OrbitaDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddIdentityCore<IdentityUser>(o =>
                {
                    o.Password.RequireDigit = false;
                    o.Password.RequireLowercase = false;
                    o.Password.RequireUppercase = false;
                    o.Password.RequireNonAlphanumeric = false;
                    o.Password.RequiredLength = 6;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<OrbitaDbContext>();
            services.AddScoped<PanelAuditService>();
            services.AddScoped<PanelUserService>();
            services.AddScoped<OfficeScopeService>();
            services.AddScoped<OfficeStaffService>();

            var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<OrbitaDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();

            foreach (var role in PanelRoles.All)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            var harness = new Harness
            {
                Db = db,
                Users = users,
                Sut = sp.GetRequiredService<OfficeStaffService>(),
                _services = sp
            };

            db.Offices.AddRange(
                new OfficeEntity
                {
                    Id = harness.OfficeA,
                    Name = "Офис A",
                    IsEnabled = true,
                    RegistrationSecretHash = "hash-a",
                    CreatedAtUtc = DateTime.UtcNow
                },
                new OfficeEntity
                {
                    Id = harness.OfficeB,
                    Name = "Офис B",
                    IsEnabled = true,
                    RegistrationSecretHash = "hash-b",
                    CreatedAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
            return harness;
        }

        public ClaimsPrincipal Principal(string userId, string role, Guid officeId)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(ClaimTypes.Email, $"{userId}@test.local"),
                new(ClaimTypes.Role, role),
                new(OfficeClaims.OfficeId, officeId.ToString("D"))
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public async Task<IdentityUser> CreateUserAsync(string email, string role, Guid officeId)
        {
            var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await Users.CreateAsync(user, "Password1!");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
            await Users.AddToRoleAsync(user, role);
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = user.Id,
                FullName = email,
                OfficeId = officeId
            });
            await Db.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
        }
    }
}
