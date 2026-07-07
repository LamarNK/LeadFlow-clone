using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Сливает свежий список субпрофилей из Avito с сохранённым состоянием (баланс, ошибки).
/// </summary>
public static class AvitoSubProfileMerger
{
    public static IReadOnlyList<AvitoSubProfile> Merge(
        IReadOnlyList<AvitoSubProfile> existing,
        IReadOnlyList<AvitoSubProfile> discovered)
    {
        var validDiscovered = AvitoSubProfileRules.FilterValid(discovered);
        if (validDiscovered.Count == 0)
        {
            return AvitoSubProfileRules.FilterValid(existing);
        }

        var byId = AvitoSubProfileRules.IndexById(existing);
        var result = new List<AvitoSubProfile>(validDiscovered.Count);

        foreach (var item in validDiscovered)
        {
            var id = item.Id.Trim();
            if (byId.TryGetValue(id, out var previous))
            {
                result.Add(new AvitoSubProfile
                {
                    Id = id,
                    Name = item.Name,
                    Category = item.Category,
                    IsCurrent = item.IsCurrent,
                    Balance = previous.Balance,
                    WalletBalance = previous.WalletBalance,
                    AdvanceDurationText = previous.AdvanceDurationText,
                    Rating = previous.Rating,
                    ReviewsCount = previous.ReviewsCount,
                    ReviewsText = previous.ReviewsText,
                    LastIssueKind = previous.LastIssueKind,
                    LastIssueMessage = previous.LastIssueMessage,
                    LastIssueAt = previous.LastIssueAt,
                    LastDiagnosticAttachmentId = previous.LastDiagnosticAttachmentId
                });
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }
}