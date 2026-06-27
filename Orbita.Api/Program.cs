using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using Orbita.Logging.Audit;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrbitaLogging("Orbita.Api");

builder.Services.AddDbContext<OrbitaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<OrbitaDbContext>()
    .AddDefaultTokenProviders();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "Orbita",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "Orbita.Web",
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey))
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = async context =>
            {
                await GlobalLogger.Instance.LogAsync(
                    $"JWT authentication failed: {context.Exception.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.jwt.failed",
                    properties: new Dictionary<string, object?>
                    {
                        ["http.path"] = context.HttpContext.Request.Path.Value
                    });
            },
            OnChallenge = async context =>
            {
                if (context.AuthenticateFailure is not null)
                {
                    return;
                }

                await GlobalLogger.Instance.LogAsync(
                    $"JWT challenge: missing or invalid token for {context.Request.Method} {context.Request.Path}.",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.jwt.challenge",
                    properties: new Dictionary<string, object?>
                    {
                        ["http.path"] = context.Request.Path.Value
                    });
            },
            OnTokenValidated = async context =>
            {
                var userManager = context.HttpContext.RequestServices
                    .GetRequiredService<UserManager<IdentityUser>>();
                await JwtSecurityStampValidator.ValidateAsync(context, userManager);
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, WorkerApiKeyAuthenticationHandler>(
        WorkerApiKeyAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Worker", policy =>
    {
        policy.AddAuthenticationSchemes(WorkerApiKeyAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy("Panel", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy("Admin", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireRole(PanelRoles.Admin);
    });
});
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["https://localhost:7123", "http://localhost:5123"])
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddScoped<TelemetryService>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<PanelAuditService>();
builder.Services.AddScoped<PanelUserService>();
builder.Services.AddScoped<WorkerAdminService>();
builder.Services.AddScoped<PasswordPolicyService>();
builder.Services.AddScoped<ServiceLogsQueryService>();
builder.Services.AddScoped<WebhookSecretProtector>();
builder.Services.AddScoped<BitrixWebhookValidator>();
builder.Services.AddScoped<PanelBitrixIntegrationService>();
builder.Services.AddDataProtection();
builder.Services.AddHttpClient(nameof(BitrixWebhookValidator), client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();
app.UseOrbitaLogging();

await SeedAsync(app);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Web");
app.UseAuthentication();
app.UseAuthorization();

var workers = app.MapGroup("/api/v1/workers");
workers.MapPost("/register", async (WorkerRegisterRequest request, TelemetryService telemetry, IConfiguration config, CancellationToken ct) =>
{
    var secret = config["RegistrationSecret"] ?? string.Empty;
    var result = await telemetry.RegisterAsync(request, secret, ct);
    if (result is null)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker registration rejected: invalid registration secret ({request.MachineName}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.registration_rejected");
        return Results.Unauthorized();
    }

    await GlobalLogger.Instance.LogAsync(
        $"Worker registered ({result.WorkerId}, {request.DisplayName}, {request.MachineName}).",
        DeskLinkAuditLogLevel.Info);
    return Results.Ok(result);
});

workers.MapPost("/heartbeat", async (WorkerHeartbeatRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker heartbeat forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.worker_id_mismatch");
        return Results.Forbid();
    }

    return await telemetry.HeartbeatAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/telemetry/snapshot", async (WorkerSnapshotRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker snapshot forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.worker_id_mismatch");
        return Results.Forbid();
    }

    return await telemetry.SaveSnapshotAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/telemetry/events", async (WorkerEventBatchRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker events forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.worker_id_mismatch");
        return Results.Forbid();
    }

    return await telemetry.SaveEventsAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

var dashboard = app.MapGroup("/api/v1/dashboard").RequireAuthorization("Panel");
dashboard.MapGet("/summary", async (DashboardQueryService query, CancellationToken ct) =>
    Results.Ok(await query.GetGlobalSummaryAsync(ct)));

var workerRead = app.MapGroup("/api/v1").RequireAuthorization("Panel");
workerRead.MapGet("/workers", async (DashboardQueryService query, CancellationToken ct) =>
    Results.Ok(await query.GetWorkersAsync(ct)));
workerRead.MapGet("/workers/{id:guid}", async (Guid id, DashboardQueryService query, CancellationToken ct) =>
{
    var detail = await query.GetWorkerDetailAsync(id, ct);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});
workerRead.MapGet("/workers/{id:guid}/accounts", async (Guid id, DashboardQueryService query, CancellationToken ct) =>
    Results.Ok(await query.GetWorkerAccountsAsync(id, ct)));
workerRead.MapGet("/events", async (Guid? workerId, int? limit, DashboardQueryService query, CancellationToken ct) =>
    Results.Ok(await query.GetWorkerEventsAsync(workerId, Math.Clamp(limit ?? 100, 1, 500), ct)));

var admin = app.MapGroup("/api/v1/admin").RequireAuthorization("Admin");
admin.MapGet("/users", async (PanelUserService panelUsers, CancellationToken ct) =>
    Results.Ok(await panelUsers.ListAsync(ct)));

admin.MapPost("/users", async (
    CreatePanelUserRequest request,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (user, error) = await panelUsers.CreateAsync(
        request.Email,
        request.Password,
        request.Role,
        GetActor(principal, http),
        ct);
    if (error is not null)
    {
        return error.Contains("уже существует", StringComparison.OrdinalIgnoreCase)
            ? Results.Conflict(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(user);
});

admin.MapDelete("/users/{id}", async (
    string id,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var error = await panelUsers.DeleteAsync(
        id,
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.NoContent();
    }

    return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
        ? Results.NotFound(new { error })
        : Results.BadRequest(new { error });
});

admin.MapPost("/users/{id}/password", async (
    string id,
    ResetPanelUserPasswordRequest request,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var error = await panelUsers.ResetPasswordAsync(
        id,
        request.Password,
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.NoContent();
    }

    return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
        ? Results.NotFound(new { error })
        : Results.BadRequest(new { error });
});

admin.MapPut("/users/{id}/role", async (
    string id,
    UpdatePanelUserRoleRequest request,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (user, error) = await panelUsers.SetRoleAsync(
        id,
        request.Role,
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.Ok(user);
    }

    return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
        ? Results.NotFound(new { error })
        : Results.BadRequest(new { error });
});

admin.MapPost("/users/{id}/lock", async (
    string id,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (user, error) = await panelUsers.LockAsync(
        id,
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.Ok(user);
    }

    return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
        ? Results.NotFound(new { error })
        : Results.BadRequest(new { error });
});

admin.MapPost("/users/{id}/unlock", async (
    string id,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (user, error) = await panelUsers.UnlockAsync(id, GetActor(principal, http), ct);
    if (error is null)
    {
        return Results.Ok(user);
    }

    return Results.NotFound(new { error });
});

admin.MapPost("/users/{id}/revoke-sessions", async (
    string id,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var error = await panelUsers.RevokeSessionsAsync(
        id,
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.NoContent();
    }

    return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
        ? Results.NotFound(new { error })
        : Results.BadRequest(new { error });
});

admin.MapGet("/integrations/bitrix", async (
    PanelBitrixIntegrationService integrations,
    CancellationToken ct) =>
    Results.Ok(await integrations.ListAllAsync(ct)));

admin.MapGet("/users/{userId}/integrations/bitrix", async (
    string userId,
    PanelBitrixIntegrationService integrations,
    CancellationToken ct) =>
{
    var integration = await integrations.GetForUserAsync(userId, ct);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
});

admin.MapPut("/users/{userId}/integrations/bitrix", async (
    string userId,
    SaveBitrixIntegrationRequest request,
    PanelBitrixIntegrationService integrations,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (integration, error) = await integrations.SaveAsync(
        userId,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(integration);
});

admin.MapPost("/users/{userId}/integrations/bitrix/validate", async (
    string userId,
    ValidateBitrixIntegrationRequest request,
    PanelBitrixIntegrationService integrations,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (validation, error) = await integrations.ValidateAsync(
        userId,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        persistResult: string.IsNullOrWhiteSpace(request.WebhookUrl),
        ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(validation);
});

admin.MapGet("/workers", async (WorkerAdminService workers, CancellationToken ct) =>
    Results.Ok(await workers.ListAsync(ct)));

admin.MapPut("/workers/{id:guid}", async (
    Guid id,
    UpdateAdminWorkerRequest request,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (worker, error) = await workers.RenameAsync(id, request.DisplayName, ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerRenamed,
        "worker",
        id.ToString(),
        request.DisplayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker renamed ({id}, name={request.DisplayName}).",
        DeskLinkAuditLogLevel.Info);

    return Results.Ok(worker);
});

admin.MapPost("/workers/{id:guid}/enable", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (worker, error) = await workers.SetEnabledAsync(id, true, ct);
    if (error is not null)
    {
        return Results.NotFound(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerEnabled,
        "worker",
        id.ToString(),
        worker!.DisplayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker enabled ({id}, {worker.DisplayName}).",
        DeskLinkAuditLogLevel.Info);

    return Results.Ok(worker);
});

admin.MapPost("/workers/{id:guid}/disable", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (worker, error) = await workers.SetEnabledAsync(id, false, ct);
    if (error is not null)
    {
        return Results.NotFound(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerDisabled,
        "worker",
        id.ToString(),
        worker!.DisplayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker disabled ({id}, {worker.DisplayName}).",
        DeskLinkAuditLogLevel.Warning);

    return Results.Ok(worker);
});

admin.MapPost("/workers/{id:guid}/rotate-key", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (result, error) = await workers.RotateApiKeyAsync(id, ct);
    if (error is not null)
    {
        return Results.NotFound(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerKeyRotated,
        "worker",
        id.ToString(),
        null,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker API key rotated ({id}).",
        DeskLinkAuditLogLevel.Warning);

    return Results.Ok(result);
});

admin.MapGet("/workers/registration", (WorkerAdminService workers) =>
    Results.Ok(workers.GetRegistrationInfo()));

admin.MapGet("/audit", async (
    string? q,
    string? action,
    DateOnly? date,
    int? page,
    int? pageSize,
    PanelAuditService audit,
    CancellationToken ct) =>
    Results.Ok(await audit.SearchAsync(
        q,
        action,
        date,
        page ?? 1,
        pageSize ?? PanelAuditService.DefaultPageSize,
        ct)));

admin.MapGet("/security/policy", (PasswordPolicyService policy) =>
    Results.Ok(policy.GetPolicy()));

admin.MapGet("/logs", async (
    string? q,
    string? level,
    string? service,
    DateTime? date,
    int? page,
    int? pageSize,
    ServiceLogsQueryService logs,
    CancellationToken ct) =>
    Results.Ok(await logs.SearchAsync(
        q,
        level,
        service,
        date,
        page ?? 1,
        pageSize ?? ServiceLogsQueryService.DefaultPageSize,
        ct)));

var panel = app.MapGroup("/api/v1/panel").RequireAuthorization("Panel");
panel.MapGet("/me", async (
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var profile = await panelUsers.GetProfileAsync(userId, ct);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

panel.MapPost("/me/password", async (
    ChangeOwnPasswordRequest request,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var error = await panelUsers.ChangeOwnPasswordAsync(
        userId,
        request.CurrentPassword,
        request.NewPassword,
        GetActor(principal, http),
        ct);
    if (error is null)
    {
        return Results.NoContent();
    }

    return Results.BadRequest(new { error });
});

panel.MapGet("/security/policy", (PasswordPolicyService policy) =>
    Results.Ok(policy.GetPolicy()));

panel.MapGet("/me/integrations/bitrix", async (
    PanelBitrixIntegrationService integrations,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await integrations.GetForUserAsync(userId, ct));
});

panel.MapPut("/me/integrations/bitrix", async (
    SaveBitrixIntegrationRequest request,
    PanelBitrixIntegrationService integrations,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var actor = GetActor(principal, http);
    var (integration, error) = await integrations.SaveAsync(
        userId,
        request.WebhookUrl,
        userId,
        actor.Email,
        actor.IpAddress,
        ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(integration);
});

panel.MapPost("/me/integrations/bitrix/validate", async (
    ValidateBitrixIntegrationRequest request,
    PanelBitrixIntegrationService integrations,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var actor = GetActor(principal, http);
    var (validation, error) = await integrations.ValidateAsync(
        userId,
        request.WebhookUrl,
        userId,
        actor.Email,
        actor.IpAddress,
        persistResult: string.IsNullOrWhiteSpace(request.WebhookUrl),
        ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(validation);
});

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    PanelAuditService audit,
    IConfiguration config,
    HttpContext http,
    CancellationToken ct) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString();
    var user = await users.FindByEmailAsync(request.Email);
    if (user is null)
    {
        await audit.LogAsync(null, request.Email, PanelAuditActions.LoginFailed, "user", null, "user_not_found", ip, ct);
        await GlobalLogger.Instance.LogAsync(
            $"Login failed: user not found ({request.Email}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.login.user_not_found");
        return Results.Unauthorized();
    }

    if (await users.IsLockedOutAsync(user))
    {
        await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "locked_out", ip, ct);
        await GlobalLogger.Instance.LogAsync(
            $"Login failed: account locked ({request.Email}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.login.locked_out");
        return Results.Unauthorized();
    }

    var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
    if (!result.Succeeded)
    {
        await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "invalid_password", ip, ct);
        await GlobalLogger.Instance.LogAsync(
            $"Login failed: invalid password ({request.Email}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.login.invalid_password");
        return Results.Unauthorized();
    }

    var roles = await users.GetRolesAsync(user);
    var token = JwtTokenFactory.CreateToken(user, roles, config);
    await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginSucceeded, "user", user.Id, null, ip, ct);
    await GlobalLogger.Instance.LogAsync(
        $"Login succeeded ({request.Email}).",
        DeskLinkAuditLogLevel.Info);
    return Results.Ok(new LoginResponse(token, user.Email ?? request.Email));
});

app.Run();

static AuditActor GetActor(ClaimsPrincipal principal, HttpContext http) =>
    new(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        http.Connection.RemoteIpAddress?.ToString());

static bool TryGetWorkerId(ClaimsPrincipal user, out Guid workerId)
{
    var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(value, out workerId);
}

static async Task SeedAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
    await db.Database.MigrateAsync();

    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var email = config["Admin:Email"] ?? "admin@orbita.local";
    var password = config["Admin:Password"] ?? "OrbitaAdmin1!";

    foreach (var roleName in PanelRoles.All)
    {
        if (!await roles.RoleExistsAsync(roleName))
        {
            await roles.CreateAsync(new IdentityRole(roleName));
        }
    }

    if (await users.FindByEmailAsync(email) is null)
    {
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, PanelRoles.Admin);

        await GlobalLogger.Instance.LogAsync(
            $"Admin user seeded ({email}).",
            DeskLinkAuditLogLevel.Info);
    }
    else if (await users.FindByEmailAsync(email) is { } existingAdmin
             && !await users.IsInRoleAsync(existingAdmin, PanelRoles.Admin))
    {
        await users.AddToRoleAsync(existingAdmin, PanelRoles.Admin);
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, string Email);

static class JwtTokenFactory
{
    public static string CreateToken(IdentityUser user, IEnumerable<string> roles, IConfiguration config)
    {
        var key = config["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
        var issuer = config["Jwt:Issuer"] ?? "Orbita";
        var audience = config["Jwt:Audience"] ?? "Orbita.Web";
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(JwtSecurityStampValidator.SecurityStampClaimType, user.SecurityStamp ?? string.Empty)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}