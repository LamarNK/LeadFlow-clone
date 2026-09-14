using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Persists one provider request and serves the statistics projection.</summary>
public sealed class CaptchaProviderRequestService(OrbitaDbContext db, OfficeScopeService officeScope)
{
    public async Task<Guid?> CreateAsync(Guid workerId, CaptchaProviderRequestCreateDto request, CancellationToken ct)
    {
        var accountBelongsToWorker = await db.WorkerAccounts.AnyAsync(
            x => x.WorkerId == workerId && x.AccountId == request.AccountId,
            ct);
        if (!accountBelongsToWorker)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new CaptchaProviderRequestEntity
        {
            Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            WorkerId = workerId,
            AccountId = request.AccountId,
            CycleRunId = request.CycleRunId,
            SubProfileRunId = request.SubProfileRunId,
            SubProfileId = request.SubProfileId,
            SubProfileName = request.SubProfileName,
            Provider = request.Provider,
            CaptchaType = request.CaptchaType,
            Stage = request.Stage,
            Reason = request.Reason,
            Attempt = Math.Max(1, request.Attempt),
            MaxAttempts = Math.Max(1, request.MaxAttempts),
            ProviderStatus = "submitted",
            TargetStatus = "pending",
            PageUrl = request.PageUrl,
            DiagnosticAttachmentId = request.DiagnosticAttachmentId,
            SubmittedAtUtc = request.SubmittedAtUtc == default ? now : request.SubmittedAtUtc,
            UpdatedAtUtc = now
        };

        db.CaptchaProviderRequests.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.Id;
    }

    public async Task<bool> MarkProviderAcceptedAsync(Guid workerId, CaptchaProviderRequestProviderResultDto result, CancellationToken ct)
    {
        var entity = await ForWorker(workerId).FirstOrDefaultAsync(x => x.Id == result.Id, ct);
        if (entity is null) return false;
        entity.ProviderStatus = string.IsNullOrWhiteSpace(result.ProviderStatus) ? "accepted" : result.ProviderStatus;
        entity.ProviderTaskId = result.ProviderTaskId;
        entity.ErrorCode = result.ErrorCode;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MarkTargetOutcomeAsync(Guid workerId, CaptchaProviderRequestTargetResultDto result, CancellationToken ct)
    {
        var entity = await ForWorker(workerId).FirstOrDefaultAsync(x => x.Id == result.Id, ct);
        if (entity is null) return false;
        entity.TargetStatus = result.TargetStatus;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<CaptchaProviderStatisticsDto> GetStatisticsAsync(
        OfficeScope scope,
        DateTime? from,
        DateTime? to,
        IReadOnlyList<Guid>? workerIds,
        IReadOnlyList<Guid>? accountIds,
        int? timeZoneOffsetMinutes,
        CancellationToken ct)
    {
        var fromUtc = ToUtcBoundary(from, timeZoneOffsetMinutes, DateTime.MinValue);
        var toUtc = ToUtcBoundary(to?.Date.AddDays(1), timeZoneOffsetMinutes, DateTime.MaxValue);
        var workerQuery = officeScope.ApplyWorkerFilter(db.Workers.AsNoTracking(), scope);
        var workerSet = workerQuery.Select(w => w.Id);
        var query = db.CaptchaProviderRequests.AsNoTracking()
            .Where(x => workerSet.Contains(x.WorkerId) && x.SubmittedAtUtc >= fromUtc && x.SubmittedAtUtc < toUtc);
        if (workerIds is { Count: > 0 }) query = query.Where(x => workerIds.Contains(x.WorkerId));
        if (accountIds is { Count: > 0 }) query = query.Where(x => accountIds.Contains(x.AccountId));

        var rows = await query.OrderByDescending(x => x.SubmittedAtUtc).ToListAsync(ct).ConfigureAwait(false);
        var workers = await workerQuery.Select(w => new { w.Id, w.DisplayName }).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var accounts = await db.WorkerAccounts.AsNoTracking()
            .Where(x => rows.Select(r => r.AccountId).Contains(x.AccountId) && workers.Keys.Contains(x.WorkerId))
            .Select(x => new { x.WorkerId, x.AccountId, x.DisplayName })
            .ToListAsync(ct).ConfigureAwait(false);

        var grouped = rows.GroupBy(x => new { x.WorkerId, x.AccountId })
            .Select(group => new CaptchaProviderWorkerStatisticsDto(
                group.Key.WorkerId,
                workers.GetValueOrDefault(group.Key.WorkerId)?.DisplayName ?? group.Key.WorkerId.ToString("D"),
                group.Key.AccountId,
                accounts.FirstOrDefault(x => x.WorkerId == group.Key.WorkerId && x.AccountId == group.Key.AccountId)?.DisplayName ?? group.Key.AccountId.ToString("D"),
                group.Count(),
                group.Count(x => x.ProviderStatus == "accepted"),
                group.Count(x => x.ProviderStatus == "no_slot"),
                group.Count(x => x.ProviderStatus == "error"),
                group.Count(x => x.TargetStatus == "accepted"),
                group.Count(x => x.TargetStatus == "rejected"),
                group.Select(Map).ToList()))
            .OrderBy(x => x.WorkerName).ThenBy(x => x.AccountName).ToList();

        return new CaptchaProviderStatisticsDto(
            rows.Count,
            rows.Count(x => x.ProviderStatus == "accepted"),
            rows.Count(x => x.ProviderStatus == "no_slot"),
            rows.Count(x => x.ProviderStatus == "error"),
            rows.Count(x => x.TargetStatus == "accepted"),
            rows.Count(x => x.TargetStatus == "rejected"),
            grouped);
    }

    private IQueryable<CaptchaProviderRequestEntity> ForWorker(Guid workerId) =>
        db.CaptchaProviderRequests.Where(x => x.WorkerId == workerId);

    private static CaptchaProviderRequestDetailsDto Map(CaptchaProviderRequestEntity x) => new(
        x.Id, x.WorkerId, x.AccountId, x.CycleRunId, x.SubProfileRunId, x.SubProfileId,
        x.SubProfileName, x.Provider, x.CaptchaType, x.Stage, x.Reason, x.Attempt,
        x.MaxAttempts, x.ProviderStatus, x.TargetStatus, x.ProviderTaskId, x.ErrorCode,
        x.PageUrl, x.DiagnosticAttachmentId, x.SubmittedAtUtc, x.UpdatedAtUtc);

    private static DateTime ToUtcBoundary(DateTime? value, int? offsetMinutes, DateTime fallback)
    {
        if (value is null) return fallback;
        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        return local.AddMinutes(-(offsetMinutes ?? 0));
    }
}
