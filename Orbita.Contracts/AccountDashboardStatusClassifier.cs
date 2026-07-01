namespace Orbita.Contracts;

public enum AccountDashboardCategory
{
    Active,
    Inactive,
    Blocked,
    Error
}

public static class AccountDashboardStatusClassifier
{
    public static AccountDashboardCategory Classify(string? status, bool isEnabledInPanel)
    {
        status ??= string.Empty;

        if (status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            return AccountDashboardCategory.Blocked;

        if (status.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("RequiresLogin", StringComparison.OrdinalIgnoreCase)
            || status.Equals("RequiresManualAction", StringComparison.OrdinalIgnoreCase))
            return AccountDashboardCategory.Error;

        if (!isEnabledInPanel
            || status.Equals("Paused", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Offline", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Inactive", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(status))
            return AccountDashboardCategory.Inactive;

        if (status.Equals("Active", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Monitoring", StringComparison.OrdinalIgnoreCase))
            return AccountDashboardCategory.Active;

        return AccountDashboardCategory.Inactive;
    }

    public static (int Total, int Active, int Inactive, int Blocked, int Errors) Summarize(
        IEnumerable<(string Status, bool IsEnabledInPanel)> accounts)
    {
        var total = 0;
        var active = 0;
        var inactive = 0;
        var blocked = 0;
        var errors = 0;

        foreach (var (status, isEnabledInPanel) in accounts)
        {
            total++;
            switch (Classify(status, isEnabledInPanel))
            {
                case AccountDashboardCategory.Active:
                    active++;
                    break;
                case AccountDashboardCategory.Inactive:
                    inactive++;
                    break;
                case AccountDashboardCategory.Blocked:
                    blocked++;
                    break;
                case AccountDashboardCategory.Error:
                    errors++;
                    break;
            }
        }

        return (total, active, inactive, blocked, errors);
    }
}