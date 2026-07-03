namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Данные сайдбара Avito Pro: балансы (<c>osp-sidebar/tools/money</c>) и рейтинг (<c>osp-sidebar/tools/stats/rating</c>).
/// </summary>
public sealed record AvitoMoneySidebar(
    decimal? WalletBalance,
    decimal? AdvanceBalance,
    string? AdvanceDurationText,
    decimal? Rating = null,
    int? ReviewsCount = null,
    string? ReviewsText = null)
{
    public bool HasAnyBalance => WalletBalance.HasValue || AdvanceBalance.HasValue;

    public bool HasAnyData => HasAnyBalance || Rating.HasValue;

    public void ApplyTo(Models.AvitoSubProfile sub)
    {
        if (WalletBalance.HasValue)
        {
            sub.WalletBalance = WalletBalance;
        }

        if (AdvanceBalance.HasValue)
        {
            sub.Balance = AdvanceBalance;
        }

        if (!string.IsNullOrWhiteSpace(AdvanceDurationText))
        {
            sub.AdvanceDurationText = AdvanceDurationText;
        }

        if (Rating.HasValue)
        {
            sub.Rating = Rating;
        }

        if (ReviewsCount.HasValue)
        {
            sub.ReviewsCount = ReviewsCount;
        }

        if (!string.IsNullOrWhiteSpace(ReviewsText))
        {
            sub.ReviewsText = ReviewsText;
        }
    }

    public void ApplyTo(ProfileResult part)
    {
        if (WalletBalance.HasValue)
        {
            part.WalletBalance = WalletBalance;
        }

        if (AdvanceBalance.HasValue)
        {
            part.Balance = AdvanceBalance;
        }

        if (!string.IsNullOrWhiteSpace(AdvanceDurationText))
        {
            part.AdvanceDurationText = AdvanceDurationText;
        }

        if (Rating.HasValue)
        {
            part.Rating = Rating;
        }

        if (ReviewsCount.HasValue)
        {
            part.ReviewsCount = ReviewsCount;
        }

        if (!string.IsNullOrWhiteSpace(ReviewsText))
        {
            part.ReviewsText = ReviewsText;
        }
    }
}