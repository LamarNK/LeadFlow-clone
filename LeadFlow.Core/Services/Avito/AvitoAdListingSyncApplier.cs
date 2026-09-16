using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoAdListingSyncApplier
{
    public static IReadOnlyList<AvitoAdListingRecord> ApplyListSnapshot(
        IReadOnlyList<AvitoAdListingRecord> existing,
        Guid workerId,
        Guid accountId,
        string avitoSubProfileId,
        IReadOnlyList<AvitoAdListCard> cards,
        DateTime utcNow,
        bool listComplete)
    {
        var subId = avitoSubProfileId ?? string.Empty;
        var byId = existing
            .Where(x => x.WorkerId == workerId
                        && x.AccountId == accountId
                        && string.Equals(x.AvitoSubProfileId, subId, StringComparison.Ordinal))
            .ToDictionary(x => x.AvitoItemId, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AvitoAdListingRecord>();

        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.AvitoItemId) || !seen.Add(card.AvitoItemId))
            {
                continue;
            }

            if (!byId.TryGetValue(card.AvitoItemId, out var record))
            {
                record = new AvitoAdListingRecord
                {
                    Id = Guid.NewGuid(),
                    WorkerId = workerId,
                    AccountId = accountId,
                    AvitoSubProfileId = subId,
                    AvitoItemId = card.AvitoItemId,
                    CreatedAtUtc = utcNow,
                    PublicationDateSource = AvitoAdPublicationDateSources.Unknown,
                    State = AvitoAdListingStates.UnknownPublicationDate
                };
            }

            record.Title = string.IsNullOrWhiteSpace(card.Title) ? record.Title : card.Title;
            if (!string.IsNullOrWhiteSpace(card.Url))
            {
                record.Url = card.Url;
                if (string.Equals(record.LastParseError, "missing_view_link", StringComparison.Ordinal)
                    || string.Equals(record.LastParseError, "empty_view_link_href", StringComparison.Ordinal)
                    || string.Equals(record.LastParseError, "invalid_view_link_href", StringComparison.Ordinal))
                {
                    record.LastParseError = null;
                }
            }
            else if (string.IsNullOrWhiteSpace(record.Url))
            {
                record.LastParseError = card.UrlParseError ?? "missing_view_link";
            }

            record.StatusText = card.StatusText ?? string.Empty;
            record.SourceTab = string.IsNullOrWhiteSpace(card.SourceTab) ? AvitoAdStatus.ActiveTab : card.SourceTab;
            record.ErrorReason = card.ErrorReason ?? string.Empty;
            record.CanPublish = card.CanPublish;
            record.ImageUrl = card.ImageUrl ?? string.Empty;
            record.Salary = card.Salary ?? string.Empty;
            record.City = card.City ?? string.Empty;
            record.AddressText = card.AddressText ?? string.Empty;
            record.DistrictText = card.DistrictText ?? string.Empty;
            record.Views = card.Views;
            record.Contacts = card.Contacts;
            record.Favorites = card.Favorites;
            if (card.AgeDays is int age)
            {
                record.AgeDays = age;
            }

            if (card.ExpiresAtUtc is DateTime expiresAtUtc)
            {
                record.ExpiresAtUtc = expiresAtUtc;
                record.RemainingDays = card.RemainingDays;
                record.PublicationDateSource = AvitoAdPublicationDateSources.ListExpiry;
                record.LastParseError = null;
            }
            else if (!string.IsNullOrWhiteSpace(card.ExpiryParseError))
            {
                record.LastParseError = card.ExpiryParseError;
            }

            record.IsActive = string.Equals(record.SourceTab, AvitoAdStatus.ActiveTab, StringComparison.Ordinal);
            record.LastSeenAtUtc = utcNow;
            if (listComplete)
            {
                record.LastSuccessfulListCheckAtUtc = utcNow;
            }

            AvitoAdListingStateCalculator.Refresh(record, utcNow);
            result.Add(record);
            byId.Remove(card.AvitoItemId);
        }

        foreach (var leftover in byId.Values)
        {
            if (listComplete)
            {
                leftover.IsActive = false;
                leftover.LastSuccessfulListCheckAtUtc = utcNow;
                AvitoAdListingStateCalculator.Refresh(leftover, utcNow);
            }

            result.Add(leftover);
        }

        return result;
    }

    public static AvitoAdListingRecord ApplyDetail(
        AvitoAdListingRecord record,
        AvitoAdDetailParseResult detail,
        DateTime utcNow)
    {
        record.DetailCheckedAtUtc = utcNow;
        record.UpdatedAtUtc = utcNow;

        if (!detail.Success || detail.PublishedAtUtc is null)
        {
            record.LastParseError = detail.FailureReason ?? "detail_parse_failed";
            if (record.PublishedAtUtc is null)
            {
                record.PublicationDateSource = AvitoAdPublicationDateSources.Unknown;
            }

            AvitoAdListingStateCalculator.Refresh(record, utcNow);
            if (record.PublishedAtUtc is null)
            {
                record.State = AvitoAdListingStates.ParseFailed;
            }

            return record;
        }

        record.LastParseError = null;
        record.PublishedAtUtc = detail.PublishedAtUtc;
        record.PublicationDateSource = AvitoAdPublicationDateSources.Exact;
        record.ExpiresAtUtc = AvitoAdExpiryCalculator.ComputeExpiresAtUtc(detail.PublishedAtUtc.Value);
        if (!string.IsNullOrWhiteSpace(detail.Title))
        {
            record.Title = detail.Title;
        }

        if (detail.RemainingDays is int remaining)
        {
            record.RemainingDays = remaining;
        }

        AvitoAdListingStateCalculator.Refresh(record, utcNow);
        return record;
    }
}
