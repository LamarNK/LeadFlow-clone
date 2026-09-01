using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class TelephonyProviderAccountSyncService(
    OrbitaDbContext db,
    CrmTelephonyCredentialProtector credentialProtector,
    IPlusofonApiClient plusofon,
    CrmTelephonyService telephony,
    TimeProvider timeProvider,
    ILogger<TelephonyProviderAccountSyncService> logger)
{
    private const int MaximumPagesPerRun = 100;
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(1);
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromMinutes(5);

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var accounts = await db.CrmTelephonyProviderAccounts
            .Where(x => x.Provider == CrmTelephonyProviders.Plusofon
                && x.IsEnabled
                && x.ExternalAccountId != null
                && x.AccessTokenProtected != null)
            .OrderBy(x => x.LastSyncedAtUtc)
            .ToListAsync(ct);
        var imported = 0;
        foreach (var account in accounts)
        {
            if (account.SyncStatus is "unauthorized" or "error"
                && account.LastSyncedAtUtc is DateTime lastAttempt
                && lastAttempt > now.AddMinutes(-5))
            {
                continue;
            }

            string token;
            try
            {
                token = credentialProtector.Unprotect(account.AccessTokenProtected!);
            }
            catch (CryptographicException ex)
            {
                SetFailure(account, now, "error", "Не удалось расшифровать Access Token.");
                logger.LogWarning(ex, "Cannot decrypt Plusofon account {AccountId} credentials.", account.Id);
                continue;
            }

            var cursor = account.SyncCursorUtc ?? account.SyncFromUtc;
            var windowFrom = cursor == account.SyncFromUtc ? cursor : cursor.Subtract(CursorOverlap);
            var windowTo = new[] { windowFrom.Add(MaximumWindow), now }.Min();
            if (windowTo <= windowFrom)
            {
                account.LastSyncedAtUtc = now;
                account.SyncStatus = "online";
                account.LastSyncError = null;
                continue;
            }

            var completed = true;
            for (var page = 1; page <= MaximumPagesPerRun; page++)
            {
                var result = await plusofon.GetCallsAsync(
                    account.ExternalAccountId!, token, windowFrom, windowTo, page, ct);
                if (result.Outcome == PlusofonRecordingOutcome.Unauthorized)
                {
                    SetFailure(account, now, "unauthorized", "Плюсофон отклонил Client ID или Access Token.");
                    completed = false;
                    break;
                }
                if (result.Outcome is PlusofonRecordingOutcome.TransientFailure or PlusofonRecordingOutcome.NotReady)
                {
                    SetFailure(account, now, "error", "Плюсофон временно не вернул историю звонков.");
                    completed = false;
                    break;
                }

                foreach (var call in result.Calls)
                {
                    var receive = await telephony.ReceiveProviderAccountCallAsync(
                        account.Id,
                        new SipoutCallWebhookPayload(
                            call.CallId,
                            call.NumberA,
                            call.NumberB,
                            call.Direction,
                            call.ProviderUserKey,
                            call.FinalNumber,
                            call.ConnectedAt,
                            call.DurationSeconds,
                            call.RecordingUrl),
                        ct);
                    if (receive.Outcome is SipoutCallReceiveOutcome.Accepted or SipoutCallReceiveOutcome.Unmatched)
                    {
                        imported++;
                    }
                }

                if (!result.HasMore) break;
                if (page == MaximumPagesPerRun)
                {
                    SetFailure(account, now, "error", "Превышен лимит страниц истории за один проход.");
                    completed = false;
                }
            }

            if (completed)
            {
                account.SyncCursorUtc = windowTo;
                account.LastSyncedAtUtc = now;
                account.SyncStatus = "online";
                account.LastSyncError = null;
                account.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await telephony.ReconcileUnmatchedCallsAsync(ct);
        return imported;
    }

    private static void SetFailure(
        CrmTelephonyProviderAccountEntity account,
        DateTime now,
        string status,
        string message)
    {
        account.LastSyncedAtUtc = now;
        account.SyncStatus = status;
        account.LastSyncError = message;
        account.UpdatedAtUtc = now;
    }
}

public sealed class TelephonyProviderAccountSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<TelephonyProviderAccountSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TelephonyProviderAccountSyncService>()
                    .ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telephony provider account synchronization failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
