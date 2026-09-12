using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class AvitoAdsQueryService(OrbitaDbContext db, OfficeScopeService officeScope)
{
    public async Task<AvitoAdListingListResponse> GetListingsAsync(
        OfficeScope scope,
        Guid? workerId,
        Guid? accountId,
        string? subProfileId,
        string? state,
        bool? isActive,
        string? q,
        CancellationToken ct,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        IReadOnlyList<string>? subProfileIds = null)
    {
        var workers = officeScope.ApplyWorkerFilter(db.Workers.AsNoTracking(), scope);
        var query =
            from ad in db.WorkerAvitoAds.AsNoTracking()
            join worker in workers on ad.WorkerId equals worker.Id
            join account in db.WorkerAccounts.AsNoTracking()
                on new { ad.WorkerId, ad.AccountId } equals new { account.WorkerId, account.AccountId }
            select new { ad, worker, account };

        var selectedWorkers = ResponseCatalogFilterValues.MergeIds(workerIds, workerId);
        if (selectedWorkers.Count > 0)
        {
            query = query.Where(x => selectedWorkers.Contains(x.ad.WorkerId));
        }

        var selectedAccounts = ResponseCatalogFilterValues.MergeIds(accountIds, accountId);
        if (selectedAccounts.Count > 0)
        {
            query = query.Where(x => selectedAccounts.Contains(x.ad.AccountId));
        }

        var selectedSubProfiles = ResponseCatalogFilterValues.MergeValues(subProfileIds, subProfileId);
        if (selectedSubProfiles.Count > 0)
        {
            query = query.Where(x => selectedSubProfiles.Contains(x.ad.AvitoSubProfileId));
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            query = query.Where(x => x.ad.State == state);
        }

        if (isActive is bool active)
        {
            query = query.Where(x => x.ad.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                x.ad.Title.Contains(term)
                || x.ad.AvitoItemId.Contains(term)
                || x.account.DisplayName.Contains(term)
                || x.worker.DisplayName.Contains(term)
                || x.ad.StatusText.Contains(term));
        }

        var rows = await query
            .OrderBy(x => x.ad.ExpiresAtUtc == null)
            .ThenBy(x => x.ad.ExpiresAtUtc)
            .ThenBy(x => x.ad.Title)
            .ToListAsync(ct);

        var items = rows.Select(x => new AvitoAdListingListItem(
            x.ad.Id,
            x.ad.WorkerId,
            x.worker.DisplayName,
            x.ad.AccountId,
            x.account.DisplayName,
            x.ad.AvitoSubProfileId,
            ResolveSubProfileName(x.account.SubProfilesJson, x.ad.AvitoSubProfileId),
            x.ad.AvitoItemId,
            x.ad.Title,
            x.ad.Url,
            x.ad.StatusText,
            x.ad.PublishedAtUtc,
            x.ad.ExpiresAtUtc,
            x.ad.AgeDays,
            x.ad.RemainingDays,
            x.ad.State,
            x.ad.PublicationDateSource,
            x.ad.LastSeenAtUtc,
            x.ad.DetailCheckedAtUtc,
            x.ad.IsActive,
            x.ad.LastParseError)).ToList();

        var summarySource = items;
        if (isActive is not null || !string.IsNullOrWhiteSpace(state))
        {
            var allForSummary = await (
                    from ad in db.WorkerAvitoAds.AsNoTracking()
                    join worker in workers on ad.WorkerId equals worker.Id
                    select ad)
                .ToListAsync(ct);
            if (selectedWorkers.Count > 0)
            {
                allForSummary = allForSummary.Where(x => selectedWorkers.Contains(x.WorkerId)).ToList();
            }

            if (selectedAccounts.Count > 0)
            {
                allForSummary = allForSummary.Where(x => selectedAccounts.Contains(x.AccountId)).ToList();
            }

            if (selectedSubProfiles.Count > 0)
            {
                allForSummary = allForSummary.Where(x => selectedSubProfiles.Contains(x.AvitoSubProfileId)).ToList();
            }

            summarySource = allForSummary.Select(x => new AvitoAdListingListItem(
                x.Id,
                x.WorkerId,
                string.Empty,
                x.AccountId,
                string.Empty,
                x.AvitoSubProfileId,
                string.Empty,
                x.AvitoItemId,
                x.Title,
                x.Url,
                x.StatusText,
                x.PublishedAtUtc,
                x.ExpiresAtUtc,
                x.AgeDays,
                x.RemainingDays,
                x.State,
                x.PublicationDateSource,
                x.LastSeenAtUtc,
                x.DetailCheckedAtUtc,
                x.IsActive,
                x.LastParseError)).ToList();
        }

        var summary = new AvitoAdListingSummary(
            summarySource.Count(x => x.IsActive),
            summarySource.Count(x => x.IsActive && x.State == AvitoAdListingStates.UnknownPublicationDate),
            summarySource.Count(x => x.IsActive && x.State == AvitoAdListingStates.ApproachingExpiry),
            summarySource.Count(x => x.IsActive && x.State == AvitoAdListingStates.ExpiresToday),
            summarySource.Count(x => x.IsActive && x.State == AvitoAdListingStates.Expired));

        return new AvitoAdListingListResponse(items, summary, items.Count);
    }

    private static string ResolveSubProfileName(string? json, string subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return "—";
        }

        var profiles = SubProfileDeserializer.Deserialize(json);
        return profiles?.FirstOrDefault(x => string.Equals(x.Id, subProfileId, StringComparison.Ordinal))?.Name
               ?? subProfileId;
    }
}
