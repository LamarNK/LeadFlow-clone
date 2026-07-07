using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class BalanceSnapshotHelper
{
    public static bool HasMeaningfulBalanceData(
        WorkerBalanceDto? balance,
        WorkerAccountDto? account = null)
    {
        if (balance is null)
        {
            return false;
        }

        if (balance.TotalBalance > 0 || balance.TotalWalletBalance > 0)
        {
            return true;
        }

        if (balance.SubProfiles.Any(static s => HasMeaningfulBalanceSubProfile(s)))
        {
            return true;
        }

        if (balance.SubProfiles.Count > 0
            && balance.SubProfiles.All(static s => IsPlaceholderBalanceSubProfile(s)))
        {
            return false;
        }

        if (account?.SubProfiles?.Any(static s => s.Balance.HasValue || s.WalletBalance.HasValue) == true)
        {
            return true;
        }

        return false;
    }

    public static WorkerBalanceDto? FromWorkerAccount(WorkerAccountEntity account)
    {
        var subProfiles = SubProfileDeserializer.Deserialize(account.SubProfilesJson);
        if (subProfiles is null || subProfiles.Count == 0)
        {
            return account.TotalBalance > 0
                ? new WorkerBalanceDto(account.AccountId, account.DisplayName, account.TotalBalance, [])
                : null;
        }

        var items = subProfiles
            .Select(static sp => new SubProfileBalanceDto(
                string.IsNullOrWhiteSpace(sp.Name) ? sp.Id : sp.Name,
                sp.Balance,
                sp.WalletBalance,
                sp.AdvanceDurationText))
            .ToList();

        var total = account.TotalBalance > 0
            ? account.TotalBalance
            : items.Sum(static x => x.Balance ?? 0m);
        var wallet = items.Sum(static x => x.WalletBalance ?? 0m);
        if (total <= 0
            && wallet <= 0
            && !items.Any(static x => x.Balance.HasValue || x.WalletBalance.HasValue))
        {
            return null;
        }

        return new WorkerBalanceDto(account.AccountId, account.DisplayName, total, items, wallet);
    }

    public static IReadOnlyList<WorkerBalanceDto> MergeWithPersisted(
        IReadOnlyList<WorkerBalanceDto> incoming,
        IReadOnlyList<WorkerBalanceDto>? previousSnapshot,
        IReadOnlyDictionary<Guid, WorkerAccountEntity> existingAccounts,
        IReadOnlyList<WorkerAccountDto>? incomingAccounts = null)
    {
        var previousByAccount = (previousSnapshot ?? []).ToDictionary(static x => x.AccountId);
        var accountsById = (incomingAccounts ?? []).ToDictionary(static x => x.AccountId);
        var result = new List<WorkerBalanceDto>(incoming.Count);

        foreach (var item in incoming)
        {
            accountsById.TryGetValue(item.AccountId, out var accountDto);

            if (HasMeaningfulBalanceData(item, accountDto))
            {
                result.Add(item);
                continue;
            }

            if (previousByAccount.TryGetValue(item.AccountId, out var previous)
                && HasMeaningfulBalanceData(previous, accountDto))
            {
                result.Add(previous with { AccountName = item.AccountName });
                continue;
            }

            if (existingAccounts.TryGetValue(item.AccountId, out var persisted)
                && FromWorkerAccount(persisted) is { } fromAccount)
            {
                result.Add(fromAccount with { AccountName = item.AccountName });
                continue;
            }
        }

        return result;
    }

    private static bool HasMeaningfulBalanceSubProfile(SubProfileBalanceDto item) =>
        item.Balance.HasValue || item.WalletBalance.HasValue;

    private static bool IsPlaceholderBalanceSubProfile(SubProfileBalanceDto item) =>
        !HasMeaningfulBalanceSubProfile(item)
        && SubProfileSnapshotHelper.IsPlaceholderToken(item.SubProfileName);

    public static decimal ResolveTotalBalance(
        decimal incomingTotal,
        WorkerAccountEntity existing,
        WorkerBalanceDto? balanceDto,
        WorkerAccountDto? accountDto) =>
        HasMeaningfulBalanceData(balanceDto, accountDto)
            ? incomingTotal
            : existing.TotalBalance;

    public static bool ShouldPersistSubProfiles(
        IReadOnlyList<WorkerSubProfileDto> incoming,
        string? existingJson) =>
        SubProfileSnapshotHelper.ShouldPersistSubProfiles(incoming, existingJson);
}