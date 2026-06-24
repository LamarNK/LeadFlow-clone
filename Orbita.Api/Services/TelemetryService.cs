using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class TelemetryService(OrbitaDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkerRegisterResponse?> RegisterAsync(WorkerRegisterRequest request, string registrationSecret, CancellationToken ct)
    {
        if (!string.Equals(request.RegistrationSecret, registrationSecret, StringComparison.Ordinal))
        {
            return null;
        }

        var apiKey = ApiKeyService.GenerateApiKey();
        var worker = new WorkerEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = request.DisplayName.Trim(),
            MachineName = request.MachineName.Trim(),
            AppVersion = request.AppVersion.Trim(),
            ApiKeyHash = ApiKeyService.HashApiKey(apiKey),
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };

        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
        return new WorkerRegisterResponse(worker.Id, apiKey);
    }

    public async Task<bool> HeartbeatAsync(WorkerHeartbeatRequest request, CancellationToken ct)
    {
        var worker = await db.Workers.FindAsync([request.WorkerId], ct);
        if (worker is null)
        {
            return false;
        }

        worker.DisplayName = request.DisplayName.Trim();
        worker.MachineName = request.MachineName.Trim();
        worker.AppVersion = request.AppVersion.Trim();
        worker.MonitoringStatus = request.MonitoringStatus;
        worker.MonitoringStatusMessage = request.MonitoringStatusMessage;
        worker.IsMonitoringActive = request.IsMonitoringActive;
        worker.NextCycleCheckAtUtc = request.NextCycleCheckAtUtc;
        worker.LastSeenAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SaveSnapshotAsync(WorkerSnapshotRequest request, CancellationToken ct)
    {
        var worker = await db.Workers.FindAsync([request.WorkerId], ct);
        if (worker is null)
        {
            return false;
        }

        worker.LastSeenAtUtc = DateTime.UtcNow;
        worker.MonitoringStatus = worker.MonitoringStatus;

        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = request.WorkerId,
            CapturedAtUtc = request.CapturedAtUtc,
            StatsJson = JsonSerializer.Serialize(request.Stats, JsonOptions),
            BalancesJson = JsonSerializer.Serialize(request.Balances, JsonOptions)
        });

        var balanceByAccount = request.Balances.ToDictionary(x => x.AccountId, x => x.TotalBalance);
        var existingAccounts = await db.WorkerAccounts
            .Where(x => x.WorkerId == request.WorkerId)
            .ToDictionaryAsync(x => x.AccountId, ct);

        foreach (var account in request.Accounts)
        {
            balanceByAccount.TryGetValue(account.AccountId, out var balance);
            if (existingAccounts.TryGetValue(account.AccountId, out var existing))
            {
                existing.DisplayName = account.DisplayName;
                existing.Status = account.Status;
                existing.IsEnabled = account.IsEnabled;
                existing.ActiveAdsCount = account.ActiveAdsCount;
                existing.BlockedCount = account.BlockedCount;
                existing.DraftsCount = account.DraftsCount;
                existing.LastErrorMessage = account.LastErrorMessage;
                existing.LastMonitoringAt = account.LastMonitoringAt;
                existing.TotalBalance = balance;
                existing.UpdatedAtUtc = request.CapturedAtUtc;
            }
            else
            {
                db.WorkerAccounts.Add(new WorkerAccountEntity
                {
                    WorkerId = request.WorkerId,
                    AccountId = account.AccountId,
                    DisplayName = account.DisplayName,
                    Status = account.Status,
                    IsEnabled = account.IsEnabled,
                    ActiveAdsCount = account.ActiveAdsCount,
                    BlockedCount = account.BlockedCount,
                    DraftsCount = account.DraftsCount,
                    LastErrorMessage = account.LastErrorMessage,
                    LastMonitoringAt = account.LastMonitoringAt,
                    TotalBalance = balance,
                    UpdatedAtUtc = request.CapturedAtUtc
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SaveEventsAsync(WorkerEventBatchRequest request, CancellationToken ct)
    {
        var exists = await db.Workers.AnyAsync(x => x.Id == request.WorkerId, ct);
        if (!exists)
        {
            return false;
        }

        foreach (var evt in request.Events)
        {
            db.WorkerEvents.Add(new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = request.WorkerId,
                AccountId = evt.AccountId,
                Level = evt.Level,
                Message = evt.Message,
                Details = evt.Details,
                CreatedAtUtc = evt.CreatedAtUtc
            });
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}