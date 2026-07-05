using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class AccountErrorMetrics
{
    public static int ComputeErrorCount(
        int todayEventErrors,
        string? lastErrorMessage,
        IReadOnlyList<SubProfileRowViewModel> subProfiles,
        string statusTone)
    {
        if (todayEventErrors > 0)
        {
            return todayEventErrors;
        }

        if (!string.IsNullOrWhiteSpace(lastErrorMessage))
        {
            return 1;
        }

        var subProfileIssues = subProfiles.Count(static s => s.HasIssue);
        if (subProfileIssues > 0)
        {
            return subProfileIssues;
        }

        return statusTone == "error" ? 1 : 0;
    }

    public static string? ComputeErrorHint(
        int todayEventErrors,
        string? lastErrorMessage,
        IReadOnlyList<SubProfileRowViewModel> subProfiles,
        string statusTone)
    {
        if (!string.IsNullOrWhiteSpace(lastErrorMessage))
        {
            return null;
        }

        if (todayEventErrors > 0)
        {
            return todayEventErrors == 1
                ? "1 ошибка в журнале за сегодня"
                : $"{todayEventErrors} ошибки в журнале за сегодня";
        }

        var issue = subProfiles.FirstOrDefault(static s => s.HasIssue);
        if (issue is not null)
        {
            return string.IsNullOrWhiteSpace(issue.IssueSummary)
                ? "Проблема на субпрофиле"
                : issue.IssueSummary;
        }

        return statusTone == "error" ? "Требует внимания" : null;
    }

    public static bool HasErrors(AccountRowViewModel account) =>
        account.Errors > 0 || account.StatusTone == "error";
}