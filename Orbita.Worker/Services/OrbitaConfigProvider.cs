using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaConfigProvider(
    OrbitaApiClient apiClient,
    WorkerAccountRuntimeStore runtimeStore) : IWorkerConfigProvider
{
    private WorkerConfigDto? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<WorkerMonitoringConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (_cached is null || DateTime.UtcNow - _cachedAtUtc > CacheTtl)
        {
            _cached = await apiClient.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            _cachedAtUtc = DateTime.UtcNow;
        }

        if (_cached is null)
        {
            return new WorkerMonitoringConfig();
        }

        var defaultBaseUrl = string.IsNullOrWhiteSpace(_cached.AdsPowerApiBaseUrl)
            ? "http://local.adspower.net:50325"
            : _cached.AdsPowerApiBaseUrl;

        var accounts = _cached.Accounts
            .Where(a => a.IsEnabled)
            .Select(a =>
            {
                var account = WorkerAccountConfigMapper.ToAccount(a, _cached, defaultBaseUrl);
                runtimeStore.OverlayRuntime(account);
                return account;
            })
            .ToList();

        return new WorkerMonitoringConfig
        {
            WorkerId = _cached.WorkerId,
            MaxConcurrentAccounts = _cached.MaxConcurrentAccounts,
            Accounts = accounts,
            ResponseFilters = _cached.ResponseFilters,
            MessengerAutoReply = _cached.MessengerAutoReplyEnabled
                ? new AvitoMessengerAutoReplySettings
                {
                    Enabled = true,
                    Message = _cached.MessengerAutoReplyMessage ?? AvitoMessengerAutoReplySettings.DefaultMessage
                }
                : null,
            PhoneUnchangedHours = _cached.EffectivePhoneUnchangedHours,
            RuCaptchaApiKey = string.IsNullOrWhiteSpace(_cached.RuCaptchaApiKey)
                ? null
                : _cached.RuCaptchaApiKey.Trim(),
            GeeTestDynamicContextEnabled = string.Equals(
                Environment.GetEnvironmentVariable("LEADFLOW_GEETEST_DYNAMIC_CONTEXT_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            AdsPowerEnabled = _cached.AdsPowerEnabled,
            MultiloginEnabled = _cached.MultiloginEnabled,
            LocalChromeEnabled = _cached.LocalChromeEnabled,
            PendingTopUpHistoryChecks = _cached.PendingTopUpHistoryChecks ?? []
        };
    }

    public void InvalidateCache() => _cached = null;

    public void InvalidateConfigCache() => InvalidateCache();
}
