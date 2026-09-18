using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using Orbita.Web.Authorization;
using Orbita.Web.Middleware;
using Orbita.Web.Options;
using Orbita.Web.Services;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrbitaLogging("Orbita.Web");

const long defaultMaxUploadBytes = 536_870_912;
var maxUploadBytes = builder.Configuration.GetValue<long?>("WorkerReleases:MaxUploadBytes") ?? defaultMaxUploadBytes;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("Orbita.Web");
}

builder.Services.Configure<DesignPreviewOptions>(
    builder.Configuration.GetSection(DesignPreviewOptions.SectionName));

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddControllersWithViews();

builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<OrbitaAuthService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IWorkersService, WorkersService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IResponsesService, ResponsesService>();
builder.Services.AddScoped<IErrorsService, ErrorsService>();
builder.Services.AddScoped<IOfficeContext, OfficeContext>();
builder.Services.AddScoped<IAccountsService, AccountsService>();
builder.Services.AddScoped<IWorkerScheduleWebService, WorkerScheduleWebService>();
builder.Services.AddScoped<IBalancesService, BalancesService>();
builder.Services.AddScoped<IListingsService, ListingsService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<NavBadgesService>();
builder.Services.AddScoped<GlobalSearchService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IMySettingsService, MySettingsService>();
builder.Services.AddSingleton<BitrixValidationResultCache>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(OrbitaRoles.Admin, policy =>
        policy.RequireClaim(PanelPermissions.ClaimType, PanelPermissions.Administration));
    options.AddPolicy("OfficeStaff", policy =>
        policy.RequireRole(PanelRoles.Admin, PanelRoles.OfficeLead));
    foreach (var permission in PanelPermissions.All)
    {
        options.AddPolicy(permission.Id, policy =>
        {
            if (permission.Id is PanelPermissions.Balances && !OrbitaFeatureToggles.BalancesEnabled
                || permission.Id is PanelPermissions.Listings && !OrbitaFeatureToggles.ListingsEnabled)
            {
                policy.RequireAssertion(_ => false);
            }
            else if (permission.Id is PanelPermissions.Balances or PanelPermissions.Listings)
            {
                policy.RequireRole(PanelRoles.Admin, PanelRoles.Operator);
            }
            else if (permission.Id is PanelPermissions.CrmBoard or PanelPermissions.CrmTasks or PanelPermissions.CrmAnalytics)
            {
                policy.RequireAssertion(context =>
                    context.User.HasClaim(PanelPermissions.ClaimType, permission.Id)
                    || context.User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)
                    || (permission.Id is PanelPermissions.CrmBoard or PanelPermissions.CrmTasks
                        && context.User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam)));
            }
            else
            {
                policy.RequireClaim(PanelPermissions.ClaimType, permission.Id);
            }
        });
    }
});

var orbitaApiBaseUrl = builder.Configuration["OrbitaApi:BaseUrl"] ?? "https://localhost:7291";
var orbitaApiUri = new Uri(orbitaApiBaseUrl.TrimEnd('/') + "/");

builder.Services.AddHttpClient<OrbitaApiClient>(client =>
{
    client.BaseAddress = orbitaApiUri;
    client.Timeout = TimeSpan.FromMinutes(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // A system proxy can intercept loopback calls on Windows and return 503
    // before the request reaches the local Orbita API.
    UseProxy = !orbitaApiUri.IsLoopback
});

var apiProxyBase = orbitaApiUri.ToString();
builder.Services.AddReverseProxy()
    .LoadFromMemory(
        [
            new RouteConfig
            {
                RouteId = "orbita_signalr",
                ClusterId = "orbita_api",
                Match = new RouteMatch { Path = "/hubs/{**catch-all}" }
            }
        ],
        [
            new ClusterConfig
            {
                ClusterId = "orbita_api",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["api"] = new() { Address = apiProxyBase }
                }
            }
        ]);

// Build the web app only after all services and middleware dependencies are configured.
var app = builder.Build();
app.UseOrbitaLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();
app.UseWhen(
    context => HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase),
    browser => browser.UseStatusCodePagesWithReExecute("/error/{0}"));
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // asp-append-version adds ?v=... so hashed assets can be cached for a year.
        ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Query.ContainsKey("v")
            ? "public,max-age=31536000,immutable"
            : "public,max-age=86400";
    }
});
app.UseMiddleware<Orbita.Web.Middleware.DynamicResponseCacheHeadersMiddleware>();
app.UseWebSockets();
app.UseRouting();
app.UseMiddleware<ThemeMiddleware>();
app.UseAuthentication();
app.UseMiddleware<JwtCookieAuthenticationMiddleware>();
app.UseMiddleware<OfficeContextMiddleware>();
app.UseAuthorization();

app.MapReverseProxy();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();
