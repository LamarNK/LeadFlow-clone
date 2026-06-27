using Microsoft.Extensions.Logging;
using NotifyBot.Application.Abstractions;
using Telegram.Bot;

namespace NotifyBot.Infrastructure.Telegram;

public sealed class TelegramService(
    ITelegramBotClient botClient,
    IAdminUserRepository adminUserRepository,
    ILogger<TelegramService> logger) : ITelegramService
{
    public async Task SendMessageAsync(long chatId, string message, CancellationToken cancellationToken = default)
    {
        await botClient.SendMessage(chatId, message, cancellationToken: cancellationToken);
        logger.LogInformation("Telegram message sent to chat {ChatId}", chatId);
    }

    public async Task SendAdminMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var admins = await adminUserRepository.GetActiveAdminsAsync(cancellationToken);
        if (admins.Count == 0)
        {
            logger.LogWarning("No registered admin users found for admin notification");
            return;
        }

        foreach (var admin in admins)
        {
            try
            {
                await botClient.SendMessage(admin.TelegramUserId, message, cancellationToken: cancellationToken);
                logger.LogInformation("Admin notification sent to {UserId} (@{Username})", admin.TelegramUserId, admin.Username);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send admin notification to {UserId}", admin.TelegramUserId);
            }
        }
    }
}