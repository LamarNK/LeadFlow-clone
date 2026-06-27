using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramWebhookSetupService(
    ITelegramBotClient botClient,
    IOptions<TelegramOptions> options,
    ILogger<TelegramWebhookSetupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var webhookUrl = options.Value.WebhookUrl;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            logger.LogWarning("Telegram webhook URL is not configured. Admin commands require manual webhook setup.");
            return;
        }

        await botClient.SetWebhook(
            webhookUrl,
            allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember],
            cancellationToken: cancellationToken);
        logger.LogInformation("Telegram webhook configured: {WebhookUrl}", webhookUrl);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}