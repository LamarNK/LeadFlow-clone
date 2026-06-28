using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Собирает общий список активных объявлений из сохранённых JSON-снимков всех аккаунтов (БД).
/// </summary>
public static class ActiveAdsSnapshotAggregator
{
    public static List<AvitoAdStatus> AggregateFromAccounts(IReadOnlyList<AvitoAccount> accounts)
    {
        var result = new List<AvitoAdStatus>();
        foreach (var account in accounts)
        {
            var ads = AvitoAdSnapshots.Deserialize(account.ActiveAdsSnapshotJson, account.Id);
            foreach (var ad in ads)
            {
                ad.AccountId = account.Id;
                result.Add(ad);
            }
        }

        return result;
    }
}
