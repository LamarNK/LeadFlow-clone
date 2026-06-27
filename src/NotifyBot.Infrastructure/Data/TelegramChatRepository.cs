using Microsoft.EntityFrameworkCore;
using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Entities;

namespace NotifyBot.Infrastructure.Data;

public sealed class TelegramChatRepository(NotifyBotDbContext dbContext) : ITelegramChatRepository
{
    public Task<TelegramChat?> GetByChatIdAsync(long chatId, CancellationToken cancellationToken = default) =>
        dbContext.TelegramChats.AsNoTracking().FirstOrDefaultAsync(x => x.ChatId == chatId, cancellationToken);

    public async Task<IReadOnlyList<TelegramChat>> GetActiveChatsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.TelegramChats
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ThenBy(x => x.Title)
            .ToListAsync(cancellationToken);

    public async Task UpsertAsync(TelegramChat chat, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.TelegramChats.FirstOrDefaultAsync(x => x.ChatId == chat.ChatId, cancellationToken);
        if (existing is null)
        {
            dbContext.TelegramChats.Add(chat);
        }
        else
        {
            existing.ChatType = chat.ChatType;
            existing.Title = chat.Title;
            existing.Username = chat.Username;
            existing.IsActive = chat.IsActive;
            existing.LastSeenAtUtc = chat.LastSeenAtUtc ?? existing.LastSeenAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeactivateAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var chat = await dbContext.TelegramChats.FirstOrDefaultAsync(x => x.ChatId == chatId, cancellationToken);
        if (chat is null)
        {
            return false;
        }

        chat.IsActive = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}