using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Orbita.Contracts;

namespace Orbita.Api.Hubs;

[Authorize(Policy = "Panel")]
public sealed class PanelHub : Hub
{
    public const string GlobalGroup = "global";

    public static string OfficeGroup(Guid officeId) => $"office:{officeId:D}";

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            if (principal.IsInRole(PanelRoles.Admin))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GlobalGroup);
            }

            if (Guid.TryParse(principal.FindFirstValue(OfficeClaims.OfficeId), out var officeId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, OfficeGroup(officeId));
            }
        }

        await base.OnConnectedAsync();
    }
}