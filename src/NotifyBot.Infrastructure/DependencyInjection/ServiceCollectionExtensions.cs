using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Application.Services;
using NotifyBot.Infrastructure.Data;
using NotifyBot.Infrastructure.Notifications;
using NotifyBot.Infrastructure.Parsing;
using NotifyBot.Infrastructure.Telegram;
using Telegram.Bot;

namespace NotifyBot.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotifyBotInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.Configure<PlusofonOptions>(configuration.GetSection(PlusofonOptions.SectionName));

        services.AddDbContext<NotifyBotDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var telegramOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>().Value;
            return new TelegramBotClient(telegramOptions.BotToken);
        });

        services.AddScoped<ISmsParser, SmsParser>();
        services.AddScoped<ICardRepository, CardRepository>();
        services.AddScoped<ITelegramChatRepository, TelegramChatRepository>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<ITelegramService, TelegramService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<ISmsRoutingService, SmsRoutingService>();
        services.AddSingleton<AdminSessionStore>();
        services.AddScoped<TelegramAdminPanel>();
        services.AddScoped<ITelegramUpdateHandler, TelegramUpdateHandler>();
        services.AddHostedService<TelegramWebhookSetupService>();

        return services;
    }
}