namespace Orbita.Web.Models.ViewModels;

public sealed class SubProfileRowViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public bool IsEnabledInPanel { get; init; } = true;
    public string BalanceText { get; init; } = "—";
    public bool HasIssue { get; init; }
    public string? IssueSummary { get; init; }
    public Guid? DiagnosticAttachmentId { get; init; }
}

public sealed class SubProfilesListViewModel
{
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public IReadOnlyList<SubProfileRowViewModel> Items { get; init; } = [];
}

public static class SubProfileViewModelMapper
{
    public static bool IsRefreshPending(
        DateTime? requestedAtUtc,
        DateTime? refreshedAtUtc) =>
        requestedAtUtc is not null
        && (refreshedAtUtc is null || requestedAtUtc > refreshedAtUtc);
    public static IReadOnlyList<SubProfileRowViewModel> Map(
        IReadOnlyList<Orbita.Contracts.WorkerSubProfileDto>? subProfiles)
    {
        if (subProfiles is null || subProfiles.Count == 0)
        {
            return [];
        }

        return subProfiles
            .Select(sp => new SubProfileRowViewModel
            {
                Id = sp.Id,
                Name = string.IsNullOrWhiteSpace(sp.Name) ? sp.Id : sp.Name,
                Category = sp.Category,
                IsCurrent = sp.IsCurrent,
                IsEnabledInPanel = sp.IsEnabledInPanel,
                BalanceText = sp.Balance.HasValue ? $"{sp.Balance.Value:N0} ₽" : "—",
                HasIssue = !string.IsNullOrWhiteSpace(sp.LastIssueKind),
                IssueSummary = string.IsNullOrWhiteSpace(sp.LastIssueMessage)
                    ? null
                    : sp.LastIssueMessage,
                DiagnosticAttachmentId = sp.DiagnosticAttachmentId
            })
            .ToList();
    }

    public static string BuildSummary(IReadOnlyList<SubProfileRowViewModel> subProfiles)
    {
        if (subProfiles.Count == 0)
        {
            return string.Empty;
        }

        if (subProfiles.Count == 1)
        {
            return subProfiles[0].Name;
        }

        var enabled = subProfiles.Where(s => s.IsEnabledInPanel).ToList();
        if (enabled.Count == 0)
        {
            return $"{subProfiles.Count} субпроф. · все выкл.";
        }

        if (enabled.Count <= 3)
        {
            var names = string.Join(" · ", enabled.Select(s => s.Name));
            return enabled.Count < subProfiles.Count
                ? $"{names} ({enabled.Count}/{subProfiles.Count})"
                : names;
        }

        return enabled.Count < subProfiles.Count
            ? $"{enabled.Count} / {subProfiles.Count} активны"
            : $"{subProfiles.Count} субпроф.";
    }
}