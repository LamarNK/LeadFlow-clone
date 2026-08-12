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

public static class OrbitaEndpointMappings
{
    public static void MapOrbitaEndpoints(this WebApplication app)
    {
        WorkerEndpoints.Map(app);
        DashboardEndpoints.Map(app);
        AdminEndpoints.Map(app);
        OfficeStaffEndpoints.Map(app);
        PanelEndpoints.Map(app);
        PanelResponseEndpoints.Map(app);
        BitrixWorkforceEndpoints.Map(app);
        CrmEndpoints.Map(app);
        WorkerPanelEndpoints.Map(app);
        AuthenticationEndpoints.Map(app);
    }
}
