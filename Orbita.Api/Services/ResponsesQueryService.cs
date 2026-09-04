using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponsesQueryService(
    OrbitaDbContext db,
    ResponseBitrixDeliveryService deliveries)
{
    public Task<ResponsesPageDto> GetPageAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        GetPageInternalAsync(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo,
            fromUtc,
            toUtc,
            page,
            pageSize,
            sort,
            sortDir,
            ct);

    public async Task<ResponsesSummaryDto> GetSummaryAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct = default)
    {
        var query = BuildFilteredQuery(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo,
            fromUtc,
            toUtc);
        var total = await query.CountAsync(ct);
        if (total == 0)
        {
            return new ResponsesSummaryDto(0, 0, 0, 0, 0, null);
        }

        var duplicates = await query.CountAsync(x => x.Status == ResponseStatuses.Duplicate, ct);
        var sent = await CountSentAsync(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo,
            fromUtc,
            toUtc,
            ct);
        var unique = total - duplicates;
        var uniqueAuthors = await ResponseSummaryMetrics.CountUniqueAuthorsAsync(query, ct);

        var avgMinutes = await query
            .Where(x => x.CollectedAt > x.CreatedAt)
            .Select(x => (double?)(x.CollectedAt - x.CreatedAt).TotalMinutes)
            .AverageAsync(ct);

        return new ResponsesSummaryDto(
            total,
            unique,
            duplicates,
            sent,
            uniqueAuthors,
            avgMinutes > 0 ? avgMinutes : null);
    }

    /// <summary>
    /// "Sent" counts every successful delivery (CRM + Bitrix) by its actual send date
    /// (delivery CreatedAtUtc / card creation / ProcessedAt for legacy rows), not by collection date.
    /// </summary>
    private async Task<int> CountSentAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct)
    {
        var scopedQuery = BuildFilteredQueryCore(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo);
        var scopedResponseIds = scopedQuery.Select(x => x.Id);

        var bitrixSends = await db.ResponseBitrixDeliveries
            .AsNoTracking()
            .CountAsync(d =>
                d.Outcome == ResponseBitrixDeliveryOutcomes.Sent
                && d.CreatedAtUtc >= (fromUtc ?? DateTime.MinValue)
                && d.CreatedAtUtc < (toUtc ?? DateTime.MaxValue)
                && scopedResponseIds.Contains(d.ResponseId), ct);

        var crmSends = await db.ResponseCrmDeliveries
            .AsNoTracking()
            .CountAsync(d =>
                d.Outcome == ResponseCrmDeliveryOutcomes.Sent
                && d.CreatedAtUtc >= (fromUtc ?? DateTime.MinValue)
                && d.CreatedAtUtc < (toUtc ?? DateTime.MaxValue)
                && scopedResponseIds.Contains(d.ResponseId), ct);

        // Legacy rows created before the delivery journal existed: Status=Sent responses
        // without any journal entry or CRM card are counted by their process/send time once.
        var responsesWithBitrixDelivery = db.ResponseBitrixDeliveries.AsNoTracking().Select(d => d.ResponseId);
        var responsesWithCrmDelivery = db.ResponseCrmDeliveries.AsNoTracking().Select(d => d.ResponseId);
        var responsesWithCrmCard = db.CrmCandidateCards.AsNoTracking().Select(c => c.ResponseId);
        var legacySends = await scopedQuery
            .CountAsync(x =>
                x.Status == ResponseStatuses.Sent
                && x.ProcessedAt != null
                && x.ProcessedAt >= (fromUtc ?? DateTime.MinValue)
                && x.ProcessedAt < (toUtc ?? DateTime.MaxValue)
                && !responsesWithBitrixDelivery.Contains(x.Id)
                && !responsesWithCrmDelivery.Contains(x.Id)
                && !responsesWithCrmCard.Contains(x.Id), ct);

        // Legacy CRM cards: card creation time is the CRM send time when no CRM delivery
        // journal entry exists for the same response and office.
        var crmDeliveryKeys = db.ResponseCrmDeliveries.AsNoTracking()
            .Select(d => new { d.ResponseId, d.OfficeId });
        var legacyCrmCards = await db.CrmCandidateCards
            .AsNoTracking()
            .CountAsync(card =>
                card.CreatedAtUtc >= (fromUtc ?? DateTime.MinValue)
                && card.CreatedAtUtc < (toUtc ?? DateTime.MaxValue)
                && !crmDeliveryKeys.Any(d => d.ResponseId == card.ResponseId && d.OfficeId == card.OfficeId)
                && scopedResponseIds.Contains(card.ResponseId), ct);

        return bitrixSends + crmSends + legacySends + legacyCrmCards;
    }

    public async Task<IReadOnlyList<ResponseFilterAccountDto>> GetFilterAccountsAsync(
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct = default)
    {
        var query = ApplyOfficeFilter(db.CandidateResponses.AsNoTracking(), scope, officeFilter);
        var rows = await query
            .Select(x => new { x.AccountId, x.AccountName })
            .Distinct()
            .OrderBy(x => x.AccountName)
            .ToListAsync(ct);
        return rows.Select(x => new ResponseFilterAccountDto(x.AccountId, x.AccountName)).ToList();
    }

    public async Task<IReadOnlyList<ResponseFilterVacancyDto>> GetFilterVacanciesAsync(
        OfficeScope scope,
        Guid? officeFilter,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct = default)
    {
        var query = ApplyOfficeFilter(db.CandidateResponses.AsNoTracking(), scope, officeFilter)
            .Where(x => x.Vacancy != "");

        if (fromUtc is not null)
        {
            query = query.Where(x => x.CollectedAt >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(x => x.CollectedAt < toUtc.Value);
        }

        var rows = await query
            .GroupBy(x => x.Vacancy)
            .Select(g => new { Vacancy = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Vacancy)
            .Take(100)
            .ToListAsync(ct);

        return rows.Select(x => new ResponseFilterVacancyDto(x.Vacancy, x.Count)).ToList();
    }

    public async Task<ResponseDetailDto?> GetDetailAsync(
        Guid id,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .AsNoTracking()
            .Include(x => x.Office)
            .Include(x => x.Worker)
            .Include(x => x.BitrixInstance)
            .Include(x => x.DuplicateBitrixInstance)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null)
        {
            return null;
        }

        var sharedWithScope = false;
        if (!scope.IsGlobalAdmin && scope.OfficeId is Guid scopeOfficeId)
        {
            sharedWithScope = await db.ResponseCrmDeliveries.AsNoTracking()
                .AnyAsync(
                    d => d.ResponseId == entity.Id
                         && d.OfficeId == scopeOfficeId
                         && d.Outcome == ResponseCrmDeliveryOutcomes.Sent,
                    ct);
            if (!sharedWithScope)
            {
                sharedWithScope = await db.CrmCandidateCards.AsNoTracking()
                    .AnyAsync(c => c.ResponseId == entity.Id && c.OfficeId == scopeOfficeId, ct);
            }
        }

        if (!scope.CanAccessResponse(entity.OfficeId, entity.Worker?.OfficeId, sharedWithScope))
        {
            return null;
        }

        var portalHost = entity.BitrixInstance?.PortalHost;
        var subProfilesJson = await db.WorkerAccounts
            .AsNoTracking()
            .Where(a => a.AccountId == entity.AccountId)
            .Select(a => a.SubProfilesJson)
            .FirstOrDefaultAsync(ct);
        var nameLookup = SubProfileNameResolver.BuildLookup([(entity.AccountId, subProfilesJson ?? "[]")]);
        var subProfileName = CoalesceSubProfileName(
            entity.AvitoSubProfileName,
            nameLookup,
            entity.AccountId,
            entity.AvitoSubProfileId);
        var deliveryLookup = await deliveries.LoadByResponseIdsAsync([entity.Id], ct);
        var bitrixDeliveries = deliveryLookup.TryGetValue(entity.Id, out var loaded)
            ? loaded
            : [];
        var phoneHistory = await db.CandidatePhoneHistory
            .AsNoTracking()
            .Where(x => x.PersonId == entity.PersonId)
            .OrderBy(x => x.RecordedAtUtc)
            .Select(x => new CandidatePhoneHistoryDto(
                x.PhoneRaw,
                x.PhoneNormalized,
                x.RecordedAtUtc))
            .ToListAsync(ct);
        return MapDetail(entity, portalHost, subProfileName, bitrixDeliveries, phoneHistory);
    }

    public async Task<ResponseAvatarFile?> GetAvatarAsync(
        Guid id,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.OfficeId,
                WorkerOfficeId = x.Worker == null ? (Guid?)null : x.Worker.OfficeId,
                x.AvatarImage
            })
            .FirstOrDefaultAsync(ct);
        if (entity?.AvatarImage is not { Length: > 0 })
        {
            return null;
        }

        var sharedWithScope = false;
        if (!scope.IsGlobalAdmin && scope.OfficeId is Guid scopeOfficeId)
        {
            sharedWithScope = await db.ResponseCrmDeliveries.AsNoTracking()
                .AnyAsync(
                    d => d.ResponseId == entity.Id
                         && d.OfficeId == scopeOfficeId
                         && d.Outcome == ResponseCrmDeliveryOutcomes.Sent,
                    ct);
            if (!sharedWithScope)
            {
                sharedWithScope = await db.CrmCandidateCards.AsNoTracking()
                    .AnyAsync(c => c.ResponseId == entity.Id && c.OfficeId == scopeOfficeId, ct);
            }
        }

        if (!scope.CanAccessResponse(entity.OfficeId, entity.WorkerOfficeId, sharedWithScope))
        {
            return null;
        }

        var contentType = CandidateResponseAvatar.DetectContentType(entity.AvatarImage);
        return string.IsNullOrEmpty(contentType)
            ? null
            : new ResponseAvatarFile(entity.AvatarImage, contentType);
    }

    private async Task<ResponsesPageDto> GetPageInternalAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        string? sort,
        string? sortDir,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildFilteredQuery(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo,
            fromUtc,
            toUtc);
        var total = await query.CountAsync(ct);
        var orderedQuery = ApplyOrdering(query, sort, sortDir);
        orderedQuery = orderedQuery
            .Include(x => x.BitrixInstance)
            .Include(x => x.DuplicateBitrixInstance);
        var rows = await orderedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.OfficeId,
                x.WorkerId,
                WorkerName = x.WorkerName != "" ? x.WorkerName : (x.Worker != null ? x.Worker.DisplayName : string.Empty),
                x.AccountId,
                x.AccountName,
                x.Source,
                x.SourceResponseId,
                x.FullName,
                x.Age,
                x.Gender,
                x.PhoneRaw,
                x.PhoneNormalized,
                x.Vacancy,
                x.VacancyUrl,
                x.MessengerUrl,
                HasAvatar = x.AvatarImage != null,
                x.City,
                x.Status,
                x.IsLocalDuplicate,
                x.IsBitrixDuplicate,
                x.BitrixEntityId,
                x.BitrixEntityType,
                x.BitrixInstanceId,
                BitrixInstanceName = x.BitrixInstance != null ? x.BitrixInstance.Name : null,
                BitrixInstanceSignature = x.BitrixInstance != null ? x.BitrixInstance.Signature : null,
                BitrixPortalHost = x.BitrixInstance != null ? x.BitrixInstance.PortalHost : null,
                x.DuplicateBitrixInstanceId,
                DuplicateBitrixInstanceName = x.DuplicateBitrixInstance != null
                    ? (x.DuplicateBitrixInstance.Signature != "" ? x.DuplicateBitrixInstance.Signature : x.DuplicateBitrixInstance.Name)
                    : null,
                x.AvitoSubProfileId,
                x.AvitoSubProfileName,
                ResponseHighlightEnabled = x.Worker != null && x.Worker.ResponseHighlightEnabled,
                ResponseHighlightAgeBuckets = x.Worker != null ? x.Worker.ResponseHighlightAgeBuckets : null,
                ResponseHighlightTargetsJson = x.Worker != null ? x.Worker.ResponseHighlightTargetsJson : null,
                x.CreatedAt,
                x.CollectedAt,
                x.ProcessedAt,
                x.PhoneMetricKind,
                x.PreviousPhoneRaw,
                x.PreviousPhoneNormalized,
                x.PhoneUnchangedHours,
                x.PhoneChangedAtUtc
            })
            .ToListAsync(ct);

        var accountIds = rows.Select(x => x.AccountId).Distinct().ToList();
        var subProfileRows = accountIds.Count == 0
            ? []
            : await db.WorkerAccounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.AccountId))
                .Select(a => new { a.AccountId, a.SubProfilesJson })
                .ToListAsync(ct);

        var nameLookup = SubProfileNameResolver.BuildLookup(
            subProfileRows.Select(x => (x.AccountId, x.SubProfilesJson)));

        var responseIds = rows.Select(x => x.Id).ToList();
        var deliveryLookup = await deliveries.LoadByResponseIdsAsync(responseIds, ct);
        var crmDeliveryLookup = await LoadCrmDeliveriesByResponseIdsAsync(responseIds, ct);

        var items = rows
            .Select(x =>
            {
                var bitrixEntityUrl = BitrixPortalLinks.TryBuildEntityDetailsUrl(
                    x.BitrixPortalHost,
                    x.BitrixEntityType,
                    x.BitrixEntityId);
                var highlightLabels = ResponseHighlightRules.GetHighlightLabels(
                    x.Age,
                    x.ResponseHighlightEnabled,
                    x.ResponseHighlightAgeBuckets,
                    x.AccountId,
                    x.AvitoSubProfileId,
                    x.ResponseHighlightTargetsJson);
                var isHighlighted = highlightLabels.Count > 0;
                var highlightLabel = highlightLabels.FirstOrDefault();
                var bitrixDeliveries = deliveryLookup.TryGetValue(x.Id, out var loaded)
                    ? loaded
                    : [];
                var crmDeliveries = crmDeliveryLookup.TryGetValue(x.Id, out var loadedCrm)
                    ? loadedCrm
                    : [];
                var phoneMetricLabel = ResponsePhoneMetricKinds.FormatLabel(
                    x.PhoneMetricKind,
                    x.PhoneUnchangedHours,
                    string.IsNullOrWhiteSpace(x.PreviousPhoneRaw) ? x.PreviousPhoneNormalized : x.PreviousPhoneRaw);
                return new ResponseListItemDto(
                    x.Id,
                    x.OfficeId,
                    x.WorkerId ?? Guid.Empty,
                    x.WorkerName,
                    x.AccountId,
                    x.AccountName,
                    x.Source,
                    x.SourceResponseId,
                    x.FullName,
                    x.Age,
                    string.IsNullOrWhiteSpace(x.Gender) ? null : x.Gender,
                    x.PhoneRaw,
                    x.PhoneNormalized,
                    x.Vacancy,
                    x.VacancyUrl,
                    x.MessengerUrl,
                    x.City,
                    x.Status,
                    x.IsLocalDuplicate,
                    x.IsBitrixDuplicate,
                    x.BitrixEntityId,
                    string.IsNullOrWhiteSpace(x.BitrixEntityType) ? null : x.BitrixEntityType,
                    bitrixEntityUrl,
                    x.BitrixInstanceId,
                    x.BitrixInstanceName,
                    x.BitrixInstanceSignature,
                    x.DuplicateBitrixInstanceId,
                    x.DuplicateBitrixInstanceName,
                    x.AvitoSubProfileId,
                    CoalesceSubProfileName(
                        x.AvitoSubProfileName,
                        nameLookup,
                        x.AccountId,
                        x.AvitoSubProfileId),
                    isHighlighted,
                    highlightLabel,
                    x.CreatedAt,
                    x.CollectedAt,
                    x.ProcessedAt,
                    bitrixDeliveries,
                    x.PhoneMetricKind ?? string.Empty,
                    string.IsNullOrWhiteSpace(x.PreviousPhoneRaw) ? null : x.PreviousPhoneRaw,
                    string.IsNullOrWhiteSpace(x.PreviousPhoneNormalized) ? null : x.PreviousPhoneNormalized,
                    x.PhoneUnchangedHours,
                    x.PhoneChangedAtUtc,
                    string.IsNullOrWhiteSpace(phoneMetricLabel) ? null : phoneMetricLabel,
                    x.HasAvatar,
                    highlightLabels,
                    crmDeliveries);
            })
            .ToList();

        return new ResponsesPageDto(items, total, page, pageSize);
    }

    private async Task<IReadOnlyDictionary<Guid, List<ResponseCrmDeliveryDto>>> LoadCrmDeliveriesByResponseIdsAsync(
        IReadOnlyList<Guid> responseIds,
        CancellationToken ct)
    {
        if (responseIds.Count == 0)
        {
            return new Dictionary<Guid, List<ResponseCrmDeliveryDto>>();
        }

        var rows = await db.ResponseCrmDeliveries
            .AsNoTracking()
            .Where(x => responseIds.Contains(x.ResponseId))
            .Include(x => x.Office)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.ResponseId,
                Delivery = new ResponseCrmDeliveryDto(
                    x.Id,
                    x.OfficeId,
                    x.Office.Name,
                    x.Outcome,
                    x.CardId,
                    string.IsNullOrWhiteSpace(x.ErrorMessage) ? null : x.ErrorMessage,
                    x.Source,
                    x.CreatedAtUtc)
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.ResponseId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.Delivery).ToList());
    }

    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "time", "responded", "author", "phone", "city", "age", "gender", "vacancy", "account", "status", "source"
    };

    private static IQueryable<CandidateResponseEntity> ApplyOrdering(
        IQueryable<CandidateResponseEntity> query,
        string? sort,
        string? sortDir)
    {
        var column = NormalizeSortColumn(sort);
        var descending = ResolveDescending(column, sortDir);

        var ordered = column switch
        {
            "author" => descending
                ? query.OrderByDescending(x => x.FullName)
                : query.OrderBy(x => x.FullName),
            "phone" => descending
                ? query.OrderByDescending(x => x.PhoneNormalized).ThenByDescending(x => x.PhoneRaw)
                : query.OrderBy(x => x.PhoneNormalized).ThenBy(x => x.PhoneRaw),
            "city" => descending
                ? query.OrderByDescending(x => x.City)
                : query.OrderBy(x => x.City),
            "age" => descending
                ? query.OrderByDescending(x => x.Age)
                : query.OrderBy(x => x.Age),
            "gender" => descending
                ? query.OrderByDescending(x => x.Gender)
                : query.OrderBy(x => x.Gender),
            "vacancy" => descending
                ? query.OrderByDescending(x => x.Vacancy)
                : query.OrderBy(x => x.Vacancy),
            "account" => descending
                ? query.OrderByDescending(x => x.AccountName)
                : query.OrderBy(x => x.AccountName),
            "status" => descending
                ? query.OrderByDescending(x => x.Status)
                : query.OrderBy(x => x.Status),
            "source" => descending
                ? query.OrderByDescending(x => x.Source)
                : query.OrderBy(x => x.Source),
            "responded" => descending
                ? query.OrderByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.CreatedAt),
            _ => descending
                ? query.OrderByDescending(x => x.CollectedAt)
                : query.OrderBy(x => x.CollectedAt)
        };

        return column is "time" or "responded"
            ? ordered
            : descending
                ? ordered.ThenByDescending(x => x.CollectedAt)
                : ordered.ThenBy(x => x.CollectedAt);
    }

    private static string NormalizeSortColumn(string? sort) =>
        !string.IsNullOrWhiteSpace(sort) && AllowedSortColumns.Contains(sort)
            ? sort.ToLowerInvariant()
            : "time";

    private static bool ResolveDescending(string column, string? dir)
    {
        if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return column is "time" or "responded";
    }

    private IQueryable<CandidateResponseEntity> BuildFilteredQuery(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var query = BuildFilteredQueryCore(
            scope,
            officeFilter,
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            gender,
            ageFrom,
            ageTo);

        if (fromUtc is not null)
        {
            query = query.Where(x => x.CollectedAt >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(x => x.CollectedAt < toUtc.Value);
        }

        return query;
    }

    /// <summary>
    /// Same filters as <see cref="BuildFilteredQuery"/> but without the CollectedAt period range —
    /// used to count sends by their actual send date (delivery CreatedAtUtc / ProcessedAt).
    /// </summary>
    private IQueryable<CandidateResponseEntity> BuildFilteredQueryCore(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo)
    {
        var query = db.CandidateResponses
            .AsNoTracking()
            .Include(x => x.Worker)
            .AsQueryable();

        query = ApplyOfficeFilter(query, scope, officeFilter);

        if (workerId is Guid wid)
        {
            query = query.Where(x => x.WorkerId == wid);
        }

        if (accountId is Guid aid)
        {
            query = query.Where(x => x.AccountId == aid);
        }

        query = ApplyStatusFilter(query, status);
        query = ApplyBitrixDestinationFilter(query, bitrixDestination);

        query = ApplySearchFilter(query, search);
        query = ApplyVacancyFilter(query, vacancy);
        query = ApplyGenderFilter(query, gender);
        query = ApplyAgeFilter(query, ageFrom, ageTo);

        return query;
    }

    private static IQueryable<CandidateResponseEntity> ApplyGenderFilter(
        IQueryable<CandidateResponseEntity> query,
        string? gender)
    {
        var normalized = CandidateGenders.NormalizeFilterValue(gender);
        if (string.IsNullOrEmpty(normalized))
        {
            return query;
        }

        return normalized switch
        {
            CandidateGenders.Male => query.Where(x => x.Gender == CandidateGenders.Male),
            CandidateGenders.Female => query.Where(x => x.Gender == CandidateGenders.Female),
            CandidateGenders.Unknown => query.Where(x => x.Gender == null || x.Gender == string.Empty),
            _ => query
        };
    }

    private static IQueryable<CandidateResponseEntity> ApplyAgeFilter(
        IQueryable<CandidateResponseEntity> query,
        int? ageFrom,
        int? ageTo)
    {
        if (ageFrom is int from)
        {
            query = query.Where(x => x.Age.HasValue && x.Age.Value >= from);
        }

        if (ageTo is int to)
        {
            query = query.Where(x => x.Age.HasValue && x.Age.Value <= to);
        }

        return query;
    }

    private IQueryable<CandidateResponseEntity> ApplyBitrixDestinationFilter(
        IQueryable<CandidateResponseEntity> query,
        string? bitrixDestination)
    {
        if (string.IsNullOrWhiteSpace(bitrixDestination))
        {
            return query;
        }

        if (string.Equals(bitrixDestination, "not_sent", StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(x =>
                x.Status != ResponseStatuses.Sent
                && x.BitrixInstanceId == null
                && !db.ResponseBitrixDeliveries.Any(d =>
                    d.ResponseId == x.Id
                    && d.Outcome == ResponseBitrixDeliveryOutcomes.Sent)
                && !db.ResponseCrmDeliveries.Any(d =>
                    d.ResponseId == x.Id
                    && d.Outcome == ResponseCrmDeliveryOutcomes.Sent));
        }

        // CRM office: crm:{officeId}
        if (bitrixDestination.StartsWith("crm:", StringComparison.OrdinalIgnoreCase))
        {
            var officeRaw = bitrixDestination["crm:".Length..];
            if (!Guid.TryParse(officeRaw, out var officeId))
            {
                return query;
            }

            return query.Where(x =>
                db.ResponseCrmDeliveries.Any(d =>
                    d.ResponseId == x.Id
                    && d.OfficeId == officeId
                    && d.Outcome == ResponseCrmDeliveryOutcomes.Sent));
        }

        if (!Guid.TryParse(bitrixDestination, out var instanceId))
        {
            return query;
        }

        return query.Where(x =>
            (x.BitrixInstanceId == instanceId && x.Status == ResponseStatuses.Sent)
            || db.ResponseBitrixDeliveries.Any(d =>
                d.ResponseId == x.Id
                && d.BitrixInstanceId == instanceId
                && d.Outcome == ResponseBitrixDeliveryOutcomes.Sent));
    }

    private static IQueryable<CandidateResponseEntity> ApplySearchFilter(
        IQueryable<CandidateResponseEntity> query,
        string? search)
    {
        foreach (var token in SearchQueryNormalizer.Tokenize(search))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            var phoneDigits = SearchQueryNormalizer.ExtractDigits(token);
            var hasPhone = phoneDigits.Length >= 4;
            var phonePattern = $"%{phoneDigits}%";

            query = query.Where(x =>
                EF.Functions.ILike(x.FullName, pattern)
                || EF.Functions.ILike(x.PhoneRaw, pattern)
                || EF.Functions.ILike(x.PhoneNormalized, pattern)
                || EF.Functions.ILike(x.Vacancy, pattern)
                || EF.Functions.ILike(x.City, pattern)
                || EF.Functions.ILike(x.AccountName, pattern)
                || (hasPhone && (
                    EF.Functions.ILike(x.PhoneRaw, phonePattern)
                    || EF.Functions.ILike(x.PhoneNormalized, phonePattern))));
        }

        return query;
    }

    private static IQueryable<CandidateResponseEntity> ApplyVacancyFilter(
        IQueryable<CandidateResponseEntity> query,
        string? vacancy)
    {
        foreach (var token in SearchQueryNormalizer.Tokenize(vacancy))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            query = query.Where(x =>
                EF.Functions.ILike(x.Vacancy, pattern)
                || EF.Functions.ILike(x.SourceResponseId, pattern)
                || EF.Functions.ILike(x.VacancyUrl, pattern));
        }

        return query;
    }

    private static IQueryable<CandidateResponseEntity> ApplyStatusFilter(
        IQueryable<CandidateResponseEntity> query,
        string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return query;
        }

        return status.ToLowerInvariant() switch
        {
            "unique" => query.Where(x => x.Status != ResponseStatuses.Duplicate),
            "duplicate" => query.Where(x => x.Status == ResponseStatuses.Duplicate),
            "sent" => query.Where(x => x.Status == ResponseStatuses.Sent),
            "action_required" => query.Where(x => x.Status == ResponseStatuses.ActionRequired),
            "error" => query.Where(x => x.Status == ResponseStatuses.Error),
            "exclude-duplicates" => query.Where(x => x.Status != ResponseStatuses.Duplicate),
            _ => query.Where(x => x.Status == status)
        };
    }

    private static IQueryable<CandidateResponseEntity> ApplyOfficeFilter(
        IQueryable<CandidateResponseEntity> query,
        OfficeScope scope,
        Guid? officeFilter)
    {
        if (scope.IsGlobalAdmin)
        {
            return officeFilter is Guid officeId
                ? query.Where(x =>
                    x.OfficeId == officeId
                    || (x.OfficeId == null && x.Worker != null && x.Worker.OfficeId == officeId)
                    || x.CrmDeliveries.Any(d =>
                        d.OfficeId == officeId && d.Outcome == ResponseCrmDeliveryOutcomes.Sent))
                : query;
        }

        if (scope.OfficeId is Guid scopedOfficeId)
        {
            // Ownership office, collection-pool from home workers, or shared via CRM delivery
            // (same response — no clone/transfer when another office is selected for CRM).
            return query.Where(x =>
                x.OfficeId == scopedOfficeId
                || (x.OfficeId == null && x.Worker != null && x.Worker.OfficeId == scopedOfficeId)
                || x.CrmDeliveries.Any(d =>
                    d.OfficeId == scopedOfficeId && d.Outcome == ResponseCrmDeliveryOutcomes.Sent));
        }

        return query.Where(_ => false);
    }

    private static string? CoalesceSubProfileName(
        string? storedName,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string> lookup,
        Guid accountId,
        string? subProfileId)
    {
        if (!string.IsNullOrWhiteSpace(storedName))
        {
            return storedName.Trim();
        }

        return ResolveSubProfileName(lookup, accountId, subProfileId);
    }

    private static string? ResolveSubProfileName(
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string> lookup,
        Guid accountId,
        string? subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return null;
        }

        var id = subProfileId.Trim();
        return lookup.TryGetValue((accountId, id), out var name) ? name : null;
    }

    private static ResponseDetailDto MapDetail(
        CandidateResponseEntity entity,
        string? portalHost,
        string? subProfileName = null,
        IReadOnlyList<ResponseBitrixDeliveryDto>? bitrixDeliveries = null,
        IReadOnlyList<CandidatePhoneHistoryDto>? phoneHistory = null)
    {
        var bitrixEntityUrl = BitrixPortalLinks.TryBuildEntityDetailsUrl(
            portalHost,
            entity.BitrixEntityType,
            entity.BitrixEntityId);
        var phoneMetricLabel = ResponsePhoneMetricKinds.FormatLabel(
            entity.PhoneMetricKind,
            entity.PhoneUnchangedHours,
            string.IsNullOrWhiteSpace(entity.PreviousPhoneRaw)
                ? entity.PreviousPhoneNormalized
                : entity.PreviousPhoneRaw);
        return new(
            entity.Id,
            entity.OfficeId,
            entity.WorkerId ?? Guid.Empty,
            !string.IsNullOrEmpty(entity.WorkerName)
                ? entity.WorkerName
                : entity.Worker?.DisplayName ?? string.Empty,
            entity.AccountId,
            entity.AccountName,
            entity.Source,
            entity.SourceResponseId,
            entity.FullName,
            entity.FirstName,
            entity.LastName,
            entity.MiddleName,
            entity.Age,
            string.IsNullOrWhiteSpace(entity.Gender) ? null : entity.Gender,
            entity.PhoneRaw,
            entity.PhoneNormalized,
            entity.City,
            entity.Vacancy,
            entity.VacancyUrl,
            entity.MessengerUrl,
            entity.AvitoSubProfileId,
            subProfileName,
            entity.RawText,
            entity.ChatMessagesJson,
            entity.Status,
            entity.IsLocalDuplicate,
            entity.IsBitrixDuplicate,
            entity.DuplicateSummary,
            entity.BitrixEntityId,
            string.IsNullOrWhiteSpace(entity.BitrixEntityType) ? null : entity.BitrixEntityType,
            bitrixEntityUrl,
            entity.BitrixContactId,
            entity.BitrixInstanceId,
            entity.BitrixInstance?.Name,
            entity.BitrixInstance?.Signature,
            entity.DuplicateBitrixInstanceId,
            entity.DuplicateBitrixInstance is null
                ? null
                : (!string.IsNullOrWhiteSpace(entity.DuplicateBitrixInstance.Signature)
                    ? entity.DuplicateBitrixInstance.Signature
                    : entity.DuplicateBitrixInstance.Name),
            string.IsNullOrWhiteSpace(entity.DistributionMode) ? null : entity.DistributionMode,
            entity.ErrorMessage,
            entity.CreatedAt,
            entity.CollectedAt,
            entity.ProcessedAt,
            bitrixDeliveries ?? [],
            entity.PhoneMetricKind ?? string.Empty,
            string.IsNullOrWhiteSpace(entity.PreviousPhoneRaw) ? null : entity.PreviousPhoneRaw,
            string.IsNullOrWhiteSpace(entity.PreviousPhoneNormalized) ? null : entity.PreviousPhoneNormalized,
            entity.PhoneUnchangedHours,
            entity.PhoneChangedAtUtc,
            string.IsNullOrWhiteSpace(phoneMetricLabel) ? null : phoneMetricLabel,
            phoneHistory ?? [],
            entity.AvatarImage is { Length: > 0 },
            entity.Office?.Name);
    }
}

public sealed record ResponseAvatarFile(byte[] Bytes, string ContentType);
