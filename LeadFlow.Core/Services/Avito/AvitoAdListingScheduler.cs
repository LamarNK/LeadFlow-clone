using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoAdListingScheduler
{
    public static bool ShouldCheckList(
        DateTime? lastSuccessfulListCheckAtUtc,
        DateTime utcNow,
        TimeSpan interval)
    {
        if (lastSuccessfulListCheckAtUtc is null)
        {
            return true;
        }

        return utcNow - lastSuccessfulListCheckAtUtc.Value >= interval;
    }

    public static bool ShouldOpenDetail(
        AvitoAdListingRecord? existing,
        AvitoAdListCard card,
        DateTime utcNow,
        AvitoAdListingScheduleOptions options)
    {
        if (existing is null)
        {
            return true;
        }

        if (existing.PublishedAtUtc is null
            || !string.Equals(existing.PublicationDateSource, AvitoAdPublicationDateSources.Exact, StringComparison.Ordinal))
        {
            return DetailIsDue(existing.DetailCheckedAtUtc, utcNow, options.UnknownDateRecheckAfter);
        }

        if (card.AgeDays is null)
        {
            return true;
        }

        var expectedAge = AvitoAdExpiryCalculator.CalendarAgeDays(existing.PublishedAtUtc.Value, utcNow);
        if (Math.Abs(card.AgeDays.Value - expectedAge) > 1)
        {
            return true;
        }

        if (!string.Equals(existing.StatusText ?? string.Empty, card.StatusText ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        var remaining = existing.ExpiresAtUtc is DateTime expires
            ? AvitoAdExpiryCalculator.RemainingControlDays(expires, utcNow)
            : int.MaxValue;

        if (remaining <= options.ApproachingDays)
        {
            return DetailIsDue(existing.DetailCheckedAtUtc, utcNow, options.ApproachingRecheckAfter);
        }

        if (expectedAge < options.FreshAgeDays && existing.DetailCheckedAtUtc is not null)
        {
            return false;
        }

        return DetailIsDue(existing.DetailCheckedAtUtc, utcNow, options.StaleDetailRecheckAfter);
    }

    public static IReadOnlyList<AvitoAdListingRecord> PlanDetailChecks(
        IReadOnlyList<AvitoAdListingRecord> records,
        IReadOnlyDictionary<string, AvitoAdListCard> cardsById,
        DateTime utcNow,
        AvitoAdListingScheduleOptions options)
    {
        return PlanDetailCheckQueue(records, cardsById, utcNow, options)
            .Take(Math.Max(0, options.MaxDetailPagesPerRun))
            .ToList();
    }

    /// <summary>
    /// Полная упорядоченная очередь объявлений, которым нужна детальная проверка.
    /// В отличие от <see cref="PlanDetailChecks"/>, лимит страниц здесь не применяется:
    /// вызывающая сторона использует её для прозрачного журналирования ожидания в очереди.
    /// </summary>
    public static IReadOnlyList<AvitoAdListingRecord> PlanDetailCheckQueue(
        IReadOnlyList<AvitoAdListingRecord> records,
        IReadOnlyDictionary<string, AvitoAdListCard> cardsById,
        DateTime utcNow,
        AvitoAdListingScheduleOptions options)
    {
        var ranked = new List<(int Priority, AvitoAdListingRecord Record)>();
        foreach (var record in records)
        {
            if (!record.IsActive)
            {
                continue;
            }

            cardsById.TryGetValue(record.AvitoItemId, out var card);
            card ??= new AvitoAdListCard
            {
                AvitoItemId = record.AvitoItemId,
                Title = record.Title,
                Url = record.Url,
                AgeDays = record.AgeDays,
                StatusText = record.StatusText
            };

            if (!ShouldOpenDetail(record, card, utcNow, options))
            {
                continue;
            }

            ranked.Add((Priority(record, utcNow, options), record));
        }

        return ranked
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Record.DetailCheckedAtUtc ?? DateTime.MinValue)
            .Select(x => x.Record)
            .ToList();
    }

    private static bool DetailIsDue(DateTime? lastCheckUtc, DateTime utcNow, TimeSpan interval)
    {
        if (lastCheckUtc is null)
        {
            return true;
        }

        return utcNow - lastCheckUtc.Value >= interval;
    }

    private static int Priority(AvitoAdListingRecord record, DateTime utcNow, AvitoAdListingScheduleOptions options)
    {
        if (record.PublishedAtUtc is null
            || string.Equals(record.State, AvitoAdListingStates.ParseFailed, StringComparison.Ordinal)
            || string.Equals(record.State, AvitoAdListingStates.UnknownPublicationDate, StringComparison.Ordinal))
        {
            return 0;
        }

        var remaining = record.ExpiresAtUtc is DateTime expires
            ? AvitoAdExpiryCalculator.RemainingControlDays(expires, utcNow)
            : int.MaxValue;

        if (remaining <= 0)
        {
            return 1;
        }

        if (remaining <= options.ApproachingDays)
        {
            return 2;
        }

        if (record.DetailCheckedAtUtc is null)
        {
            return 3;
        }

        return 4;
    }
}
