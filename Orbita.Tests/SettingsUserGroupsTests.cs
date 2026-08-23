using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class SettingsUserGroupsTests
{
    [Fact]
    public void BuildUsersTab_PreservesUserPresenceFromApi()
    {
        var lastSeenAtUtc = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var model = SettingsIndexBuilder.BuildUsersTab(
            [new PanelUserDto(
                "operator",
                "operator@orbita.local",
                true,
                PanelRoles.Operator,
                false,
                FullName: "Оператор",
                LastSeenAtUtc: lastSeenAtUtc,
                IsOnline: true)],
            [],
            "operator");

        var user = Assert.Single(model.Users);
        Assert.True(user.IsOnline);
        Assert.Equal(lastSeenAtUtc, user.LastSeenAtUtc);
        Assert.NotNull(model.PresenceStats);
        Assert.Equal(1, model.PresenceStats.Online);
        Assert.Equal(1, model.PresenceStats.Total);
    }

    [Fact]
    public void BuildPresenceStats_ExposesPeakHourAndOnlineCounts()
    {
        var users = new List<PanelUserRowViewModel>
        {
            new()
            {
                Id = "online",
                Email = "online@orbita.local",
                Role = PanelRoles.Operator,
                RoleLabel = "Оператор",
                ProfileId = "operator",
                IsOnline = true,
                LastSeenAtUtc = DateTime.UtcNow
            },
            new()
            {
                Id = "offline",
                Email = "offline@orbita.local",
                Role = PanelRoles.Manager,
                RoleLabel = "Менеджер",
                ProfileId = "manager",
                LastSeenAtUtc = DateTime.UtcNow.AddHours(-2)
            },
            new()
            {
                Id = "never",
                Email = "never@orbita.local",
                Role = PanelRoles.Operator,
                RoleLabel = "Оператор",
                ProfileId = "operator"
            }
        };
        var typical = new int[24];
        typical[11] = 6;
        typical[12] = 4;
        var today = new int[24];
        today[11] = 3;
        var stats = SettingsIndexBuilder.BuildPresenceStats(
            users,
            new PanelUserPresenceHourSeriesDto(
                typical,
                today,
                11,
                6,
                11,
                3,
                14,
                11,
                DateTime.UtcNow));

        Assert.Equal(1, stats.Online);
        Assert.Equal(1, stats.Offline);
        Assert.Equal(1, stats.NeverSeen);
        Assert.Equal(3, stats.Total);
        Assert.Equal("11:00–12:00", stats.TypicalPeakLabel);
        Assert.True(stats.HasHourlyData);
        Assert.True(stats.Hours[11].IsTypicalPeak);
        Assert.True(stats.Hours[11].IsCurrentHour);
    }

    [Fact]
    public void BuildUserGroups_SplitsAdminsOfficesAndUnassigned()
    {
        var officeOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var officeTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var users = new List<PanelUserRowViewModel>
        {
            new()
            {
                Id = "admin",
                Email = "admin@orbita.local",
                Role = PanelRoles.Admin,
                RoleLabel = "Администратор",
                ProfileId = "admin",
                FullName = "Админ"
            },
            new()
            {
                Id = "mgr",
                Email = "mgr@orbita.local",
                Role = PanelRoles.Manager,
                RoleLabel = "Менеджер",
                ProfileId = "manager",
                OfficeId = officeOne,
                OfficeName = "Основной",
                FullName = "Менеджер"
            },
            new()
            {
                Id = "op",
                Email = "op@orbita.local",
                Role = PanelRoles.Operator,
                RoleLabel = "Оператор",
                ProfileId = "operator",
                OfficeId = officeTwo,
                OfficeName = "Сибирь",
                FullName = "Оператор"
            },
            new()
            {
                Id = "lost",
                Email = "lost@orbita.local",
                Role = PanelRoles.Operator,
                RoleLabel = "Оператор",
                ProfileId = "operator",
                FullName = "Без офиса"
            }
        };
        var offices = new List<OfficeDto>
        {
            new(officeTwo, "Сибирь", true, DateTime.UtcNow, 0, 1),
            new(officeOne, "Основной", true, DateTime.UtcNow, 0, 1)
        };

        var groups = SettingsIndexBuilder.BuildUserGroups(users, offices);

        Assert.Equal(4, groups.Count);
        Assert.Equal("admins", groups[0].Key);
        Assert.Equal(["admin"], groups[0].Users.Select(x => x.Id));
        Assert.Equal("office:11111111-1111-1111-1111-111111111111", groups[1].Key);
        Assert.Equal("Основной", groups[1].Title);
        Assert.Equal("office:22222222-2222-2222-2222-222222222222", groups[2].Key);
        Assert.Equal("Сибирь", groups[2].Title);
        Assert.Equal("unassigned", groups[3].Key);
        Assert.Equal(["lost"], groups[3].Users.Select(x => x.Id));
    }
}
