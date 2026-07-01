namespace Orbita.Web.Services;

internal static class KpiCardLinks
{
    public static string? Dashboard(string key, DateTime from, DateTime to) => key switch
    {
        "responses" => Responses(from, to),
        "duplicates" => Responses(from, to, status: "duplicate"),
        "errors" => "/Errors",
        "accounts" => "/Accounts",
        "workers" => "/Workers",
        _ => null
    };

    public static string? Responses(DateTime from, DateTime to, string? status = null, Guid? workerId = null, Guid? accountId = null)
    {
        var parts = new List<string>
        {
            $"from={from:yyyy-MM-dd}",
            $"to={to:yyyy-MM-dd}"
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (workerId is not null)
        {
            parts.Add($"workerId={workerId.Value}");
        }

        if (accountId is not null)
        {
            parts.Add($"accountId={accountId.Value}");
        }

        return $"/Responses?{string.Join("&", parts)}";
    }

    public static string? ResponsesCard(string key, DateTime from, DateTime to, Guid? workerId = null, Guid? accountId = null) => key switch
    {
        "total" => Responses(from, to, workerId: workerId, accountId: accountId),
        "unique" => Responses(from, to, status: "unique", workerId: workerId, accountId: accountId),
        "duplicates" => Responses(from, to, status: "duplicate", workerId: workerId, accountId: accountId),
        "unique_authors" => Responses(from, to, workerId: workerId, accountId: accountId),
        _ => null
    };

    public static string? WorkersCard(string key) => key switch
    {
        "total" => "/Workers",
        "online" => "/Workers?status=online",
        "offline" => "/Workers?status=offline",
        "responses" => "/Responses",
        "errors" => "/Errors",
        _ => null
    };

    public static string? WorkerDetailsCard(string key, Guid workerId) => key switch
    {
        "accounts" => "#worker-accounts",
        "responses" => Responses(DateTime.Today, DateTime.Today, workerId: workerId),
        "duplicates" => Responses(DateTime.Today, DateTime.Today, status: "duplicate", workerId: workerId),
        "errors" => $"/Errors?workerId={workerId}",
        _ => null
    };

    public static string? AccountsCard(string key) => key switch
    {
        "total" => "/Accounts?tab=all",
        "active" => "/Accounts?tab=active",
        "inactive" => "/Accounts?tab=inactive",
        "blocked" => "/Accounts?tab=blocked",
        "errors" => "/Accounts?tab=errors",
        _ => null
    };

    public static string? EventsCard(string key) => key switch
    {
        "total" => "/Events",
        "success" => "/Events?level=success",
        "warning" => "/Events?level=warning",
        "error" => "/Events?level=error",
        _ => null
    };

    public static string? ErrorsCard(string key) => key switch
    {
        "total" => "/Errors",
        "critical" => "/Errors?severity=critical",
        "high" => "/Errors?severity=high",
        "medium" => "/Errors?severity=medium",
        "low" => "/Errors?severity=low",
        _ => null
    };
}