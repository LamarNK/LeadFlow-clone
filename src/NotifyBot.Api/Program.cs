using NotifyBot.Api.Middleware;
using NotifyBot.Infrastructure.Data;
using NotifyBot.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Development.Local.json", optional: true, reloadOnChange: true);
}

builder.Services.AddControllers();
builder.Services.AddNotifyBotInfrastructure(builder.Configuration);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<NotifyBotDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("NotifyBot.Startup");
    await DatabaseInitializer.MigrateAndSeedAsync(dbContext, logger);
}

app.UseMiddleware<PlusofonWebhookAuthMiddleware>();
app.MapControllers();

app.Run();