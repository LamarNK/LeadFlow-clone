using NotifyBot.Domain.Entities;

namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Provides access to card-to-destination mappings stored in the database.
/// </summary>
public interface ICardRepository
{
    /// <summary>
    /// Finds a card by its last four digits.
    /// </summary>
    Task<Card?> GetByLast4Async(string last4, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all configured cards.
    /// </summary>
    Task<IReadOnlyList<Card>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a card record.
    /// </summary>
    Task UpsertAsync(Card card, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns a Telegram destination to the card.
    /// </summary>
    Task<bool> SetDestinationAsync(string last4, long? destinationChatId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string last4, CancellationToken cancellationToken = default);
}