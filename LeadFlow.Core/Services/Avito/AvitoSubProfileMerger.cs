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
        if (discovered.Count == 0)
        {
            return existing;
        }

        var byId = existing.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var result = new List<AvitoSubProfile>(discovered.Count);

        foreach (var item in discovered)
        {
            if (byId.TryGetValue(item.Id, out var previous))
            {
                result.Add(new AvitoSubProfile
                {
                    Id = item.Id,
                    Name = item.Name,
                    Category = item.Category,
                    IsCurrent = item.IsCurrent,
                    Balance = previous.Balance,
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