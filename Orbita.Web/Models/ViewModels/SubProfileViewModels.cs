using Orbita.Contracts;
using Orbita.Web.Formatting;

namespace Orbita.Web.Models.ViewModels;

public sealed class SubProfileRowViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public bool IsEnabledInPanel { get; init; } = true;
    public string BalanceText { get; init; } = "—";
    public string? RatingText { get; init; }
    public bool HasIssue { get; init; }
    public string? IssueSummary { get; init; }
    public Guid? DiagnosticAttachmentId { get; init; }
}

public sealed class SubProfilesListViewModel
{
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public string? ProcessingSubProfileId { get; init; }
    public IReadOnlyList<SubProfileRowViewModel> Items { get; init; } = [];
}

public sealed class SubProfilesSectionViewModel
{
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public string PanelIdPrefix { get; init; } = "subprofiles";
    public bool CanRefreshSubProfiles { get; init; }
    public bool IsSubProfilesRefreshPending { get; init; }
    public bool HasSubProfiles { get; init; }
    public string? ProcessingSubProfileId { get; init; }
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
        IReadOnlyList<Orbita.Contracts.WorkerSubProfileDto>? subProfiles,
        IReadOnlyList<Orbita.Contracts.SubProfileBalanceDto>? balanceItems = null)
    {
        if (subProfiles is null || subProfiles.Count == 0)
        {
            return [];
        }

        var balanceByName = balanceItems is null || balanceItems.Count == 0
            ? null
            : balanceItems
                .Where(b => !string.IsNullOrWhiteSpace(b.SubProfileName))
                .ToDictionary(
                    b => b.SubProfileName.Trim(),
                    b => b,
                    StringComparer.OrdinalIgnoreCase);

        return subProfiles
            .Select(sp =>
            {
                var name = string.IsNullOrWhiteSpace(sp.Name) ? sp.Id : sp.Name;
                SubProfileBalanceDto? balanceItem = null;
                if (balanceByName is not null)
                {
                    balanceByName.TryGetValue(name, out balanceItem);
                }

                var advance = ResolveBalance(sp.Balance, balanceItem?.Balance);
                var wallet = ResolveBalance(sp.WalletBalance, balanceItem?.WalletBalance);
                var duration = ResolveDuration(sp.AdvanceDurationText, balanceItem?.AdvanceDurationText);
                return new SubProfileRowViewModel
            {
                Id = sp.Id,
                Name = name,
                Category = sp.Category,
                IsCurrent = sp.IsCurrent,
                IsEnabledInPanel = sp.IsEnabledInPanel,
                BalanceText = BalanceDisplay.FormatSubProfile(wallet, advance, duration),
                RatingText = RatingDisplay.FormatSubProfile(sp.Rating, sp.ReviewsCount, sp.ReviewsText),
                HasIssue = !string.IsNullOrWhiteSpace(sp.LastIssueKind),
                IssueSummary = string.IsNullOrWhiteSpace(sp.LastIssueMessage)
                    ? null
                    : sp.LastIssueMessage,
                DiagnosticAttachmentId = sp.DiagnosticAttachmentId
            };
            })
            .ToList();
    }

    public static string? BuildBalanceBreakdown(IReadOnlyList<SubProfileRowViewModel> subProfiles)
    {
        if (subProfiles.Count <= 1)
        {
            return null;
        }

        var parts = subProfiles
            .Select(s => $"{s.Name}: {s.BalanceText}")
            .ToList();

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static decimal? ResolveBalance(decimal? profileValue, decimal? balanceItemValue) =>
        profileValue ?? balanceItemValue;

    private static string? ResolveDuration(string? profileValue, string? balanceItemValue) =>
        !string.IsNullOrWhiteSpace(profileValue)
            ? profileValue
            : string.IsNullOrWhiteSpace(balanceItemValue) ? null : balanceItemValue;

    public static IReadOnlyList<SubProfileRowViewModel> GetPreviewChips(
        IReadOnlyList<SubProfileRowViewModel> subProfiles,
        int max = 3)
    {
        if (subProfiles.Count == 0)
        {
            return [];
        }

        var enabled = subProfiles.Where(s => s.IsEnabledInPanel).ToList();
        var source = enabled.Count > 0 ? enabled : subProfiles;
        return source.Take(max).ToList();
    }

    public static int GetPreviewOverflowCount(
        IReadOnlyList<SubProfileRowViewModel> subProfiles,
        int max = 3)
    {
        if (subProfiles.Count == 0)
        {
            return 0;
        }

        var previewCount = Math.Min(GetPreviewChips(subProfiles, max).Count, subProfiles.Count);
        return Math.Max(0, subProfiles.Count - previewCount);
    }

    public static bool HasAnyIssue(IReadOnlyList<SubProfileRowViewModel> subProfiles) =>
        subProfiles.Any(s => s.HasIssue);

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