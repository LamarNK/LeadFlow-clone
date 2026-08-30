using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PlusofonRecordingSyncService(
    OrbitaDbContext db,
    CrmTelephonyCredentialProtector credentialProtector,
    IPlusofonApiClient plusofon,
    TimeProvider timeProvider,
    ILogger<PlusofonRecordingSyncService> logger,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    private const int BatchSize = 50;
    private const int MaxAttempts = 20;

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var calls = await db.CrmCalls
            .Where(x => x.Provider == CrmTelephonyProviders.Plusofon
                && x.RecordingUrl == null
                && x.RecordingFetchAttempts < MaxAttempts
                && x.NextRecordingFetchAtUtc != null
                && x.NextRecordingFetchAtUtc <= now)
            .OrderBy(x => x.NextRecordingFetchAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (calls.Count == 0)
        {
            return 0;
        }

        var officeIds = calls.Select(x => x.OfficeId).Distinct().ToArray();
        var receivers = await db.CrmTelephonyWebhooks.AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                && x.Provider == CrmTelephonyProviders.Plusofon
                && x.IsEnabled)
            .ToDictionaryAsync(x => x.OfficeId, ct);
        var accountIds = calls.Where(x => x.ProviderAccountId != null)
            .Select(x => x.ProviderAccountId!.Value)
            .Distinct()
            .ToArray();
        var providerAccounts = await db.CrmTelephonyProviderAccounts.AsNoTracking()
            .Where(x => accountIds.Contains(x.Id) && x.IsEnabled)
            .ToDictionaryAsync(x => x.Id, ct);
        var updated = 0;
        foreach (var call in calls)
        {
            string? clientId;
            string? protectedToken;
            if (call.ProviderAccountId is Guid accountId)
            {
                if (!providerAccounts.TryGetValue(accountId, out var providerAccount))
                {
                    call.NextRecordingFetchAtUtc = now.AddMinutes(10);
                    continue;
                }
                clientId = providerAccount.ExternalAccountId;
                protectedToken = providerAccount.AccessTokenProtected;
            }
            else if (receivers.TryGetValue(call.OfficeId, out var receiver))
            {
                clientId = receiver.ProviderClientId;
                protectedToken = receiver.ProviderAccessTokenProtected;
            }
            else
            {
                clientId = null;
                protectedToken = null;
            }

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(protectedToken))
            {
                call.NextRecordingFetchAtUtc = now.AddMinutes(10);
                continue;
            }

            string accessToken;
            try
            {
                accessToken = credentialProtector.Unprotect(protectedToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Cannot decrypt Plusofon credentials for office {OfficeId}.",
                    call.OfficeId);
                call.RecordingFetchAttempts = MaxAttempts;
                call.NextRecordingFetchAtUtc = null;
                continue;
            }

            var result = await plusofon.GetRecordingAsync(
                clientId,
                accessToken,
                call.ExternalCallId,
                ct);
            call.RecordingFetchAttempts++;
            call.UpdatedAtUtc = now;
            switch (result.Outcome)
            {
                case PlusofonRecordingOutcome.Ready:
                    call.RecordingUrl = result.RecordingUrl;
                    call.NextRecordingFetchAtUtc = null;
                    call.NextRecordingArchiveAtUtc = now;
                    updated++;
                    if (call.CardId is not null)
                    {
                        panelRealtime?.Notify([PanelChangeKind.Crm], call.OfficeId);
                    }
                    break;
                case PlusofonRecordingOutcome.Unauthorized:
                    call.RecordingFetchAttempts = MaxAttempts;
                    call.NextRecordingFetchAtUtc = null;
                    logger.LogWarning(
                        "Plusofon rejected recording credentials for office {OfficeId}.",
                        call.OfficeId);
                    break;
                default:
                    call.NextRecordingFetchAtUtc = now.Add(GetRetryDelay(call.RecordingFetchAttempts));
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return updated;
    }

    private static TimeSpan GetRetryDelay(int attempt) => attempt switch
    {
        <= 2 => TimeSpan.FromSeconds(30),
        <= 5 => TimeSpan.FromMinutes(1),
        <= 10 => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromMinutes(10)
    };
}

public sealed class PlusofonRecordingHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlusofonRecordingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<PlusofonRecordingSyncService>()
                    .ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Plusofon recording synchronization failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
