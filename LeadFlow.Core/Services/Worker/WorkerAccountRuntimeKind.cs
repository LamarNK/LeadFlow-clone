using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public enum WorkerAccountRuntimeKind
{
    Legacy = 0,
    AdsPower = 1,
    Multilogin = 2,
    Local = 3
}

public static class WorkerAccountRuntime
{
    public static bool IsAdsPower(AvitoAccount account) =>
        account.ProfileProvider == AvitoProfileProvider.AdsPower
        && !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
        && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

    public static bool IsMultiloginProvider(AvitoAccount account) =>
        account.ProfileProvider == AvitoProfileProvider.Multilogin;

    public static bool IsMultilogin(AvitoAccount account) =>
        IsMultiloginProvider(account)
        && !string.IsNullOrWhiteSpace(account.MultiloginProfileId)
        && !string.IsNullOrWhiteSpace(account.MultiloginFolderId)
        && !string.IsNullOrWhiteSpace(account.MultiloginLauncherUrl)
        && !string.IsNullOrWhiteSpace(account.MultiloginAutomationToken);

    public static bool IsLocalProvider(AvitoAccount account) =>
        account.ProfileProvider == AvitoProfileProvider.Local;

    public static bool IsLocal(AvitoAccount account) =>
        IsLocalProvider(account)
        && !string.IsNullOrWhiteSpace(account.BrowserProfilePath);

    public static WorkerAccountRuntimeKind Resolve(AvitoAccount account)
    {
        if (IsMultiloginProvider(account))
        {
            return WorkerAccountRuntimeKind.Multilogin;
        }

        if (IsAdsPower(account))
        {
            return WorkerAccountRuntimeKind.AdsPower;
        }

        if (IsLocalProvider(account))
        {
            return WorkerAccountRuntimeKind.Local;
        }

        return WorkerAccountRuntimeKind.Legacy;
    }

    public static string MonitorProfileId(AvitoAccount account)
    {
        if (IsMultiloginProvider(account))
        {
            return account.MultiloginProfileId ?? string.Empty;
        }

        if (IsLocalProvider(account))
        {
            return account.Id.ToString("D");
        }

        return account.AdsPowerProfileId ?? string.Empty;
    }

    public static bool IsBrowserProviderEnabled(
        AvitoAccount account,
        bool adsPowerEnabled,
        bool multiloginEnabled,
        bool localChromeEnabled) =>
        Resolve(account) switch
        {
            WorkerAccountRuntimeKind.Multilogin => multiloginEnabled,
            WorkerAccountRuntimeKind.Local => localChromeEnabled,
            WorkerAccountRuntimeKind.AdsPower => adsPowerEnabled,
            _ => false
        };
}
