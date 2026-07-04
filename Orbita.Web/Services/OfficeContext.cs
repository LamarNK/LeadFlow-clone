using System.Security.Claims;
using Orbita.Contracts;

namespace Orbita.Web.Services;

public sealed class OfficeContext : IOfficeContext
{
    private bool _bound;

    public bool IsAdmin { get; private set; }

    public bool ShowAllOffices => IsAdmin && EffectiveOfficeId is null;

    public bool ShowOfficeColumn => ShowAllOffices;

    public Guid? EffectiveOfficeId { get; private set; }

    public string? ContextLabel { get; private set; }

    public void Bind(HttpContext context)
    {
        if (_bound)
        {
            return;
        }

        _bound = true;
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return;
        }

        IsAdmin = user.IsInRole(PanelRoles.Admin);
        if (IsAdmin)
        {
            BindAdmin(context);
            return;
        }

        BindOperator(user);
    }

    private void BindAdmin(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(OfficeSelection.CookieName, out var raw)
            && Guid.TryParse(raw, out var officeId))
        {
            EffectiveOfficeId = officeId;
            ContextLabel = context.Request.Cookies.TryGetValue(OfficeSelection.NameCookieName, out var name)
                && !string.IsNullOrWhiteSpace(name)
                ? $"Офис: {name}"
                : "Офис";
            return;
        }

        EffectiveOfficeId = null;
        ContextLabel = "Все офисы";
    }

    private void BindOperator(ClaimsPrincipal user)
    {
        if (Guid.TryParse(user.FindFirstValue(OfficeClaims.OfficeId), out var officeId))
        {
            EffectiveOfficeId = officeId;
            ContextLabel = null;
        }
    }
}