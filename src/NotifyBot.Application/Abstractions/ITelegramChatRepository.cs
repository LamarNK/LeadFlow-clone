using NotifyBot.Domain.Entities;

namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Stores Telegram chats where the bot can deliver messages.
/// </summary>
public interface ITelegramChatRepository
{
    Task<TelegramChat?> GetByChatIdAsync(long chatId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelegramChat>> GetActiveChatsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(TelegramChat chat, CancellationToken cancellationToken = default);

    Task<bool> DeactivateAsync(long chatId, CancellationToken cancellationToken = default);
}