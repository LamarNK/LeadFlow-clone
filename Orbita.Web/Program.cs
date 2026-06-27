using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Orbita.Logging.Audit;
using Orbita.Web.Authorization;
using Orbita.Web.Middleware;
using Orbita.Web.Options;
using Orbita.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrbitaLogging("Orbita.Web");

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
builder.Services.AddScoped<IErrorsService, ErrorsService>();
builder.Services.AddScoped<IAccountsService, AccountsService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();

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
});

var app = builder.Build();
app.UseOrbitaLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
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