using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Services;

namespace Orbita.Web.Models.ViewModels;

public sealed class SubProfileRowViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public bool IsEnabledInPanel { get; init; } = true;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "success";
    public string BalanceText { get; init; } = "—";
    public decimal? Balance { get; init; }
    public bool CanTopUp { get; init; }
    public string? RatingText { get; init; }
    public int Responses { get; init; }
    public int UniqueResponses { get; init; }
    public int Errors { get; init; }
    public string? ResponsesLink { get; init; }
    public string? UniqueResponsesLink { get; init; }
    public string? ErrorsLink { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public bool IsProcessingNow { get; init; }
    public string? ProcessingLabel { get; init; }
    public string ProcessingTone { get; init; } = "live";
    public bool HasIssue { get; init; }
    public string? IssueSummary { get; init; }
    public Guid? DiagnosticAttachmentId { get; init; }
}

public enum SubProfileTableLayout
{
    Accounts,
    WorkerDetails
}

public sealed class SubProfileTableRowsViewModel
{
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public string PanelId { get; init; } = string.Empty;
    public string? ProcessingSubProfileId { get; init; }
    public SubProfileTableLayout Layout { get; init; }
    public bool ShowOfficeColumn { get; init; }
    public IReadOnlyList<SubProfileRowViewModel> Items { get; init; } = [];
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

    public static IReadOnlyList<SubProfileRowViewModel> MapFromBalances(
        IReadOnlyList<SubProfileBalanceDto>? balanceItems)
    {
        if (balanceItems is null || balanceItems.Count == 0)
        {
            return [];
        }

        return balanceItems
            .Select((item, index) =>
            {
                var name = string.IsNullOrWhiteSpace(item.SubProfileName)
                    ? balanceItems.Count == 1 ? "Субпрофиль" : $"Субпрофиль {index + 1}"
                    : item.SubProfileName.Trim();

                return new SubProfileRowViewModel
                {
                    Id = name,
                    Name = name,
                    BalanceText = BalanceDisplay.FormatSubProfile(
                        item.WalletBalance,
                        item.Balance,
                        item.AdvanceDurationText)
                };
            })
            .ToList();
    }

    public static IReadOnlyList<SubProfileRowViewModel> Map(
        IReadOnlyList<WorkerSubProfileDto>? subProfiles,
        IReadOnlyList<SubProfileBalanceDto>? balanceItems = null,
        Guid? workerId = null,
        Guid? accountId = null,
        bool workerIsOnline = false,
        WorkerActivityDto? workerActivity = null,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null)
    {
        if (subProfiles is null || subProfiles.Count == 0)
        {
            return MapFromBalances(balanceItems);
        }

        return subProfiles
            .Select((sp, index) =>
            {
                var id = sp.Id?.Trim() ?? string.Empty;
                var name = string.IsNullOrWhiteSpace(sp.Name) ? id : sp.Name.Trim();
                var balanceItem = ResolveBalanceItem(balanceItems, index, name);

                if (string.IsNullOrWhiteSpace(name)
                    && !string.IsNullOrWhiteSpace(balanceItem?.SubProfileName))
                {
                    name = balanceItem.SubProfileName.Trim();
                }

                if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    id = name;
                }

                var advance = ResolveBalance(sp.Balance, balanceItem?.Balance);
                var wallet = ResolveBalance(sp.WalletBalance, balanceItem?.WalletBalance);
                var duration = ResolveDuration(sp.AdvanceDurationText, balanceItem?.AdvanceDurationText);
                var hasIssue = !string.IsNullOrWhiteSpace(sp.LastIssueKind);
                var responses = sp.TodayResponses;
                var uniqueResponses = Math.Max(0, responses - sp.TodayDuplicates);
                var errors = sp.TodayEventErrors > 0
                    ? sp.TodayEventErrors
                    : hasIssue ? 1 : 0;
                var processing = accountId.HasValue
                    ? WorkerActivityPresenter.PresentForSubProfile(
                        workerActivity,
                        workerIsOnline,
                        accountId.Value,
                        id,
                        activeAccounts)
                    : new AccountProcessingViewModel();
                var (statusLabel, statusTone) = SubProfileStatusMapper.ForDto(sp);
                var metricLinks = workerId is Guid wid && accountId is Guid aid
                    ? AccountMetricLinks.Hrefs(wid, aid)
                    : null;
                return new SubProfileRowViewModel
                {
                    Id = id,
                    Name = name,
                    Category = sp.Category,
                    IsCurrent = sp.IsCurrent,
                    IsEnabledInPanel = sp.IsEnabledInPanel,
                    StatusLabel = statusLabel,
                    StatusTone = statusTone,
                    BalanceText = BalanceDisplay.FormatSubProfile(wallet, advance, duration),
                    Balance = advance,
                    CanTopUp = !string.IsNullOrWhiteSpace(sp.Id)
                               && advance is decimal balance
                               && balance < TopUpSessionRules.LowBalanceThresholdRub,
                    RatingText = RatingDisplay.FormatSubProfile(sp.Rating, sp.ReviewsCount, sp.ReviewsText),
                    Responses = responses,
                    UniqueResponses = uniqueResponses,
                    Errors = errors,
                    ResponsesLink = metricLinks?.Responses,
                    UniqueResponsesLink = metricLinks?.UniqueResponses,
                    ErrorsLink = metricLinks?.Errors,
                    LastActivityUtc = sp.LastActivityUtc,
                    IsProcessingNow = processing.IsProcessingNow,
                    ProcessingLabel = processing.Label,
                    ProcessingTone = processing.Tone,
                    HasIssue = hasIssue,
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

    private static SubProfileBalanceDto? ResolveBalanceItem(
        IReadOnlyList<SubProfileBalanceDto>? balanceItems,
        int index,
        string name)
    {
        if (balanceItems is not { Count: > 0 })
        {
            return null;
        }

        if (index >= 0 && index < balanceItems.Count)
        {
            return balanceItems[index];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return balanceItems.FirstOrDefault(b =>
            string.Equals(b.SubProfileName?.Trim(), name, StringComparison.OrdinalIgnoreCase));
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
