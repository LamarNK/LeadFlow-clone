using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.LocalChrome;

public static class LocalChromeLaunchOptionsFactory
{
    public static LocalChromeLaunchOptions FromAccount(AvitoAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        string? address = null;
        var useProxy = WorkerAccountRuntime.IsLocalProvider(account)
            && !string.IsNullOrWhiteSpace(account.ProxyAddress)
            && LocalChromeProxyRules.TryNormalizeAddress(account.ProxyAddress, out address, out _);

        return new LocalChromeLaunchOptions
        {
            UserDataDir = account.BrowserProfilePath,
            ExecutablePath = account.LocalChromeExecutablePath,
            AccountId = account.Id,
            ProxyEnabled = useProxy,
            ProxyServer = useProxy ? address : null,
            ProxyUsername = useProxy ? NullIfEmpty(account.ProxyUsername) : null,
            ProxyPassword = useProxy ? NullIfEmpty(account.ProxyPassword) : null
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
