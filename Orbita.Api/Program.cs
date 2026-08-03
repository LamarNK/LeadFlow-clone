using Orbita.Api.Endpoints;
using Orbita.Api.Hubs;
using Orbita.Logging.Audit;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureOrbitaApi();

var app = builder.Build();
app.UseOrbitaLogging();
app.UseForwardedHeaders();

await app.SeedAsync();

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
app.MapOrbitaEndpoints();
app.Run();
