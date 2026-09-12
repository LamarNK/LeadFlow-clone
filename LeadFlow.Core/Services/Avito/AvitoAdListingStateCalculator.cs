using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoAdListingStateCalculator
{
    public const int ApproachingDays = 7;

    public static string Compute(AvitoAdListingRecord record, DateTime utcNow)
    {
        if (!record.IsActive)
        {
            return AvitoAdListingStates.NotActive;
        }

        if (record.PublishedAtUtc is null
            || !string.Equals(record.PublicationDateSource, AvitoAdPublicationDateSources.Exact, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(record.LastParseError)
                ? AvitoAdListingStates.UnknownPublicationDate
                : AvitoAdListingStates.ParseFailed;
        }

        var expiresAt = record.ExpiresAtUtc
                        ?? AvitoAdExpiryCalculator.ComputeExpiresAtUtc(record.PublishedAtUtc.Value);
        var remaining = AvitoAdExpiryCalculator.RemainingControlDays(expiresAt, utcNow);
        if (remaining < 0)
        {
            return AvitoAdListingStates.Expired;
        }

        if (remaining == 0)
        {
            return AvitoAdListingStates.ExpiresToday;
        }

        if (remaining <= ApproachingDays)
        {
            return AvitoAdListingStates.ApproachingExpiry;
        }

        return AvitoAdListingStates.Active;
    }

    public static void Refresh(AvitoAdListingRecord record, DateTime utcNow)
    {
        if (record.PublishedAtUtc is DateTime published
            && string.Equals(record.PublicationDateSource, AvitoAdPublicationDateSources.Exact, StringComparison.Ordinal))
        {
            record.ExpiresAtUtc = AvitoAdExpiryCalculator.ComputeExpiresAtUtc(published);
            record.AgeDays = AvitoAdExpiryCalculator.CalendarAgeDays(published, utcNow);
        }

        record.State = Compute(record, utcNow);
        record.UpdatedAtUtc = utcNow;
    }
}
