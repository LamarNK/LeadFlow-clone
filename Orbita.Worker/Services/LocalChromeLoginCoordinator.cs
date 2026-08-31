using LeadFlow.Core.Models;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class LocalChromeLoginCoordinator(
    ILocalChromeBrowserLauncher launcher,
    LocalChromeAccountLock accountLock,
    WorkerAccountRuntimeStore runtimeStore,
    OrbitaConfigProvider configProvider,
    OrbitaApiClient apiClient,
    IWorkerRealtimeChannel realtime)
{
    private readonly LocalChromeLoginSessionRunner _runner = new(launcher, accountLock);
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public bool IsLoginOpen(Guid accountId) =>
        accountLock.IsHeld(accountId, LocalChromeAccountLock.Login);

    public async Task TryRunPendingSessionAsync(
        WorkerPendingLocalChromeLoginDto pending,
        WorkerConfigDto config,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var account = ResolveAccount(pending.AccountId, config);
            if (account is null)
            {
                await CompleteQuietlyAsync(pending.SessionId, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await _runner.RunAsync(account, cancellationToken).ConfigureAwait(false);
                MarkReadyAfterLogin(account);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Path and launch details stay off worker logs.
            }
            finally
            {
                await CompleteQuietlyAsync(pending.SessionId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            realtime.RequestWake();
        }
    }

    private AvitoAccount? ResolveAccount(Guid accountId, WorkerConfigDto config)
    {
        var dto = config.Accounts.FirstOrDefault(x => x.AccountId == accountId);
        if (dto is null)
        {
            return null;
        }

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, config.AdsPowerApiBaseUrl ?? string.Empty);
        runtimeStore.OverlayRuntime(account);
        return account;
    }

    private void MarkReadyAfterLogin(AvitoAccount account)
    {
        account.Status = AvitoAccountStatus.Authorized;
        account.LastAuthCheckAt = DateTime.UtcNow;
        account.LastErrorMessage = string.Empty;
        runtimeStore.Upsert(account);
        configProvider.InvalidateCache();
    }

    private async Task CompleteQuietlyAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await apiClient
                .CompleteLocalChromeLoginAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Next config poll will drop an expired session.
        }
    }
}
