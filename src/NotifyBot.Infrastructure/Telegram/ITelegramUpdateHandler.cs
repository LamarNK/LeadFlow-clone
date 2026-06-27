using Telegram.Bot.Types;

namespace NotifyBot.Infrastructure.Telegram;

/// <summary>
/// Processes incoming Telegram bot updates.
/// </summary>
public interface ITelegramUpdateHandler
{
    /// <summary>
    /// Handles a single Telegram update.
    /// </summary>
    Task HandleAsync(Update update, CancellationToken cancellationToken = default);
}