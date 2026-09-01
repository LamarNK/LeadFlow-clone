using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class WorkerActivityPresenter
{
    public const int LiveThresholdSeconds = 90;

    public static WorkerActivityViewModel Present(
        WorkerActivityDto? activity,
        bool isOnline,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null,
        DateTime? nowUtc = null)
    {
        nowUtc ??= DateTime.UtcNow;

        if (!isOnline)
        {
            return new WorkerActivityViewModel
            {
                Label = "Оффлайн",
                Tone = "offline",
                IsLive = false
            };
        }

        var liveAccounts = GetLiveActiveAccounts(activeAccounts, nowUtc);
        if (liveAccounts.Count > 1)
        {
            return new WorkerActivityViewModel
            {
                Label = $"{liveAccounts.Count} аккаунта в работе",
                Tone = "live",
                IsLive = true,
                Phase = WorkerActivityPhases.Parallel,
                UpdatedAtUtc = liveAccounts.Max(x => x.UpdatedAtUtc),
                ActiveAccounts = PresentActiveAccounts(liveAccounts, isOnline, nowUtc)
            };
        }

        if (liveAccounts.Count == 1)
        {
            return PresentActiveAccount(liveAccounts[0], nowUtc);
        }

        if (activity is null)
        {
            return new WorkerActivityViewModel
            {
                Label = "Нет данных",
                Tone = "muted",
                IsLive = false
            };
        }

        var age = nowUtc.Value - activity.UpdatedAtUtc;
        var isLive = age.TotalSeconds >= 0 && age.TotalSeconds < LiveThresholdSeconds;
        var label = FormatLabel(activity, nowUtc.Value);
        if (!isLive && age.TotalSeconds >= LiveThresholdSeconds)
        {
            label += $" · {FormatAge(age)} назад";
        }

        return new WorkerActivityViewModel
        {
            Label = label,
            Tone = MapTone(activity.Phase, isLive),
            IsLive = isLive,
            Phase = activity.Phase,
            AccountId = activity.AccountId,
            SubProfileId = activity.SubProfileId,
            UpdatedAtUtc = activity.UpdatedAtUtc,
            NextCycleAtUtc = string.Equals(activity.Phase, WorkerActivityPhases.Waiting, StringComparison.Ordinal)
                ? activity.NextCycleAtUtc
                : null
        };
    }

    public static IReadOnlyList<WorkerActivityViewModel> PresentActiveAccounts(
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts,
        bool isOnline,
        DateTime? nowUtc = null)
    {
        if (!isOnline)
        {
            return [];
        }

        return GetLiveActiveAccounts(activeAccounts, nowUtc)
            .Select(x => PresentActiveAccount(x, nowUtc))
            .ToList();
    }

    public static AccountProcessingViewModel PresentForSubProfile(
        WorkerActivityDto? activity,
        bool workerIsOnline,
        Guid accountId,
        string subProfileId,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null,
        DateTime? nowUtc = null)
    {
        if (!workerIsOnline || string.IsNullOrWhiteSpace(subProfileId))
        {
            return new AccountProcessingViewModel();
        }

        var match = GetLiveActiveAccounts(activeAccounts, nowUtc)
            .FirstOrDefault(x => x.AccountId == accountId
                && string.Equals(x.SubProfileId, subProfileId, StringComparison.Ordinal));
        if (match is not null)
        {
            return new AccountProcessingViewModel
            {
                IsProcessingNow = true,
                Label = FormatSubProfileLabelFromActive(match),
                SubProfileId = subProfileId,
                Tone = MapTone(match.Phase, isLive: true)
            };
        }

        if (activity?.AccountId != accountId
            || !string.Equals(activity.SubProfileId, subProfileId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(activity.Message))
        {
            return new AccountProcessingViewModel();
        }

        var workerActivity = Present(activity, workerIsOnline, activeAccounts, nowUtc);
        return new AccountProcessingViewModel
        {
            IsProcessingNow = workerActivity.IsLive,
            Label = activity.Message,
            SubProfileId = subProfileId,
            Tone = workerActivity.Tone
        };
    }

    public static AccountProcessingViewModel PresentForAccount(
        WorkerActivityDto? activity,
        bool workerIsOnline,
        Guid accountId,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null,
        DateTime? nowUtc = null)
    {
        if (!workerIsOnline)
        {
            return new AccountProcessingViewModel();
        }

        var match = GetLiveActiveAccounts(activeAccounts, nowUtc)
            .FirstOrDefault(x => x.AccountId == accountId);
        if (match is not null)
        {
            return new AccountProcessingViewModel
            {
                IsProcessingNow = true,
                Label = FormatAccountLabelFromActive(match),
                SubProfileId = match.SubProfileId,
                Tone = MapTone(match.Phase, isLive: true)
            };
        }

        var workerActivity = Present(activity, workerIsOnline, activeAccounts, nowUtc);
        if (activity?.AccountId != accountId || string.IsNullOrWhiteSpace(activity.Message))
        {
            return new AccountProcessingViewModel();
        }

        return new AccountProcessingViewModel
        {
            IsProcessingNow = workerActivity.IsLive,
            Label = FormatAccountLabel(activity),
            SubProfileId = activity.SubProfileId,
            Tone = workerActivity.Tone
        };
    }

    private static WorkerActivityViewModel PresentActiveAccount(
        WorkerActiveAccountDto active,
        DateTime? nowUtc)
    {
        nowUtc ??= DateTime.UtcNow;
        var age = nowUtc.Value - active.UpdatedAtUtc;
        var isLive = age.TotalSeconds >= 0 && age.TotalSeconds < LiveThresholdSeconds;
        var label = FormatAccountActivityLabel(active);
        if (!isLive && age.TotalSeconds >= LiveThresholdSeconds)
        {
            label += $" · {FormatAge(age)} назад";
        }

        return new WorkerActivityViewModel
        {
            Label = label,
            Tone = MapTone(active.Phase, isLive),
            IsLive = isLive,
            Phase = active.Phase,
            AccountId = active.AccountId,
            SubProfileId = active.SubProfileId,
            UpdatedAtUtc = active.UpdatedAtUtc
        };
    }

    private static List<WorkerActiveAccountDto> GetLiveActiveAccounts(
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts,
        DateTime? nowUtc)
    {
        if (activeAccounts is null || activeAccounts.Count == 0)
        {
            return [];
        }

        nowUtc ??= DateTime.UtcNow;
        return activeAccounts
            .Where(x =>
            {
                var age = nowUtc.Value - x.UpdatedAtUtc;
                return age.TotalSeconds >= 0 && age.TotalSeconds < LiveThresholdSeconds;
            })
            .OrderBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatAccountActivityLabel(WorkerActiveAccountDto active)
    {
        var accountPart = $"«{active.AccountName}»";
        if (!string.IsNullOrWhiteSpace(active.SubProfileName))
        {
            return $"{accountPart} · «{active.SubProfileName}» · {active.Message}";
        }

        return $"{accountPart} · {active.Message}";
    }

    private static string FormatAccountLabelFromActive(WorkerActiveAccountDto active)
    {
        if (!string.IsNullOrWhiteSpace(active.SubProfileName))
        {
            return $"«{active.SubProfileName}» · {active.Message}";
        }

        return active.Message;
    }

    private static string FormatSubProfileLabelFromActive(WorkerActiveAccountDto active) =>
        !string.IsNullOrWhiteSpace(active.SubProfileName)
            ? $"«{active.SubProfileName}» · {active.Message}"
            : active.Message;

    private static string FormatLabel(WorkerActivityDto activity, DateTime nowUtc)
    {
        if (string.Equals(activity.Phase, WorkerActivityPhases.Idle, StringComparison.Ordinal))
        {
            return activity.Message;
        }

        if (string.Equals(activity.Phase, WorkerActivityPhases.Parallel, StringComparison.Ordinal)
            && activity.ActiveAccounts is { Count: > 1 })
        {
            return $"{activity.ActiveAccounts.Count} аккаунта в работе";
        }

        if (string.Equals(activity.Phase, WorkerActivityPhases.Waiting, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(activity.Message)
                && activity.Message.Contains("обновлен", StringComparison.OrdinalIgnoreCase))
            {
                return activity.Message;
            }

            return FormatWaitingLabel(activity.NextCycleAtUtc, nowUtc);
        }

        if (!string.IsNullOrWhiteSpace(activity.AccountName))
        {
            var accountPart = $"«{activity.AccountName}»";
            if (!string.IsNullOrWhiteSpace(activity.SubProfileName))
            {
                return $"{accountPart} · «{activity.SubProfileName}» · {activity.Message}";
            }

            return $"{accountPart} · {activity.Message}";
        }

        return activity.Message;
    }

    private static string FormatWaitingLabel(DateTime? nextCycleAtUtc, DateTime nowUtc)
    {
        if (nextCycleAtUtc is not DateTime nextUtc)
        {
            return "Пауза · ожидание цикла";
        }

        var remaining = nextUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "Пауза · ожидание цикла";
        }

        var remainingText = remaining < TimeSpan.FromMinutes(1)
            ? "меньше минуты"
            : $"{(int)remaining.TotalMinutes} мин";

        return $"Пауза · осталось {remainingText}";
    }

    private static string FormatAccountLabel(WorkerActivityDto activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.SubProfileName))
        {
            return $"«{activity.SubProfileName}» · {activity.Message}";
        }

        return activity.Message;
    }

    private static string MapTone(string phase, bool isLive) => phase switch
    {
        WorkerActivityPhases.Error => "error",
        WorkerActivityPhases.Skipped => "warning",
        WorkerActivityPhases.Waiting or WorkerActivityPhases.Idle or WorkerActivityPhases.Stopped => "muted",
        _ => isLive ? "live" : "muted"
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours} ч";
        }

        return $"{Math.Max(1, (int)age.TotalMinutes)} мин";
    }
}