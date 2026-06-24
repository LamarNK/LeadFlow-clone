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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrbitaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
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
    options.AddPolicy("Admin", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
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

var app = builder.Build();

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
    return result is null ? Results.Unauthorized() : Results.Ok(result);
});

workers.MapPost("/heartbeat", async (WorkerHeartbeatRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        return Results.Forbid();
    }

    return await telemetry.HeartbeatAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/telemetry/snapshot", async (WorkerSnapshotRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        return Results.Forbid();
    }

    return await telemetry.SaveSnapshotAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

workers.MapPost("/telemetry/events", async (WorkerEventBatchRequest request, TelemetryService telemetry, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!TryGetWorkerId(user, out var workerId) || workerId != request.WorkerId)
    {
        return Results.Forbid();
    }

    return await telemetry.SaveEventsAsync(request, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization("Worker");

var dashboard = app.MapGroup("/api/v1/dashboard").RequireAuthorization("Admin");
dashboard.MapGet("/summary", async (DashboardQueryService query, CancellationToken ct) =>
    Results.Ok(await query.GetGlobalSummaryAsync(ct)));

var workerRead = app.MapGroup("/api/v1").RequireAuthorization("Admin");
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

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    IConfiguration config) =>
{
    var user = await users.FindByEmailAsync(request.Email);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
    if (!result.Succeeded)
    {
        return Results.Unauthorized();
    }

    var token = JwtTokenFactory.CreateToken(user, config);
    return Results.Ok(new LoginResponse(token, user.Email ?? request.Email));
});

app.Run();

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
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var email = config["Admin:Email"] ?? "admin@orbita.local";
    var password = config["Admin:Password"] ?? "OrbitaAdmin1!";

    if (await users.FindByEmailAsync(email) is null)
    {
        await users.CreateAsync(new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        }, password);
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, string Email);

static class JwtTokenFactory
{
    public static string CreateToken(IdentityUser user, IConfiguration config)
    {
        var key = config["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
        var issuer = config["Jwt:Issuer"] ?? "Orbita";
        var audience = config["Jwt:Audience"] ?? "Orbita.Web";
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty)
        };

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}