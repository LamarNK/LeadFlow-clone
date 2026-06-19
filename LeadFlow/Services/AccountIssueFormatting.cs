using LeadFlow.Models;

namespace LeadFlow.Services;

public static class AccountIssueFormatting
{
    public static string FormatProfileContext(AvitoAccount account, AvitoSubProfile? sub)
    {
        if (sub is not null && !string.IsNullOrWhiteSpace(sub.Name))
        {
            return $"Субпрофиль «{sub.Name.Trim()}» · аккаунт «{account.DisplayName}»";
        }

        if (sub is not null && !string.IsNullOrWhiteSpace(sub.Id))
        {
            return $"Субпрофиль id={sub.Id.Trim()} · аккаунт «{account.DisplayName}»";
        }

        if (account.ProfileProvider == AvitoProfileProvider.AdsPower
            && !string.IsNullOrWhiteSpace(account.AdsPowerProfileName))
        {
            return $"AdsPower «{account.AdsPowerProfileName.Trim()}» · аккаунт «{account.DisplayName}»";
        }

        if (account.ProfileProvider == AvitoProfileProvider.AdsPower
            && !string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
        {
            return $"AdsPower user_id={account.AdsPowerProfileId.Trim()} · аккаунт «{account.DisplayName}»";
        }

        return $"Аккаунт «{account.DisplayName}»";
    }

    public static string FormatIssue(AvitoAccount account, AvitoSubProfile? sub, string kind, string detail) =>
        $"{FormatProfileContext(account, sub)} — {AvitoSubProfileIssueKind.ToDisplayLabel(kind)}: {detail.Trim()}";
}