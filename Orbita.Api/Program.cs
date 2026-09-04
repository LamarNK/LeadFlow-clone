using Orbita.Api.Endpoints;
using Orbita.Api.Hubs;
using Orbita.Logging.Audit;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureOrbitaApi();

var app = builder.Build();
app.UseOrbitaLogging();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    // Server-side DTO caching must never make a panel response cacheable by a
    // browser or an intermediary. Files and streaming endpoints stay uncached too.
    context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    await next();
});

await app.SeedAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Web");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHub<PanelHub>("/hubs/panel");
app.MapHub<CaptchaRelayHub>("/hubs/captcha");
app.MapHub<BrowserMonitorHub>("/hubs/browser-monitor");
app.MapHub<WorkerHub>("/hubs/worker");
app.MapOrbitaEndpoints();
app.Run();
