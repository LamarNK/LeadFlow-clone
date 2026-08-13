namespace LeadFlow.Core.Services.AdsPower;

/// <summary>Когда известную карточку на странице откликов можно не открывать.</summary>
public static class MessengerEnrichmentSkip
{
    public static bool ShouldSkipKnownCandidate(
        bool isKnownSourceId,
        bool hasUnread,
        bool openPhoneWatch,
        bool hasPendingOutbound) =>
        isKnownSourceId && !hasUnread && !openPhoneWatch && !hasPendingOutbound;
}