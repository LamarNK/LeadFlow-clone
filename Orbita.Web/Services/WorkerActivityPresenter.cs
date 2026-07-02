using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class WorkerActivityPresenter
{
    public const int LiveThresholdSeconds = 90;

    public static WorkerActivityViewModel Present(
        WorkerActivityDto? activity,
        bool isOnline,
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
            UpdatedAtUtc = activity.UpdatedAtUtc
        };
    }

    public static AccountProcessingViewModel PresentForAccount(
        WorkerActivityDto? activity,
        bool workerIsOnline,
        Guid accountId,
        DateTime? nowUtc = null)
    {
        var workerActivity = Present(activity, workerIsOnline, nowUtc);
        if (!workerIsOnline
            || activity?.AccountId != accountId
            || string.IsNullOrWhiteSpace(activity.Message))
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

    private static string FormatLabel(WorkerActivityDto activity, DateTime nowUtc)
    {
        if (string.Equals(activity.Phase, WorkerActivityPhases.Idle, StringComparison.Ordinal))
        {
            return activity.Message;
        }

        if (string.Equals(activity.Phase, WorkerActivityPhases.Waiting, StringComparison.Ordinal)
            && activity.NextCycleAtUtc is not null)
        {
            var minutesUntil = (int)Math.Round((activity.NextCycleAtUtc.Value - nowUtc).TotalMinutes);
            if (minutesUntil > 0)
            {
                return $"Пауза · следующий цикл ~{minutesUntil} мин";
            }

            return "Пауза · ожидание цикла";
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