using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

internal static class AvitoAccountRuntimeStateSync
{
    public static void MergePersistedIntoAccounts(
        IReadOnlyList<AvitoAccount> targetAccounts,
        IReadOnlyList<AvitoAccount> persistedAccounts)
    {
        var persistedById = persistedAccounts.ToDictionary(static account => account.Id);
        foreach (var target in targetAccounts)
        {
            if (persistedById.TryGetValue(target.Id, out var persisted))
            {
                target.MergePersistedSnapshotFrom(persisted);
            }
        }
    }
}
