using Microsoft.EntityFrameworkCore;
using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Entities;

namespace NotifyBot.Infrastructure.Data;

public sealed class CardRepository(NotifyBotDbContext dbContext) : ICardRepository
{
    public Task<Card?> GetByLast4Async(string last4, CancellationToken cancellationToken = default) =>
        dbContext.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Last4 == last4, cancellationToken);

    public async Task<IReadOnlyList<Card>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Cards.AsNoTracking().OrderBy(x => x.Last4).ToListAsync(cancellationToken);

    public async Task UpsertAsync(Card card, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Cards.FirstOrDefaultAsync(x => x.Last4 == card.Last4, cancellationToken);
        if (existing is null)
        {
            dbContext.Cards.Add(card);
        }
        else
        {
            existing.Label = card.Label;
            existing.Enabled = card.Enabled;
            existing.DestinationChatId = card.DestinationChatId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetDestinationAsync(string last4, long? destinationChatId, CancellationToken cancellationToken = default)
    {
        var card = await dbContext.Cards.FirstOrDefaultAsync(x => x.Last4 == last4, cancellationToken);
        if (card is null)
        {
            return false;
        }

        card.DestinationChatId = destinationChatId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string last4, CancellationToken cancellationToken = default)
    {
        var card = await dbContext.Cards.FirstOrDefaultAsync(x => x.Last4 == last4, cancellationToken);
        if (card is null)
        {
            return false;
        }

        dbContext.Cards.Remove(card);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}