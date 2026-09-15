using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Безопасная подготовка продления/повторной публикации без клика в браузере.</summary>
public sealed record AvitoAdRenewalPlan(
    string AvitoItemId,
    string Title,
    string Url,
    bool IsEligible,
    string Reason,
    string ActionMarker);

public static class AvitoAdRenewalPlanner
{
    public static AvitoAdRenewalPlan Prepare(AvitoAdStatus ad)
    {
        ArgumentNullException.ThrowIfNull(ad);
        var isUnpublished = string.Equals(ad.SourceTab, AvitoAdStatus.UnpublishedTab, StringComparison.Ordinal);
        if (!isUnpublished)
        {
            return new(ad.Id, ad.Title, ad.Url, false, "listing_is_not_unpublished", string.Empty);
        }

        if (!ad.CanPublish)
        {
            return new(ad.Id, ad.Title, ad.Url, false, "publish_action_not_available", string.Empty);
        }

        return new(ad.Id, ad.Title, ad.Url, true, "ready_for_explicit_publish", "publish-action");
    }

    public static IReadOnlyList<AvitoAdRenewalPlan> PrepareMany(IEnumerable<AvitoAdStatus> ads) =>
        (ads ?? []).Select(Prepare).ToList();
}
