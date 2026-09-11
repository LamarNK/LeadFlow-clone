using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class KpiCardLinks
{
    public static string? Dashboard(string key, DateTime from, DateTime to, int timeZoneOffsetMinutes = 0) => key switch
    {
        "responses" => Responses(from, to, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "unique" => Responses(from, to, status: "unique", timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "sent" => Responses(from, to, status: "sent", timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "duplicates" => Responses(from, to, status: "duplicate", timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "errors" => "/Events?level=errors",
        "accounts" => "/Accounts",
        "workers" => "/Workers",
        _ => null
    };

    public static string? Responses(
        DateTime from,
        DateTime to,
        string? status = null,
        Guid? workerId = null,
        Guid? accountId = null,
        string? bitrixDestination = null,
        int timeZoneOffsetMinutes = 0)
    {
        var parts = new List<string>
        {
            $"from={from:yyyy-MM-dd}",
            $"to={to:yyyy-MM-dd}",
            $"tz={timeZoneOffsetMinutes}"
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (workerId is Guid worker && worker != Guid.Empty)
        {
            parts.Add($"workerId={worker}");
        }

        if (accountId is Guid account && account != Guid.Empty)
        {
            parts.Add($"accountId={account}");
        }

        if (!string.IsNullOrWhiteSpace(bitrixDestination))
        {
            parts.Add($"bitrixDestination={Uri.EscapeDataString(bitrixDestination)}");
        }

        return $"/Responses?{string.Join("&", parts)}";
    }

    public static string? ResponsesCard(
        string key,
        DateTime from,
        DateTime to,
        Guid? workerId = null,
        Guid? accountId = null,
        int timeZoneOffsetMinutes = 0) => key switch
    {
        "total" => Responses(from, to, workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "unique" => Responses(from, to, status: "unique", workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "duplicates" => Responses(from, to, status: "duplicate", workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "sent" => Responses(from, to, status: "sent", workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        "unique_authors" => Responses(from, to, workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
        _ => null
    };

    public static string? WorkersCard(string key) => key switch
    {
        "total" => "/Workers",
        "online" => "/Workers?status=online",
        "offline" => "/Workers?status=offline",
        "responses" => "/Responses",
        "errors" => "/Events?level=errors",
        _ => null
    };

    public static string AccountTodayResponses(Guid workerId, Guid accountId, int timeZoneOffsetMinutes = 0)
    {
        var today = DashboardPeriod.GetLocalCalendarDate(DateTime.UtcNow, timeZoneOffsetMinutes);
        return Responses(today, today, workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes)!;
    }

    public static string AccountTodayUnique(Guid workerId, Guid accountId, int timeZoneOffsetMinutes = 0)
    {
        var today = DashboardPeriod.GetLocalCalendarDate(DateTime.UtcNow, timeZoneOffsetMinutes);
        return Responses(today, today, status: "unique", workerId: workerId, accountId: accountId, timeZoneOffsetMinutes: timeZoneOffsetMinutes)!;
    }

    public static string AccountErrors(Guid workerId, Guid accountId) =>
        $"/Events?level=errors&workerId={workerId}&accountId={accountId}";

    public static string? WorkerDetailsCard(string key, Guid workerId, int timeZoneOffsetMinutes = 0)
    {
        var today = DashboardPeriod.GetLocalCalendarDate(DateTime.UtcNow, timeZoneOffsetMinutes);
        return key switch
        {
            "accounts" => "#worker-accounts",
            "responses" => Responses(today, today, workerId: workerId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
            "duplicates" => Responses(today, today, status: "duplicate", workerId: workerId, timeZoneOffsetMinutes: timeZoneOffsetMinutes),
            "errors" => $"/Events?level=errors&workerId={workerId}",
            _ => null
        };
    }

    public static string? AccountsCard(string key) => key switch
    {
        "total" => "/Accounts?tab=all",
        "active" => "/Accounts?tab=active",
        "inactive" => "/Accounts?tab=inactive",
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

    public static string? StatisticsCard(
        string key,
        DateTime? from = null,
        DateTime? to = null,
        StatisticsFiltersViewModel? filters = null) => key switch
    {
        "advance" or "wallet" or "accounts" => "/Accounts",
        "responses" when from is not null && to is not null => Responses(from.Value, to.Value, workerId: filters?.WorkerIds.FirstOrDefault(), accountId: filters?.AccountIds.FirstOrDefault()),
        "sent" when from is not null && to is not null => Responses(from.Value, to.Value, status: "sent", workerId: filters?.WorkerIds.FirstOrDefault(), accountId: filters?.AccountIds.FirstOrDefault()),
        "duplicates" when from is not null && to is not null => Responses(from.Value, to.Value, status: "duplicate", workerId: filters?.WorkerIds.FirstOrDefault(), accountId: filters?.AccountIds.FirstOrDefault()),
        "errors" when from is not null && to is not null => "/Events?level=errors",
        "unique" when from is not null && to is not null => Responses(from.Value, to.Value, status: "unique", workerId: filters?.WorkerIds.FirstOrDefault(), accountId: filters?.AccountIds.FirstOrDefault()),
        "unique_authors" when from is not null && to is not null => Responses(from.Value, to.Value, workerId: filters?.WorkerIds.FirstOrDefault(), accountId: filters?.AccountIds.FirstOrDefault()),
        "workers" => "/Workers?status=online",
        _ => "/Statistics"
    };

    public static string Statistics(
        DateTime from,
        DateTime to,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null)
    {
        var parts = new List<string>
        {
            $"from={from:yyyy-MM-dd}",
            $"to={to:yyyy-MM-dd}"
        };

        if (workerIds is not null)
        {
            parts.AddRange(workerIds.Select(id => $"workerIds={id}"));
        }

        if (accountIds is not null)
        {
            parts.AddRange(accountIds.Select(id => $"accountIds={id}"));
        }

        return $"/Statistics?{string.Join("&", parts)}";
    }
}
