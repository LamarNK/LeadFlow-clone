namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Sends messages through the Telegram bot.
/// </summary>
public interface ITelegramService
{
    /// <summary>
    /// Sends a message to the specified Telegram chat (group or private).
    /// </summary>
    Task SendMessageAsync(long chatId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to all configured admin users in private chats.
    /// </summary>
    Task SendAdminMessageAsync(string message, CancellationToken cancellationToken = default);
}