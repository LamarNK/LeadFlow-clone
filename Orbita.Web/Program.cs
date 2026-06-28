using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Logging.Audit;
using Orbita.Web.Authorization;
using Orbita.Web.Middleware;
using Orbita.Web.Options;
using Orbita.Web.Services;

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
builder.Services.AddControllersWithViews();

builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<OrbitaAuthService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IWorkersService, WorkersService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IResponsesService, ResponsesService>();
builder.Services.AddScoped<IErrorsService, ErrorsService>();
builder.Services.AddScoped<IAccountsService, AccountsService>();
builder.Services.AddScoped<NavBadgesService>();
builder.Services.AddScoped<GlobalSearchService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IMySettingsService, MySettingsService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Dashboard";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(OrbitaRoles.Admin, policy => policy.RequireRole(OrbitaRoles.Admin));
});

builder.Services.AddHttpClient<OrbitaApiClient>(client =>
{
    var baseUrl = builder.Configuration["OrbitaApi:BaseUrl"] ?? "https://localhost:7291";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(30);
});

var app = builder.Build();
app.UseOrbitaLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        AllowStatusCode404Response = true,
        ExceptionHandler = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Внутренняя ошибка сервера.");
        }
    });
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<ThemeMiddleware>();
app.UseAuthentication();
app.UseMiddleware<JwtCookieAuthenticationMiddleware>();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();