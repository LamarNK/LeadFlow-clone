using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services;

public static class AccountIssueTracker
{
    public static void SetSubProfileIssue(AvitoSubProfile sub, string kind, string message)
    {
        sub.LastIssueKind = kind;
        sub.LastIssueMessage = message.Trim();
        sub.LastIssueAt = DateTime.UtcNow;
    }

    public static void ClearSubProfileIssue(AvitoSubProfile sub)
    {
        sub.LastIssueKind = string.Empty;
        sub.LastIssueMessage = string.Empty;
        sub.LastIssueAt = null;
    }

    public static void ClearAllSubProfileIssues(AvitoAccount account)
    {
        var subs = account.SubProfiles;
        if (subs.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var sub in subs)
        {
            if (!sub.HasIssue)
            {
                continue;
            }

            ClearSubProfileIssue(sub);
            changed = true;
        }

        if (changed)
        {
            account.SetSubProfiles(subs.ToList());
            RefreshAccountIssueMessage(account);
        }
    }

    public static void ApplySubProfileIssue(AvitoAccount account, AvitoSubProfile sub, string kind, string detail)
    {
        SetSubProfileIssue(sub, kind, detail);
        account.SetSubProfiles(account.SubProfiles.ToList());
        RefreshAccountIssueMessage(account);
    }

    /// <summary>
    /// Синхронизирует <see cref="AvitoAccount.LastErrorMessage"/> и при необходимости статус
    /// с актуальным списком проблем суб-профилей.
    /// </summary>
    public static void RefreshAccountIssueMessage(AvitoAccount account)
    {
        if (account.HasSubProfileIssues)
        {
            account.LastErrorMessage = account.SubProfileIssuesSummary;
            SyncAccountStatusFromSubProfileIssues(account);
            return;
        }

        if (account.Status is AvitoAccountStatus.Authorized or AvitoAccountStatus.Monitoring)
        {
            account.LastErrorMessage = string.Empty;
        }
    }

    public static string BuildIssuesSummary(AvitoAccount account)
    {
        if (account.HasSubProfileIssues)
        {
            return account.SubProfileIssuesSummary;
        }

        return account.LastErrorMessage;
    }

    public static string FormatStatusHint(AvitoAccount account)
    {
        var summary = BuildIssuesSummary(account);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary.Trim();
        }

        return account.Status switch
        {
            AvitoAccountStatus.RequiresLogin => "требуется вход в Avito",
            AvitoAccountStatus.RequiresManualAction => "требуется ручное действие",
            AvitoAccountStatus.Paused => "аккаунт приостановлен",
            AvitoAccountStatus.Error => "ошибка аккаунта",
            _ => $"статус: {account.Status}"
        };
    }

    public static string FormatCycleProblemsHint(IEnumerable<AvitoAccount> accounts)
    {
        var problemAccounts = accounts
            .Where(static account => account.IsEnabled)
            .Where(static account =>
                account.HasSubProfileIssues
                || account.Status is AvitoAccountStatus.RequiresLogin
                    or AvitoAccountStatus.RequiresManualAction
                    or AvitoAccountStatus.Error)
            .ToList();

        if (problemAccounts.Count == 0)
        {
            return string.Empty;
        }

        var lines = problemAccounts
            .Select(static account => $"{account.DisplayName}: {FormatStatusHint(account)}")
            .Take(3)
            .ToList();

        var suffix = problemAccounts.Count > lines.Count ? " …" : string.Empty;
        return $"Требуют внимания: {string.Join(" | ", lines)}{suffix}";
    }

    private static void SyncAccountStatusFromSubProfileIssues(AvitoAccount account)
    {
        if (account.SubProfiles.Any(static sub =>
                sub.HasIssue && sub.LastIssueKind == AvitoSubProfileIssueKind.Captcha))
        {
            account.Status = AvitoAccountStatus.RequiresManualAction;
            return;
        }

        if (account.SubProfiles.Any(static sub =>
                sub.HasIssue && sub.LastIssueKind == AvitoSubProfileIssueKind.AuthRequired))
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
        }
    }
}