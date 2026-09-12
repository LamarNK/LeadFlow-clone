using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class AvitoAdsSyncService(OrbitaDbContext db, IPanelRealtimeNotifier realtime)
{
    public async Task<IReadOnlyList<WorkerAvitoAdDto>> GetAccountListingsAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        var rows = await db.WorkerAvitoAds
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId && x.AccountId == accountId)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<WorkerAvitoAdListScheduleDto>> GetAccountSchedulesAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        var rows = await db.WorkerAvitoAdListSchedules
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId && x.AccountId == accountId)
            .ToListAsync(ct);

        return rows.Select(ToScheduleDto).ToList();
    }

    public async Task<WorkerAvitoAdSyncResponse> SaveSubProfileSyncAsync(
        Guid workerId,
        WorkerAvitoAdSyncRequest request,
        CancellationToken ct)
    {
        var now = request.CapturedAtUtc == default ? DateTime.UtcNow : DateTime.SpecifyKind(request.CapturedAtUtc, DateTimeKind.Utc);
        var subId = request.AvitoSubProfileId ?? string.Empty;
        var existing = await db.WorkerAvitoAds
            .Where(x => x.WorkerId == workerId
                        && x.AccountId == request.AccountId
                        && x.AvitoSubProfileId == subId)
            .ToListAsync(ct);

        var incomingIds = new HashSet<string>(
            request.Items.Select(x => x.AvitoItemId).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.Ordinal);
        var upserted = 0;

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.AvitoItemId))
            {
                continue;
            }

            var row = existing.FirstOrDefault(x => string.Equals(x.AvitoItemId, item.AvitoItemId, StringComparison.Ordinal));
            if (row is null)
            {
                row = new WorkerAvitoAdEntity
                {
                    Id = Guid.NewGuid(),
                    WorkerId = workerId,
                    AccountId = request.AccountId,
                    AvitoSubProfileId = subId,
                    AvitoItemId = item.AvitoItemId,
                    CreatedAtUtc = now
                };
                db.WorkerAvitoAds.Add(row);
                existing.Add(row);
            }

            ApplyItem(row, item, now, request.ListComplete);
            upserted++;
        }

        var deactivated = 0;
        if (request.ListComplete)
        {
            foreach (var row in existing.Where(x => !incomingIds.Contains(x.AvitoItemId)))
            {
                if (row.IsActive)
                {
                    deactivated++;
                }

                row.IsActive = false;
                row.LastSuccessfulListCheckAtUtc = now;
                row.State = AvitoAdListingStates.NotActive;
                row.UpdatedAtUtc = now;
            }

            var schedule = await db.WorkerAvitoAdListSchedules
                .SingleOrDefaultAsync(
                    x => x.WorkerId == workerId
                         && x.AccountId == request.AccountId
                         && x.AvitoSubProfileId == subId,
                    ct);
            if (schedule is null)
            {
                schedule = new WorkerAvitoAdListScheduleEntity
                {
                    Id = Guid.NewGuid(),
                    WorkerId = workerId,
                    AccountId = request.AccountId,
                    AvitoSubProfileId = subId
                };
                db.WorkerAvitoAdListSchedules.Add(schedule);
            }

            schedule.LastSuccessfulCheckAtUtc = now;
            schedule.NextCheckAtUtc = request.NextListCheckAtUtc is DateTime next
                ? DateTime.SpecifyKind(next, DateTimeKind.Utc)
                : null;
            schedule.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        var officeId = await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => (Guid?)x.OfficeId)
            .FirstOrDefaultAsync(ct);
        realtime.Notify([PanelChangeKind.Listings, PanelChangeKind.Accounts], officeId, workerId);
        return new WorkerAvitoAdSyncResponse(upserted, deactivated);
    }

    private static void ApplyItem(WorkerAvitoAdEntity row, WorkerAvitoAdSyncItemDto item, DateTime now, bool listComplete)
    {
        row.Title = item.Title ?? row.Title;
        row.Url = item.Url ?? row.Url;
        row.StatusText = item.StatusText ?? string.Empty;
        row.PublishedAtUtc = item.PublishedAtUtc ?? row.PublishedAtUtc;
        row.PublicationDateSource = string.IsNullOrWhiteSpace(item.PublicationDateSource)
            ? row.PublicationDateSource
            : item.PublicationDateSource;
        row.AgeDays = item.AgeDays ?? row.AgeDays;
        row.RemainingDays = item.RemainingDays ?? row.RemainingDays;
        row.ExpiresAtUtc = item.ExpiresAtUtc ?? row.ExpiresAtUtc;
        row.LastSeenAtUtc = item.LastSeenAtUtc ?? now;
        row.DetailCheckedAtUtc = item.DetailCheckedAtUtc ?? row.DetailCheckedAtUtc;
        row.IsActive = item.IsActive;
        row.State = string.IsNullOrWhiteSpace(item.State) ? row.State : item.State;
        row.LastParseError = item.LastParseError;
        row.UpdatedAtUtc = now;
        if (listComplete)
        {
            row.LastSuccessfulListCheckAtUtc = now;
        }
    }

    private static WorkerAvitoAdDto ToDto(WorkerAvitoAdEntity row) =>
        new(
            row.Id,
            row.WorkerId,
            row.AccountId,
            row.AvitoSubProfileId,
            row.AvitoItemId,
            row.Title,
            row.Url,
            row.StatusText,
            row.PublishedAtUtc,
            string.IsNullOrWhiteSpace(row.PublicationDateSource)
                ? AvitoAdPublicationDateSources.Unknown
                : row.PublicationDateSource,
            row.AgeDays,
            row.RemainingDays,
            row.ExpiresAtUtc,
            row.LastSeenAtUtc,
            row.DetailCheckedAtUtc,
            row.LastSuccessfulListCheckAtUtc,
            row.IsActive,
            string.IsNullOrWhiteSpace(row.State) ? AvitoAdListingStates.UnknownPublicationDate : row.State,
            row.LastParseError,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);

    private static WorkerAvitoAdListScheduleDto ToScheduleDto(WorkerAvitoAdListScheduleEntity row) =>
        new(
            row.WorkerId,
            row.AccountId,
            row.AvitoSubProfileId,
            row.LastSuccessfulCheckAtUtc,
            row.NextCheckAtUtc,
            row.UpdatedAtUtc);
}
