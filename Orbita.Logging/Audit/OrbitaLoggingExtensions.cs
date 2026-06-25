using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orbita.Logging.Audit;

public static class OrbitaLoggingExtensions
{
    private static ILoggerFactory? _consoleLoggerFactory;

    public static WebApplicationBuilder AddOrbitaLogging(this WebApplicationBuilder builder, string serviceName)
    {
        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", serviceName);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddGlobalLoggerProvider();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        return builder;
    }

    public static WebApplication UseOrbitaLogging(this WebApplication app)
    {
        // Только Console — иначе GlobalLogger → ILogger → GlobalLoggerProvider → бесконечная рекурсия.
        _consoleLoggerFactory ??= LoggerFactory.Create(static builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        GlobalLogger.ConfigureAppLogger(_consoleLoggerFactory.CreateLogger("Orbita"));

        app.UseMiddleware<CorrelationIdMiddleware>();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var service = Environment.GetEnvironmentVariable("LOG_SERVICE_NAME") ?? "Orbita";
            GlobalLogger.Instance
                .LogAsync($"{service} started.", DeskLinkAuditLogLevel.Info)
                .GetAwaiter()
                .GetResult();
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var service = Environment.GetEnvironmentVariable("LOG_SERVICE_NAME") ?? "Orbita";
            GlobalLogger.Instance
                .LogAsync($"{service} stopping.", DeskLinkAuditLogLevel.Info)
                .GetAwaiter()
                .GetResult();
        });

        return app;
    }
}