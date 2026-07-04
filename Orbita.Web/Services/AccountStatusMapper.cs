using Orbita.Contracts;

namespace Orbita.Web.Services;

internal static class AccountStatusMapper
{
    public static (string Label, string Tone) ForAccountsPage(string status, bool isEnabledInPanel)
    {
        if (status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            return ("Заблокирован", "inactive");

        return Classify(status, isEnabledInPanel) switch
        {
            AccountDashboardCategory.Active => ("Активен", "active"),
            AccountDashboardCategory.Inactive => ("Неактивен", "inactive"),
            AccountDashboardCategory.Error => ("Ошибка", "error"),
            _ => ("Неактивен", "inactive")
        };
    }

    public static (string Label, string Tone) ForWorkerDetails(string status, bool isEnabledInPanel)
    {
        if (status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            return ("Заблокирован", "inactive");

        return Classify(status, isEnabledInPanel) switch
        {
            AccountDashboardCategory.Active => ("Активен", "success"),
            AccountDashboardCategory.Inactive => ("Неактивен", "inactive"),
            AccountDashboardCategory.Error => ("Ошибка", "error"),
            _ => ("Неактивен", "inactive")
        };
    }

    private static AccountDashboardCategory Classify(string status, bool isEnabledInPanel) =>
        AccountDashboardStatusClassifier.Classify(status, isEnabledInPanel);
}