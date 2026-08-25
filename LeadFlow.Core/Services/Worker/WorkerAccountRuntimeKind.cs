using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public enum WorkerAccountRuntimeKind
{
    Legacy = 0,
    AdsPower = 1,
    Multilogin = 2
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

        return WorkerAccountRuntimeKind.Legacy;
    }

    public static string MonitorProfileId(AvitoAccount account) =>
        IsMultiloginProvider(account)
            ? account.MultiloginProfileId ?? string.Empty
            : account.AdsPowerProfileId ?? string.Empty;
}
