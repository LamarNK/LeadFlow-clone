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
        IReadOnlyList<string>? subProfileIds = null,
        string? tab = null,
        int page = 1,
        int pageSize = 100,
        string? sort = null,
        string? sortDir = null)
    {
        var workers = officeScope.ApplyWorkerFilter(db.Workers.AsNoTracking(), scope);
        IQueryable<ListingQueryRow> query =
            from ad in db.WorkerAvitoAds.AsNoTracking()
            join worker in workers on ad.WorkerId equals worker.Id
            join account in db.WorkerAccounts.AsNoTracking()
                on new { ad.WorkerId, ad.AccountId } equals new { account.WorkerId, account.AccountId }
            select new ListingQueryRow
            {
                Ad = ad,
                WorkerName = worker.DisplayName,
                AccountName = account.DisplayName,
                SubProfilesJson = account.SubProfilesJson
            };

        var selectedWorkers = ResponseCatalogFilterValues.MergeIds(workerIds, workerId);
        if (selectedWorkers.Count > 0)
        {
            query = query.Where(x => selectedWorkers.Contains(x.Ad.WorkerId));
        }

        var selectedAccounts = ResponseCatalogFilterValues.MergeIds(accountIds, accountId);
        if (selectedAccounts.Count > 0)
        {
            query = query.Where(x => selectedAccounts.Contains(x.Ad.AccountId));
        }

        var selectedSubProfiles = ResponseCatalogFilterValues.MergeValues(subProfileIds, subProfileId);
        if (selectedSubProfiles.Count > 0)
        {
            query = query.Where(x => selectedSubProfiles.Contains(x.Ad.AvitoSubProfileId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                x.Ad.Title.Contains(term)
                || x.Ad.AvitoItemId.Contains(term)
                || x.AccountName.Contains(term)
                || x.WorkerName.Contains(term)
                || x.Ad.StatusText.Contains(term));
        }

        // KPI are deliberately calculated before the tab/page filter: they describe
        // the complete office/filter/search result, not only the visible page.
        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new AvitoAdListingSummary(
                group.Count(x => x.Ad.IsActive),
                group.Count(x => x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.UnknownPublicationDate),
                group.Count(x => x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.ApproachingExpiry),
                group.Count(x => x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.ExpiresToday),
                group.Count(x => x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.Expired),
                group.Count(x => x.Ad.SourceTab == "rejected"),
                group.Count(x => x.Ad.SourceTab == "inactive")))
            .SingleOrDefaultAsync(ct)
            ?? new AvitoAdListingSummary(0, 0, 0, 0, 0);

        var rowsQuery = query;
        if (!string.IsNullOrWhiteSpace(state))
        {
            rowsQuery = rowsQuery.Where(x => x.Ad.State == state);
        }

        if (isActive is bool active)
        {
            rowsQuery = rowsQuery.Where(x => x.Ad.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(tab))
        {
            rowsQuery = NormalizeTab(tab) switch
            {
                "all" => rowsQuery,
                "errors" => rowsQuery.Where(x => x.Ad.SourceTab == "rejected"),
                "unpublished" => rowsQuery.Where(x => x.Ad.SourceTab == "inactive"),
                "notactive" => rowsQuery.Where(x => !x.Ad.IsActive),
                "expiring" => rowsQuery.Where(x =>
                    x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.ApproachingExpiry),
                "today" => rowsQuery.Where(x =>
                    x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.ExpiresToday),
                "unknown" => rowsQuery.Where(x =>
                    x.Ad.IsActive && x.Ad.State == AvitoAdListingStates.UnknownPublicationDate),
                "parsefailed" => rowsQuery.Where(x => x.Ad.State == AvitoAdListingStates.ParseFailed),
                _ => rowsQuery.Where(x => x.Ad.SourceTab == "active" && x.Ad.IsActive)
            };
        }

        var total = await rowsQuery.CountAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var rows = await ApplySort(rowsQuery, sort, sortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => new AvitoAdListingListItem(
            x.Ad.Id,
            x.Ad.WorkerId,
            x.WorkerName,
            x.Ad.AccountId,
            x.AccountName,
            x.Ad.AvitoSubProfileId,
            ResolveSubProfileName(x.SubProfilesJson, x.Ad.AvitoSubProfileId),
            x.Ad.AvitoItemId,
            x.Ad.Title,
            x.Ad.Url,
            x.Ad.StatusText,
            x.Ad.PublishedAtUtc,
            x.Ad.ExpiresAtUtc,
            x.Ad.AgeDays,
            x.Ad.RemainingDays,
            x.Ad.State,
            x.Ad.PublicationDateSource,
            x.Ad.LastSeenAtUtc,
            x.Ad.DetailCheckedAtUtc,
            x.Ad.IsActive,
            x.Ad.LastParseError,
            x.Ad.SourceTab,
            x.Ad.ErrorReason,
            x.Ad.CanPublish,
            x.Ad.ImageUrl,
            x.Ad.Salary,
            x.Ad.City,
            x.Ad.AddressText,
            x.Ad.DistrictText,
            x.Ad.Views,
            x.Ad.Contacts,
            x.Ad.Favorites)).ToList();

        return new AvitoAdListingListResponse(items, summary, total);
    }

    private static IOrderedQueryable<ListingQueryRow> ApplySort(
        IQueryable<ListingQueryRow> query,
        string? sort,
        string? sortDir)
    {
        var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return sort?.Trim().ToLowerInvariant() switch
        {
            "worker" => descending
                ? query.OrderByDescending(x => x.WorkerName).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.WorkerName).ThenBy(x => x.Ad.Id),
            "account" => descending
                ? query.OrderByDescending(x => x.AccountName).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.AccountName).ThenBy(x => x.Ad.Id),
            "subprofile" => descending
                ? query.OrderByDescending(x => x.Ad.AvitoSubProfileId).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.AvitoSubProfileId).ThenBy(x => x.Ad.Id),
            "title" => descending
                ? query.OrderByDescending(x => x.Ad.Title).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.Title).ThenBy(x => x.Ad.Id),
            "id" => descending
                ? query.OrderByDescending(x => x.Ad.AvitoItemId).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.AvitoItemId).ThenBy(x => x.Ad.Id),
            "status" => descending
                ? query.OrderByDescending(x => x.Ad.StatusText).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.StatusText).ThenBy(x => x.Ad.Id),
            "published" => descending
                ? query.OrderByDescending(x => x.Ad.PublishedAtUtc).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.PublishedAtUtc).ThenBy(x => x.Ad.Id),
            "age" => descending
                ? query.OrderByDescending(x => x.Ad.AgeDays).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.AgeDays).ThenBy(x => x.Ad.Id),
            "remaining" => descending
                ? query.OrderByDescending(x => x.Ad.RemainingDays).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.RemainingDays).ThenBy(x => x.Ad.Id),
            "state" => descending
                ? query.OrderByDescending(x => x.Ad.State).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.State).ThenBy(x => x.Ad.Id),
            "source" => descending
                ? query.OrderByDescending(x => x.Ad.PublicationDateSource).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.PublicationDateSource).ThenBy(x => x.Ad.Id),
            "seen" => descending
                ? query.OrderByDescending(x => x.Ad.LastSeenAtUtc).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.LastSeenAtUtc).ThenBy(x => x.Ad.Id),
            "detail" => descending
                ? query.OrderByDescending(x => x.Ad.DetailCheckedAtUtc).ThenBy(x => x.Ad.Id)
                : query.OrderBy(x => x.Ad.DetailCheckedAtUtc).ThenBy(x => x.Ad.Id),
            _ when descending => query
                .OrderByDescending(x => x.Ad.ExpiresAtUtc == null)
                .ThenByDescending(x => x.Ad.ExpiresAtUtc)
                .ThenBy(x => x.Ad.Title)
                .ThenBy(x => x.Ad.Id),
            _ => query
                .OrderBy(x => x.Ad.ExpiresAtUtc == null)
                .ThenBy(x => x.Ad.ExpiresAtUtc)
                .ThenBy(x => x.Ad.Title)
                .ThenBy(x => x.Ad.Id)
        };
    }

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "all" or "errors" or "unpublished" or "notactive" or "expiring" or "today" or "unknown" or "parsefailed" or "active"
                => tab.Trim().ToLowerInvariant(),
            _ => "active"
        };

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

    private sealed class ListingQueryRow
    {
        public WorkerAvitoAdEntity Ad { get; init; } = null!;
        public string WorkerName { get; init; } = string.Empty;
        public string AccountName { get; init; } = string.Empty;
        public string SubProfilesJson { get; init; } = string.Empty;
    }
}
