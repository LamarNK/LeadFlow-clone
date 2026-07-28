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

var builder = WebApplication.CreateBuilder(args);
builder.AddOrbitaLogging("Orbita.Api");

const long defaultMaxUploadBytes = 536_870_912;
var maxUploadBytes = builder.Configuration.GetValue<long?>("WorkerReleases:MaxUploadBytes") ?? defaultMaxUploadBytes;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

builder.Services.AddDbContext<OrbitaDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

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
                var message = context.Exception.Message;
                if (message.Contains("IDX14100", StringComparison.Ordinal)
                    || message.Contains("no dots", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await GlobalLogger.Instance.LogAsync(
                    $"JWT authentication failed: {message}",
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
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && accessToken.ToString().Contains('.', StringComparison.Ordinal)
                    && (path.StartsWithSegments("/hubs/panel", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWithSegments("/hubs/captcha", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWithSegments("/hubs/worker", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWithSegments("/hubs/browser-monitor", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
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
    options.AddPolicy("WorkerOrPanel", policy =>
    {
        policy.AddAuthenticationSchemes(
            JwtBearerDefaults.AuthenticationScheme,
            WorkerApiKeyAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });
});
builder.Services.AddOpenApi();
builder.Services.AddSignalR(options =>
    {
        // MHTML captcha snapshots are far above the 32 KB default.
        options.MaximumReceiveMessageSize = 16 * 1024 * 1024;
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddSingleton<IPanelRealtimeNotifier, PanelRealtimeNotifier>();
builder.Services.AddSingleton<WorkerConnectionRegistry>();
builder.Services.AddSingleton<IWorkerPushNotifier, WorkerPushNotifier>();
builder.Services.AddSingleton<CaptchaRelayRegistry>();
builder.Services.AddSingleton<BrowserMonitorRegistry>();
builder.Services.AddSingleton<BrowserMonitorService>();
builder.Services.AddSingleton<ICaptchaSessionRelayNotifier, CaptchaSessionRelayNotifier>();
builder.Services.AddScoped<CaptchaSessionService>();
builder.Services.AddSingleton<ICaptchaLockNotifier, CaptchaLockNotifier>();
builder.Services.AddHostedService<CaptchaSessionSweeperService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["https://localhost:7123", "http://localhost:5123"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.AddScoped<TelemetryService>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<OfficeStatisticsQueryService>();
builder.Services.AddScoped<PanelAuditService>();
builder.Services.AddScoped<PanelUserService>();
builder.Services.AddScoped<OfficeScopeService>();
builder.Services.AddScoped<OfficeAdminService>();
builder.Services.AddScoped<WorkerAdminService>();
builder.Services.AddScoped<WorkerConfigService>();
builder.Services.AddScoped<WorkerScheduleService>();
builder.Services.AddScoped<WorkerCommandService>();
builder.Services.AddScoped<WorkerEventService>();
builder.Services.Configure<WorkerReleaseOptions>(builder.Configuration.GetSection(WorkerReleaseOptions.SectionName));
builder.Services.Configure<WorkerDiagnosticsOptions>(builder.Configuration.GetSection(WorkerDiagnosticsOptions.SectionName));
builder.Services.Configure<WorkerLogsOptions>(builder.Configuration.GetSection(WorkerLogsOptions.SectionName));
builder.Services.Configure<ServiceLogsOptions>(builder.Configuration.GetSection(ServiceLogsOptions.SectionName));
builder.Services.AddSingleton<WorkerReleaseService>();
builder.Services.AddScoped<WorkerDiagnosticsService>();
builder.Services.AddHostedService<WorkerDiagnosticsCleanupService>();
builder.Services.AddScoped<WorkerLogsService>();
builder.Services.AddHostedService<WorkerLogsCleanupService>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});
builder.Services.AddScoped<CandidateIngestionService>();
builder.Services.AddScoped<CrmLeadDistributionService>();
builder.Services.AddScoped<CrmWorkspaceService>();
builder.Services.AddScoped<BitrixInstanceService>();
builder.Services.AddScoped<DistributionRouteService>();
builder.Services.AddScoped<LeadExportQuotaService>();
builder.Services.AddScoped<DistributionEngine>();
builder.Services.AddScoped<CandidateAutoDistributionService>();
builder.Services.AddScoped<BitrixDuplicateCheckAllService>();
builder.Services.AddScoped<CandidateBitrixSendService>();
builder.Services.AddScoped<ResponseBitrixDeliveryService>();
builder.Services.AddScoped<ManualBitrixSendService>();
builder.Services.AddScoped<BulkResponsesBitrixSendService>();
builder.Services.AddScoped<BitrixLegacyMigrationService>();
builder.Services.AddScoped<CandidateLookupService>();
builder.Services.AddScoped<WorkerMonitoringStatsService>();
builder.Services.AddScoped<CandidatePersonMatchService>();
builder.Services.AddScoped<CandidatePersonPhoneService>();
builder.Services.AddScoped<CandidateDuplicateService>();
builder.Services.AddScoped<OfficeBitrixWebhookResolver>();
builder.Services.AddScoped<OfficeBitrixSettingsService>();
builder.Services.AddScoped<OfficeBitrixIntegrationService>();
builder.Services.AddScoped<ResponsesQueryService>();
builder.Services.AddSingleton<PhoneNormalizer>();
builder.Services.AddSingleton<CandidateParser>();
builder.Services.AddSingleton<BitrixClient>();
builder.Services.Configure<OrbitaBitrixSettings>(builder.Configuration.GetSection("Bitrix"));
builder.Services.AddScoped<PasswordPolicyService>();
builder.Services.AddScoped<ServiceLogsQueryService>();
builder.Services.AddHostedService<ServiceLogsCleanupService>();
builder.Services.AddHostedService<WorkerScheduleHostedService>();
builder.Services.AddScoped<WebhookSecretProtector>();
builder.Services.AddScoped<AvitoAccountSecretProtector>();
builder.Services.AddScoped<BitrixWebhookValidator>();
builder.Services.AddScoped<PanelBitrixIntegrationService>();
builder.Services.Configure<LeadFlowImportOptions>(builder.Configuration.GetSection("LeadFlowImport"));
builder.Services.AddSingleton<LeadFlowDatabaseReader>();
builder.Services.AddScoped<LeadFlowImportService>();
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("Orbita.Api");
}
else
{
    builder.Services.AddDataProtection()
        .SetApplicationName("Orbita.Api");
}
builder.Services.AddHttpClient(nameof(BitrixWebhookValidator), client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(nameof(BitrixClient), client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
app.UseOrbitaLogging();
app.UseForwardedHeaders();

await SeedAsync(app);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Web");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<PanelHub>("/hubs/panel");
app.MapHub<CaptchaRelayHub>("/hubs/captcha");
app.MapHub<BrowserMonitorHub>("/hubs/browser-monitor");
app.MapHub<WorkerHub>("/hubs/worker");

var workers = app.MapGroup("/api/v1/workers");
workers.MapPost("/register", async (WorkerRegisterRequest request, TelemetryService telemetry, IConfiguration config, CancellationToken ct) =>
{
    var secret = config["RegistrationSecret"];
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

workers.MapPost("/heartbeat", async (WorkerHeartbeatRequest request, TelemetryService telemetry, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker heartbeat forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.worker_id_mismatch");
        return Results.Forbid();
    }

    var clientIp = ClientIpResolver.Resolve(http);
    return await telemetry.HeartbeatAsync(request, clientIp, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/activity", async (WorkerActivityRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        await GlobalLogger.Instance.LogAsync(
            $"Worker activity forbidden: token worker mismatch (token={workerId}, request={request.WorkerId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "auth.worker.worker_id_mismatch");
        return Results.Forbid();
    }

    return await telemetry.SaveActivityAsync(request, ct) ? Results.Ok() : Results.NotFound();
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

workers.MapGet("/config", async (WorkerConfigService configService, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var config = await configService.GetConfigForWorkerAsync(workerId, OfficeScope.GlobalAdmin, consumePendingCommand: true, ct);
    return config is null ? Results.NotFound() : Results.Ok(config);
}).RequireAuthorization("Worker");

workers.MapPost("/captcha-sessions/status", async (
    UpdateCaptchaSessionStatusRequest request,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var (success, error) = await captchaSessions.UpdateStatusFromWorkerAsync(workerId, request, ct);
    return success ? Results.Ok() : Results.BadRequest(new { error });
}).RequireAuthorization("Worker");

workers.MapGet("/captcha-sessions/{id:guid}", async (
    Guid id,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var session = await captchaSessions.GetForWorkerAsync(workerId, id, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
}).RequireAuthorization("Worker");

workers.MapPost("/accounts/sync", async (
    WorkerAccountSyncRequest request,
    WorkerConfigService configService,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    return await configService.SyncAccountsAsync(workerId, request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/candidates", async (
    WorkerCandidateBatchRequest request,
    CandidateIngestionService ingestion,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    return Results.Ok(await ingestion.IngestBatchAsync(workerId, request, ct));
}).RequireAuthorization("Worker");

workers.MapGet("/monitoring-stats", async (
    WorkerMonitoringStatsService statsService,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var stats = await statsService.GetForWorkerAsync(workerId, ct);
    return stats is null ? Results.NotFound() : Results.Ok(stats);
}).RequireAuthorization("Worker");

workers.MapPost("/candidates/lookup", async (
    WorkerCandidateLookupRequest request,
    CandidateLookupService lookup,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var result = await lookup.LookupAsync(workerId, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireAuthorization("Worker");

workers.MapGet("/updates/check", async (
    string? currentVersion,
    WorkerReleaseService releases,
    CancellationToken ct) =>
    Results.Ok(await releases.CheckUpdateAsync(currentVersion, ct)))
    .RequireAuthorization("Worker");

workers.MapGet("/updates/download", async (
    string? version,
    WorkerReleaseService releases,
    CancellationToken ct) =>
{
    var (stream, fileName, error) = await releases.OpenPackageAsync(version, ct);
    if (stream is null || fileName is null)
    {
        return Results.NotFound(new { error = error ?? "Релиз не найден." });
    }

    return Results.File(stream, "application/octet-stream", fileName);
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

workers.MapPost("/diagnostics/upload", async (
    HttpRequest request,
    WorkerDiagnosticsService diagnostics,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
    }

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Файл не передан." });
    }

    Guid? accountId = null;
    if (Guid.TryParse(form["accountId"], out var parsedAccountId))
    {
        accountId = parsedAccountId;
    }

    var kind = form["kind"].ToString();
    var pageUrl = form["pageUrl"].ToString();

    await using var stream = file.OpenReadStream();
    var (attachmentId, error) = await diagnostics.SaveUploadAsync(
        workerId,
        accountId,
        kind,
        string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl,
        stream,
        file.Length,
        ct);

    return attachmentId is null
        ? Results.BadRequest(new { error = error ?? "Не удалось сохранить вложение." })
        : Results.Ok(new WorkerDiagnosticUploadResponse(attachmentId.Value));
}).RequireAuthorization("Worker")
.DisableAntiforgery();

workers.MapPost("/logs/batch", async (
    WorkerLogsBatchRequest request,
    WorkerLogsService logs,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId))
    {
        return Results.Forbid();
    }

    var entries = request.Entries ?? [];
    var (accepted, error) = await logs.IngestBatchAsync(workerId, entries, ct);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(new { accepted });
}).RequireAuthorization("Worker");

var dashboard = app.MapGroup("/api/v1/dashboard").RequireAuthorization("Panel");
dashboard.MapGet("/diagnostics/{id:guid}/image", async (
    Guid id,
    WorkerDiagnosticsService diagnostics,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (stream, contentType, error) = await diagnostics.OpenForPanelAsync(id, scope, ct);
    return stream is null
        ? Results.NotFound(new { error = error ?? "Вложение не найдено." })
        : Results.File(stream, contentType ?? "image/png");
});

dashboard.MapGet("/summary", async (
    Guid? officeId,
    DashboardQueryService query,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await query.GetGlobalSummaryAsync(scope, officeId, ct));
});

var workerRead = app.MapGroup("/api/v1").RequireAuthorization("Panel");
workerRead.MapGet("/workers", async (
    Guid? officeId,
    DashboardQueryService query,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await query.GetWorkersAsync(scope, officeId, ct));
});
workerRead.MapGet("/workers/{id:guid}", async (
    Guid id,
    DashboardQueryService query,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var detail = await query.GetWorkerDetailAsync(id, scope, ct);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});
workerRead.MapGet("/workers/{id:guid}/accounts", async (
    Guid id,
    DashboardQueryService query,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await query.GetWorkerAccountsAsync(id, scope, ct));
});
workerRead.MapGet("/events", async (
    Guid? workerId,
    Guid? officeId,
    int? limit,
    DateTime? since,
    DashboardQueryService query,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await query.GetWorkerEventsAsync(
        scope,
        workerId,
        officeId,
        Math.Clamp(limit ?? 100, 1, 500),
        since,
        ct));
});
workerRead.MapGet("/worker-releases/latest", async (WorkerReleaseService releases, CancellationToken ct) =>
{
    var latest = await releases.GetLatestAsync(ct);
    return latest is null ? Results.NotFound() : Results.Ok(latest);
});

app.MapGet("/api/v1/public/worker-releases/latest/download", async (WorkerReleaseService releases, CancellationToken ct) =>
{
    var (stream, fileName, error) = await releases.OpenPackageAsync(version: null, ct);
    if (stream is null || fileName is null)
    {
        return Results.NotFound(new { error = error ?? "Релиз не найден." });
    }

    return Results.File(stream, "application/octet-stream", fileName);
});

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
        request.OfficeId,
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

admin.MapPut("/users/{id}/office", async (
    string id,
    UpdatePanelUserOfficeRequest request,
    PanelUserService panelUsers,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (user, error) = await panelUsers.SetOfficeAsync(
        id,
        request.OfficeId,
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
    OfficeAdminService offices,
    CancellationToken ct) =>
    Results.Ok(await offices.ListAsync(ct)));

admin.MapGet("/offices/{id:guid}/integrations/bitrix", async (
    Guid id,
    OfficeBitrixIntegrationService bitrix,
    CancellationToken ct) =>
{
    var integration = await bitrix.GetForOfficeAsync(id, ct);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
});

admin.MapPut("/offices/{id:guid}/integrations/bitrix", async (
    Guid id,
    SaveBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService bitrix,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (integration, error) = await bitrix.SaveAsync(
        id,
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

admin.MapPost("/offices/{id:guid}/integrations/bitrix/validate", async (
    Guid id,
    ValidateBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService bitrix,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (validation, error) = await bitrix.ValidateAsync(
        id,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        persistResult: true,
        ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(validation);
});

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

admin.MapGet("/offices", async (OfficeAdminService offices, CancellationToken ct) =>
    Results.Ok(await offices.ListAsync(ct)));

admin.MapGet("/offices/{id:guid}", async (Guid id, OfficeAdminService offices, CancellationToken ct) =>
{
    var office = await offices.GetAsync(id, ct);
    return office is null ? Results.NotFound() : Results.Ok(office);
});

admin.MapPost("/offices", async (
    CreateOfficeRequest request,
    OfficeAdminService offices,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (office, error) = await offices.CreateAsync(request.Name, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditOfficeActions.OfficeCreated,
        "office",
        office!.Id.ToString(),
        office.Name,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(office);
});

admin.MapPut("/offices/{id:guid}", async (
    Guid id,
    UpdateOfficeRequest request,
    OfficeAdminService offices,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (office, error) = await offices.UpdateAsync(
        id,
        request.Name,
        request.IsEnabled,
        request.BitrixTransmissionEnabled,
        ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditOfficeActions.OfficeUpdated,
        "office",
        id.ToString(),
        request.Name,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(office);
});

admin.MapPost("/offices/{id:guid}/rotate-registration-secret", async (
    Guid id,
    OfficeAdminService offices,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (result, error) = await offices.RotateRegistrationSecretAsync(id, ct);
    if (error is not null)
    {
        return Results.NotFound(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditOfficeActions.OfficeRegistrationRotated,
        "office",
        id.ToString(),
        null,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(result);
});

admin.MapGet("/offices/{id:guid}/registration", async (Guid id, OfficeAdminService offices, CancellationToken ct) =>
{
    var info = await offices.GetRegistrationInfoAsync(id, ct);
    return info is null ? Results.NotFound() : Results.Ok(info);
});

admin.MapGet("/workers", async (
    Guid? officeId,
    WorkerAdminService workers,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = OfficeScope.GlobalAdmin;
    return Results.Ok(await workers.ListAsync(scope, officeId, ct));
});

admin.MapPost("/workers/create", async (
    CreateWorkerRequest request,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = OfficeScope.GlobalAdmin;
    var (result, error) = await workers.CreateAsync(request.DisplayName, request.OfficeId, scope, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerCreated,
        "worker",
        result!.WorkerId.ToString(),
        result.DisplayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker created ({result.WorkerId}, {result.DisplayName}).",
        DeskLinkAuditLogLevel.Info);

    return Results.Ok(result);
});

admin.MapPut("/workers/{id:guid}", async (
    Guid id,
    UpdateAdminWorkerRequest request,
    WorkerAdminService workers,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = OfficeScope.GlobalAdmin;
    var (worker, error) = await workers.RenameAsync(id, request.DisplayName, scope, ct);
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
    var scope = OfficeScope.GlobalAdmin;
    var (worker, error) = await workers.SetEnabledAsync(id, true, scope, ct);
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
    var scope = OfficeScope.GlobalAdmin;
    var (worker, error) = await workers.SetEnabledAsync(id, false, scope, ct);
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
    var scope = OfficeScope.GlobalAdmin;
    var (result, error) = await workers.RotateApiKeyAsync(id, scope, ct);
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

admin.MapPost("/leadflow-import/preview", async (
    HttpRequest request,
    LeadFlowImportService importService,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
    }

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("databaseFile");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Файл databaseFile не передан." });
    }

    if (!Guid.TryParse(form["officeId"].FirstOrDefault(), out var officeId))
    {
        return Results.BadRequest(new { error = "Укажите офис для импорта." });
    }

    var encryptionKey = form["encryptionKey"].FirstOrDefault();
    await using var stream = file.OpenReadStream();
    var (preview, error) = await importService.PreviewAsync(
        stream,
        file.FileName,
        officeId,
        encryptionKey,
        ct);
    return error is null ? Results.Ok(preview) : Results.BadRequest(new { error });
}).DisableAntiforgery();

admin.MapPost("/leadflow-import/execute", async (
    LeadFlowImportExecuteRequest request,
    LeadFlowImportService importService,
    PanelAuditService audit,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var (result, error) = await importService.ExecuteAsync(request.SessionId, request.SelectedIds, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.LeadFlowImportExecuted,
        "office",
        null,
        $"imported={result!.Imported}; skipped={result.Skipped}; failed={result.Failed}",
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(result);
});

admin.MapGet("/worker-releases", async (WorkerReleaseService releases, CancellationToken ct) =>
    Results.Ok(await releases.ListAsync(ct)));

admin.MapPost("/worker-releases/upload", async (HttpRequest request, WorkerReleaseService releases, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
    }

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("packageFile");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Файл packageFile не передан." });
    }

    await using var stream = file.OpenReadStream();
    var (release, error) = await releases.UploadAsync(
        stream,
        file.FileName,
        form["version"].FirstOrDefault(),
        form["releaseNotes"].FirstOrDefault(),
        ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(release);
}).DisableAntiforgery();

admin.MapPost("/worker-releases/set-latest", async (
    SetWorkerReleaseLatestRequest request,
    WorkerReleaseService releases,
    CancellationToken ct) =>
{
    var (success, error) = await releases.SetLatestAsync(request.Version, ct);
    return success ? Results.Ok() : Results.BadRequest(new { error });
});

admin.MapPost("/worker-releases/delete", async (
    DeleteWorkerReleaseRequest request,
    WorkerReleaseService releases,
    CancellationToken ct) =>
{
    var (success, error) = await releases.DeleteAsync(request.Version, ct);
    return success ? Results.Ok() : Results.BadRequest(new { error });
});

admin.MapGet("/worker-releases/{version}/download", async (
    string version,
    WorkerReleaseService releases,
    CancellationToken ct) =>
{
    var (stream, fileName, error) = await releases.OpenPackageAsync(version, ct);
    if (stream is null || fileName is null)
    {
        return Results.NotFound(new { error = error ?? "Релиз не найден." });
    }

    return Results.File(stream, "application/octet-stream", fileName);
});

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

admin.MapGet("/workers/{workerId:guid}/logs", async (
    Guid workerId,
    string? q,
    string? level,
    DateTime? date,
    int? page,
    int? pageSize,
    WorkerLogsService logs,
    CancellationToken ct) =>
    Results.Ok(await logs.SearchAsync(
        workerId,
        q,
        level,
        date,
        page ?? 1,
        pageSize ?? WorkerLogsService.DefaultPageSize,
        ct)));

var panel = app.MapGroup("/api/v1/panel").RequireAuthorization("Panel");
panel.MapPost("/captcha-sessions", async (
    CreateCaptchaSessionRequest request,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var (session, conflict) = await captchaSessions.CreateAsync(request, principal, ct);
    if (conflict is not null)
    {
        return Results.Conflict(new
        {
            error = conflict.Message,
            activeSessionId = conflict.ActiveSessionId,
            activeOperatorDisplayName = conflict.ActiveOperatorDisplayName,
            activeAccountName = conflict.ActiveAccountName
        });
    }

    return session is null
        ? Results.BadRequest(new { error = "Не удалось создать сессию." })
        : Results.Ok(session);
});

panel.MapGet("/captcha-sessions/{id:guid}", async (
    Guid id,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var session = await captchaSessions.GetAsync(id, principal, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

panel.MapPost("/captcha-sessions/{id:guid}/cancel", async (
    Guid id,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var (success, error) = await captchaSessions.CancelAsync(id, principal, ct);
    return success ? Results.Ok() : Results.BadRequest(new { error });
});

panel.MapGet("/workers/{id:guid}/captcha-lock", async (
    Guid id,
    CaptchaSessionService captchaSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var lockState = await captchaSessions.GetWorkerLockAsync(id, principal, ct);
    return lockState is null ? Results.NotFound() : Results.Ok(lockState);
});

panel.MapPost("/browser-monitor-sessions", async (
    Guid workerId,
    BrowserMonitorService browserMonitorSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var (session, error) = await browserMonitorSessions.StartAsync(workerId, principal, ct);
    return session is null
        ? Results.BadRequest(new { error = error ?? "Не удалось создать сессию просмотра." })
        : Results.Ok(session);
});

panel.MapGet("/browser-monitor-sessions/{id:guid}", async (
    Guid id,
    BrowserMonitorService browserMonitorSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var session = await browserMonitorSessions.GetAsync(id, principal, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

panel.MapPost("/browser-monitor-sessions/{id:guid}/stop", async (
    Guid id,
    BrowserMonitorService browserMonitorSessions,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var (success, error) = await browserMonitorSessions.StopAsync(id, principal, ct);
    return success ? Results.Ok() : Results.BadRequest(new { error });
});

workers.MapGet("/browser-monitor-sessions/{id:guid}/alive", (
    Guid id,
    BrowserMonitorService browserMonitorSessions,
    ClaimsPrincipal user) =>
{
    if (!TryGetWorkerId(user, out var workerId)
        || !browserMonitorSessions.TryAuthorizeWorker(id, workerId, out _))
    {
        return Results.NotFound();
    }

    return Results.Ok();
});

panel.MapPost("/workers/create", async (
    CreateWorkerRequest request,
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (result, error) = await workers.CreateAsync(request.DisplayName, request.OfficeId, scope, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerCreated,
        "worker",
        result!.WorkerId.ToString(),
        result.DisplayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker created ({result.WorkerId}, {result.DisplayName}).",
        DeskLinkAuditLogLevel.Info);

    return Results.Ok(result);
});

panel.MapPost("/workers/{id:guid}/enable", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (worker, error) = await workers.SetEnabledAsync(id, true, scope, ct);
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

    return Results.Ok(worker);
});

panel.MapPost("/workers/{id:guid}/disable", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (worker, error) = await workers.SetEnabledAsync(id, false, scope, ct);
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

    return Results.Ok(worker);
});

panel.MapPost("/workers/enable-all", async (
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (result, error) = await workers.SetAllEnabledAsync(true, scope, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkersMonitoringEnabledAll,
        "workers",
        "bulk",
        $"enabled:{result!.UpdatedCount}",
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(result);
});

panel.MapPost("/workers/disable-all", async (
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (result, error) = await workers.SetAllEnabledAsync(false, scope, ct);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkersMonitoringDisabledAll,
        "workers",
        "bulk",
        $"disabled:{result!.UpdatedCount}",
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    return Results.Ok(result);
});

panel.MapPost("/workers/{id:guid}/rotate-key", async (
    Guid id,
    WorkerAdminService workers,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (result, error) = await workers.RotateApiKeyAsync(id, scope, ct);
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

    return Results.Ok(result);
});

panel.MapDelete("/workers/{id:guid}", async (
    Guid id,
    WorkerAdminService workers,
    WorkerDiagnosticsService diagnostics,
    PanelAuditService audit,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (displayName, error) = await workers.DeleteAsync(id, scope, diagnostics, ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    await audit.LogAsync(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.FindFirstValue(ClaimTypes.Email),
        PanelAuditActions.WorkerDeleted,
        "worker",
        id.ToString(),
        displayName,
        http.Connection.RemoteIpAddress?.ToString(),
        ct);

    await GlobalLogger.Instance.LogAsync(
        $"Worker deleted ({id}, {displayName}).",
        DeskLinkAuditLogLevel.Warning);

    return Results.Ok(new { message = "Воркер удалён." });
});

panel.MapPost("/events/{id:guid}/dismiss", async (
    Guid id,
    WorkerEventService events,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (success, error) = await events.DismissAsync(id, scope, ct);
    if (!success)
    {
        return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(new { message = "Событие отмечено как обработанное." });
});

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
    OfficeBitrixIntegrationService officeBitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Unauthorized();
    }

    var integration = await officeBitrix.GetForScopeAsync(scope, ct);
    return integration is null
        ? Results.BadRequest(new { error = "Офис не назначен." })
        : Results.Ok(integration);
});

panel.MapGet("/office/integrations/bitrix", async (
    OfficeBitrixIntegrationService bitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var integration = await bitrix.GetForScopeAsync(scope, ct);
    return integration is null
        ? Results.BadRequest(new { error = "Офис не назначен." })
        : Results.Ok(integration);
});

panel.MapPut("/office/integrations/bitrix", async (
    SaveBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService bitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
    {
        return Results.Forbid();
    }

    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (integration, error) = await bitrix.SaveAsync(
        officeId,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        ct);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(integration);
});

panel.MapPost("/office/integrations/bitrix/validate", async (
    ValidateBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService bitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
    {
        return Results.Forbid();
    }

    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (validation, error) = await bitrix.ValidateAsync(
        officeId,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        persistResult: true,
        ct);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(validation);
});

panel.MapGet("/office/bitrix-settings", async (
    OfficeBitrixSettingsService bitrixSettings,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var settings = await bitrixSettings.GetForScopeAsync(scope, officeId, ct);
    return settings is null
        ? Results.BadRequest(new { error = "Офис не назначен." })
        : Results.Ok(settings);
});

panel.MapPut("/office/bitrix-settings", async (
    UpdateOfficeBitrixSettingsRequest request,
    OfficeBitrixSettingsService bitrixSettings,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (settings, error) = await bitrixSettings.UpdateForScopeAsync(
        scope,
        request.TransmissionEnabled,
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
        principal.FindFirstValue(ClaimTypes.Email),
        http.Connection.RemoteIpAddress?.ToString(),
        officeId,
        ct);

    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(settings);
});

var crm = app.MapGroup("/api/v1/crm").RequireAuthorization("Panel");
crm.MapGet("/board", async (
    Guid? officeId,
    string? search,
    string? scopeFilter,
    string? city,
    string? vacancy,
    bool overdueOnly,
    bool activeLoadOnly,
    bool includeClosed,
    CrmWorkspaceService workspace,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    var effectiveOfficeId = scope.ResolveFilter(officeId);
    var isAdmin = principal.IsInRole(PanelRoles.Admin);
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId)
        || (!isAdmin && !principal.IsInRole(PanelRoles.Manager)))
    {
        return Results.Forbid();
    }

    var board = await workspace.GetBoardAsync(
        resolvedOfficeId,
        userId,
        isAdmin,
        new CrmBoardQuery(search, scopeFilter, city, vacancy, overdueOnly, activeLoadOnly, includeClosed),
        ct);
    return board is null ? Results.NotFound() : Results.Ok(board);
});

crm.MapGet("/tasks", async (
    Guid? officeId,
    CrmWorkspaceService workspace,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    var effectiveOfficeId = scope.ResolveFilter(officeId);
    var isAdmin = principal.IsInRole(PanelRoles.Admin);
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId)
        || (!isAdmin && !principal.IsInRole(PanelRoles.Manager)))
    {
        return Results.Forbid();
    }

    return Results.Ok(await workspace.GetTasksAsync(resolvedOfficeId, userId, isAdmin, ct));
});

crm.MapPost("/shift/start", async (
    CrmWorkspaceService workspace,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
        || (!principal.IsInRole(PanelRoles.Manager) && !principal.IsInRole(PanelRoles.Admin)))
    {
        return Results.Forbid();
    }

    return await workspace.StartShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapPost("/shift/stop", async (
    CrmWorkspaceService workspace,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
        || (!principal.IsInRole(PanelRoles.Manager) && !principal.IsInRole(PanelRoles.Admin)))
    {
        return Results.Forbid();
    }

    return await workspace.StopShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapGet("/cards/{cardId:guid}", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    var card = await workspace.GetCardAsync(cardId, userId, principal.IsInRole(PanelRoles.Admin), ct);
    return card is null ? Results.NotFound() : Results.Ok(card);
});

crm.MapPost("/cards/{cardId:guid}/move", async (Guid cardId, CrmMoveRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    var (ok, error) = await workspace.MoveAsync(cardId, request.Stage, request.Comment, userId, principal.IsInRole(PanelRoles.Admin), ct);
    return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось сменить этап." });
});

crm.MapPost("/cards/{cardId:guid}/assign", async (Guid cardId, CrmAssignRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    return await workspace.AssignAsync(cardId, request.ManagerUserId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapPost("/cards/{cardId:guid}/active-load/{isInActiveLoad:bool}", async (Guid cardId, bool isInActiveLoad, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    return await workspace.SetActiveLoadAsync(cardId, isInActiveLoad, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapPost("/cards/{cardId:guid}/close", async (Guid cardId, CrmCloseRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    var (ok, error) = await workspace.CloseAsync(cardId, request.Reason, request.Comment, userId, principal.IsInRole(PanelRoles.Admin), ct);
    return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось закрыть карточку." });
});

crm.MapPost("/cards/{cardId:guid}/reopen", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    return await workspace.ReopenAsync(cardId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapPost("/cards/{cardId:guid}/notes", async (Guid cardId, CrmNoteCreateRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    return await workspace.AddNoteAsync(cardId, request.Text, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.BadRequest();
});

crm.MapPost("/cards/{cardId:guid}/follow-up", async (Guid cardId, CrmFollowUpRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    var task = await workspace.CreateFollowUpAsync(cardId, request.Minutes, request.Title, userId, principal.IsInRole(PanelRoles.Admin), ct);
    return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
});

crm.MapPost("/tasks", async (Guid? officeId, CrmTaskCreateRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    var effectiveOfficeId = scope.ResolveFilter(officeId);
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var isAdmin = principal.IsInRole(PanelRoles.Admin);
    if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId) || (!isAdmin && !principal.IsInRole(PanelRoles.Manager))) return Results.Forbid();
    var task = await workspace.CreateTaskAsync(resolvedOfficeId, request, userId, isAdmin, ct);
    return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
});

crm.MapPost("/tasks/{taskId:guid}/complete", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
    return await workspace.CompleteTaskAsync(taskId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
});

crm.MapGet("/offices/{officeId:guid}/settings", async (Guid officeId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
    principal.IsInRole(PanelRoles.Admin)
        ? Results.Ok(await workspace.GetOfficeSettingsAsync(officeId, ct))
        : Results.Forbid());

crm.MapPut("/offices/{officeId:guid}/settings", async (Guid officeId, CrmOfficeSettingsRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
    principal.IsInRole(PanelRoles.Admin)
        ? (await workspace.SetOfficeSettingsAsync(officeId, request.IsEnabled, request.RequireStageComment, ct) ? Results.NoContent() : Results.NotFound())
        : Results.Forbid());

crm.MapPut("/managers/{managerUserId}/capacity", async (string managerUserId, Guid? officeId, CrmCapacityRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
{
    if (!principal.IsInRole(PanelRoles.Admin)) return Results.Forbid();
    var scope = await officeScope.ResolveAsync(principal, ct);
    var effectiveOfficeId = scope.ResolveFilter(officeId);
    if (effectiveOfficeId is not Guid resolvedOfficeId) return Results.BadRequest();
    return await workspace.SetCapacityAsync(resolvedOfficeId, managerUserId, request.Capacity, ct) ? Results.NoContent() : Results.BadRequest();
});

panel.MapGet("/responses", async (
    ResponsesQueryService responses,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    string? status,
    string? search,
    string? vacancy,
    Guid? workerId,
    Guid? accountId,
    string? bitrixDestination,
    string? gender,
    int? ageFrom,
    int? ageTo,
    Guid? officeId,
    DateTime? from,
    DateTime? to,
    int? page,
    int? pageSize,
    string? sort,
    string? dir,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await responses.GetPageAsync(
        scope,
        officeId,
        status,
        search,
        vacancy,
        workerId,
        accountId,
        bitrixDestination,
        gender,
        ageFrom,
        ageTo,
        from,
        to,
        page ?? 1,
        pageSize ?? 10,
        sort,
        dir,
        ct));
});

panel.MapGet("/responses/summary", async (
    ResponsesQueryService responses,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    string? status,
    string? search,
    string? vacancy,
    Guid? workerId,
    Guid? accountId,
    string? bitrixDestination,
    string? gender,
    int? ageFrom,
    int? ageTo,
    Guid? officeId,
    DateTime? from,
    DateTime? to,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await responses.GetSummaryAsync(
        scope,
        officeId,
        status,
        search,
        vacancy,
        workerId,
        accountId,
        bitrixDestination,
        gender,
        ageFrom,
        ageTo,
        from,
        to,
        ct));
});

panel.MapGet("/statistics", async (
    OfficeStatisticsQueryService statistics,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    DateTime? from,
    DateTime? to,
    Guid[]? workerIds,
    Guid[]? accountIds,
    string? vacancy,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await statistics.GetStatisticsAsync(
        scope,
        officeId,
        from,
        to,
        workerIds,
        accountIds,
        vacancy,
        ct));
});

panel.MapGet("/responses/filters/accounts", async (
    ResponsesQueryService responses,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await responses.GetFilterAccountsAsync(scope, officeId, ct));
});

panel.MapGet("/responses/filters/vacancies", async (
    ResponsesQueryService responses,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    DateTime? from,
    DateTime? to,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await responses.GetFilterVacanciesAsync(scope, officeId, from, to, ct));
});

panel.MapGet("/responses/{id:guid}", async (
    Guid id,
    ResponsesQueryService responses,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var detail = await responses.GetDetailAsync(id, scope, ct);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

panel.MapPost("/responses/{id:guid}/resend-bitrix", async (
    Guid id,
    CandidateIngestionService ingestion,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await ingestion.ResendToBitrixAsync(id, scope, ct));
});

panel.MapGet("/bitrix-instances", async (
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await bitrixInstances.ListAsync(scope, officeId, ct));
});

panel.MapPost("/bitrix-instances", async (
    CreateBitrixInstanceRequest request,
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (instance, error) = await bitrixInstances.CreateAsync(scope, officeId, request, actorUserId, ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(instance);
});

panel.MapGet("/bitrix-instances/{id:guid}", async (
    Guid id,
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var instance = await bitrixInstances.GetAsync(id, scope, officeId, ct);
    return instance is null ? Results.NotFound() : Results.Ok(instance);
});

panel.MapPut("/bitrix-instances/{id:guid}", async (
    Guid id,
    UpdateBitrixInstanceRequest request,
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (instance, error) = await bitrixInstances.UpdateAsync(id, scope, officeId, request, actorUserId, ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(instance);
});

panel.MapDelete("/bitrix-instances/{id:guid}", async (
    Guid id,
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (success, error) = await bitrixInstances.DeleteAsync(id, scope, officeId, ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.NoContent();
});

panel.MapPost("/bitrix-instances/validate-webhook", async (
    ValidateBitrixInstanceRequest request,
    BitrixWebhookValidator validator,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var validation = await validator.ValidateAsync(request.WebhookUrl, ct);
    return Results.Ok(validation);
});

panel.MapPost("/bitrix-instances/{id:guid}/validate", async (
    Guid id,
    ValidateBitrixInstanceRequest request,
    BitrixInstanceService bitrixInstances,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (validation, error) = await bitrixInstances.ValidateAsync(
        id,
        scope,
        officeId,
        request.WebhookUrl,
        actorUserId,
        persistResult: true,
        ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(validation);
});

panel.MapGet("/distribution-route", async (
    DistributionRouteService distributionRoute,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var route = await distributionRoute.GetAsync(scope, officeId, ct);
    return route is null ? Results.BadRequest(new { error = "Офис не назначен." }) : Results.Ok(route);
});

panel.MapPut("/distribution-route", async (
    SaveDistributionRouteRequest request,
    DistributionRouteService distributionRoute,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    Guid? officeId,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (route, error) = await distributionRoute.SaveAsync(scope, officeId, request, actorUserId, ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(route);
});

panel.MapPost("/responses/{id:guid}/send-bitrix", async (
    Guid id,
    SendResponseToBitrixRequest request,
    ManualBitrixSendService manualSend,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    return Results.Ok(await manualSend.SendAsync(id, request.BitrixInstanceId, scope, ct));
});

panel.MapPost("/responses/send-bitrix-bulk", async (
    BulkSendResponsesToBitrixRequest request,
    BulkResponsesBitrixSendService bulkSend,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (result, error) = await bulkSend.SendAsync(request.ResponseIds, request.BitrixInstanceId, scope, ct);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(result);
});

panel.MapPut("/me/integrations/bitrix", async (
    SaveBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService officeBitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
    {
        return Results.Forbid();
    }

    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (integration, error) = await officeBitrix.SaveAsync(
        officeId,
        request.WebhookUrl,
        actorUserId,
        actor.Email,
        actor.IpAddress,
        ct);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(integration);
});

var workerPanel = app.MapGroup("/api/v1/workers").RequireAuthorization("Panel");
workerPanel.MapGet("/{id:guid}/config", async (
    Guid id,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var config = await configService.GetConfigForWorkerAsync(id, scope, ct);
    return config is null ? Results.NotFound() : Results.Ok(config);
});

workerPanel.MapPatch("/{id:guid}/settings", async (
    Guid id,
    UpdateWorkerSettingsRequest request,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (config, error) = await configService.UpdateSettingsAsync(id, request, scope, ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(config);
});

workerPanel.MapPost("/{id:guid}/commands", async (
    Guid id,
    WorkerCommandRequest request,
    WorkerCommandService commands,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (success, error) = await commands.EnqueueAsync(id, request.Command, scope, ct);
    if (!success)
    {
        return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(new { message = "Команда поставлена в очередь." });
});

workerPanel.MapPatch("/{id:guid}/accounts/{accountId:guid}", async (
    Guid id,
    Guid accountId,
    UpdateWorkerAccountRequest request,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (account, error) = await configService.UpdateAccountEnabledAsync(id, accountId, request, scope, ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(account);
});

workerPanel.MapPut("/{id:guid}/accounts/{accountId:guid}/credentials", async (
    Guid id,
    Guid accountId,
    UpdateWorkerAccountCredentialsRequest request,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (credentials, error) = await configService.UpdateAccountCredentialsAsync(
        id, accountId, request, scope, ct);
    if (error is not null)
    {
        return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(credentials);
});

workerPanel.MapPost("/{id:guid}/accounts/{accountId:guid}/refresh-subprofiles", async (
    Guid id,
    Guid accountId,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (success, error) = await configService.RequestSubProfilesRefreshAsync(id, accountId, scope, ct);
    if (!success)
    {
        return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(new { message = "Запрос на обновление субпрофилей отправлен воркеру." });
});

workerPanel.MapPatch("/{id:guid}/accounts/{accountId:guid}/subprofiles/{subProfileId}", async (
    Guid id,
    Guid accountId,
    string subProfileId,
    UpdateWorkerSubProfileRequest request,
    WorkerConfigService configService,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess)
    {
        return Results.Forbid();
    }

    var (success, error) = await configService.UpdateSubProfileEnabledAsync(
        id, accountId, subProfileId, request, scope, ct);
    if (!success)
    {
        return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound(new { error })
            : Results.BadRequest(new { error });
    }

    return Results.Ok(new { message = request.IsEnabledInPanel ? "Субпрофиль включён." : "Субпрофиль отключён." });
});

panel.MapPost("/me/integrations/bitrix/validate", async (
    ValidateBitrixIntegrationRequest request,
    OfficeBitrixIntegrationService officeBitrix,
    OfficeScopeService officeScope,
    ClaimsPrincipal principal,
    HttpContext http,
    CancellationToken ct) =>
{
    var scope = await officeScope.ResolveAsync(principal, ct);
    if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
    {
        return Results.Forbid();
    }

    var actor = GetActor(principal, http);
    var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var (validation, error) = await officeBitrix.ValidateAsync(
        officeId,
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

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    PanelAuditService audit,
    PanelUserService panelUsers,
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
    var isAdmin = roles.Contains(PanelRoles.Admin, StringComparer.OrdinalIgnoreCase);
    Guid? officeId = null;
    if (!isAdmin)
    {
        officeId = await panelUsers.GetOfficeIdForUserAsync(user.Id, ct);
        if (officeId is null)
        {
            await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "office_not_assigned", ip, ct);
            return Results.Json(new { error = "Оператору не назначен офис. Обратитесь к администратору." }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    var token = JwtTokenFactory.CreateToken(user, roles, config, officeId);
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
}

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, string Email);

static class JwtTokenFactory
{
    public static string CreateToken(
        IdentityUser user,
        IEnumerable<string> roles,
        IConfiguration config,
        Guid? officeId = null)
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

        if (officeId is Guid resolvedOfficeId)
        {
            claims.Add(new Claim(OfficeClaims.OfficeId, resolvedOfficeId.ToString()));
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
