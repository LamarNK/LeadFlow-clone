using Microsoft.AspNetCore.Authentication.Cookies;
using Orbita.Logging.Audit;
using Orbita.Web.Middleware;
using Orbita.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrbitaLogging("Orbita.Web");

builder.Services.AddHttpContextAccessor();
builder.Services.AddControllersWithViews();

builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<OrbitaAuthService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IWorkersService, WorkersService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IAccountsService, AccountsService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
    });
builder.Services.AddAuthorization();

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
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();