using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class ProfileStatsUpdatedEventArgs : EventArgs
{
    public required AvitoAccount Account { get; init; }
    public int PreviousBlockedCount { get; init; }
    public int PreviousDraftsCount { get; init; }
    public int PreviousActiveAdsCount { get; init; }

    public int BlockedCountDelta => Account.BlockedCount - PreviousBlockedCount;
    public bool HasNewBlockedAds => BlockedCountDelta > 0;
}
