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
