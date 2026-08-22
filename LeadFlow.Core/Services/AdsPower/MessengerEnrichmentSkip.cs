namespace LeadFlow.Core.Services.AdsPower;

/// <summary>Когда карточку на странице откликов можно не открывать.</summary>
public static class MessengerEnrichmentSkip
{
    public static bool ShouldSkipKnownCandidate(
        bool isKnownSourceId,
        bool hasUnread,
        bool openPhoneWatch,
        bool hasPendingOutbound) =>
        ShouldSkipOpeningCard(
            isKnownSourceId,
            hasUnread,
            openPhoneWatch,
            hasPendingOutbound,
            hasCompletePhone: true);

    /// <summary>
    /// Не открываем чат: известные без unread, неизвестные без полного номера.
    /// Phone-watch и исходящие менеджера — всегда открываем.
    /// </summary>
    public static bool ShouldSkipOpeningCard(
        bool isKnownSourceId,
        bool hasUnread,
        bool openPhoneWatch,
        bool hasPendingOutbound,
        bool hasCompletePhone)
    {
        if (openPhoneWatch || hasPendingOutbound)
        {
            return false;
        }

        if (isKnownSourceId)
        {
            return !hasUnread;
        }

        return !hasCompletePhone;
    }
}