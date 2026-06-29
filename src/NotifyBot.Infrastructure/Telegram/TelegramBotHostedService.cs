using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramBotHostedService(
    ITelegramBotClient botClient,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramBotHostedService> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterCommandsAsync(stoppingToken);

        if (!string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            await botClient.SetWebhook(
                _options.WebhookUrl,
                allowedUpdates: AllowedUpdateTypes,
                cancellationToken: stoppingToken);
            logger.LogInformation("Telegram webhook configured: {WebhookUrl}", _options.WebhookUrl);
            return;
        }

        await botClient.DeleteWebhook(dropPendingUpdates: false, cancellationToken: stoppingToken);
        logger.LogInformation("Telegram polling mode enabled (public URL not required)");

        var receiverOptions = new ReceiverOptions { AllowedUpdates = AllowedUpdateTypes };
        botClient.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions,
            stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();
        await handler.HandleAsync(update, cancellationToken);
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Telegram polling error");
        return Task.CompletedTask;
    }

    private async Task RegisterCommandsAsync(CancellationToken cancellationToken)
    {
        await botClient.SetMyCommands(
            [
                new BotCommand { Command = "sms", Description = "Получить свежий 3DS-код из SMS" },
                new BotCommand { Command = "check", Description = "Проверить SMS (то же, что /sms)" },
                new BotCommand { Command = "start", Description = "Панель администратора" }
            ],
            cancellationToken: cancellationToken);
    }

    private static readonly UpdateType[] AllowedUpdateTypes =
        [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember];
}