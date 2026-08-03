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

public static class EndpointRequestContext
{
    public static AuditActor GetActor(ClaimsPrincipal principal, HttpContext http) =>
        new(
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            principal.FindFirstValue(ClaimTypes.Email),
            http.Connection.RemoteIpAddress?.ToString());

    public static bool TryGetWorkerId(ClaimsPrincipal user, out Guid workerId)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out workerId);
    }
}
