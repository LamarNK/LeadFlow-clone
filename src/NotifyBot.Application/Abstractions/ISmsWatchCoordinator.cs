using NotifyBot.Application.Models;

namespace NotifyBot.Application.Abstractions;

public interface ISmsWatchCoordinator
{
    SmsWatchRegistration TryRegister(long chatId);

    IReadOnlyList<SmsWatchSessionQuery> GetActiveQueries();

    void MarkDelivered(long chatId, string dedupeKey);

    IReadOnlyList<long> TakeExpiredChatIds();
}