using System.Collections.Concurrent;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Short-lived in-process cache for heavy panel aggregates.
/// Keys include office scope and timezone so one office never sees another office's numbers.
/// Writes invalidate immediately so SignalR-triggered refreshes recompute instead of
/// serving the pre-write payload. TTL only coalesces concurrent tab refreshes.
/// </summary>
internal static class PanelAggregateCache
{
    public static readonly TimeSpan DashboardTtl = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan NavBadgesTtl = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StatisticsTtl = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan AccountsTtl = TimeSpan.FromSeconds(5);

    private const string DashboardPrefix = "d:";
    private const string StatisticsPrefix = "s:";
    private const string AccountsPrefix = "a:";

    private static readonly ConcurrentDictionary<string, CacheEntry> Entries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private static long _dashboardVersion;
    private static long _statisticsVersion;
    private static long _accountsVersion;

    public static string SummaryKey(
        OfficeScope scope,
        Guid? officeFilter,
        int? timeZoneOffsetMinutes,
        DateTime fromLocal,
        DateTime toLocal) =>
        $"{DashboardPrefix}s:{ScopeKey(scope, officeFilter)}:{TzKey(timeZoneOffsetMinutes)}:{fromLocal:yyyyMMdd}:{toLocal:yyyyMMdd}";

    public static string NavBadgesKey(OfficeScope scope, Guid? officeFilter, int? timeZoneOffsetMinutes) =>
        $"{DashboardPrefix}b:{ScopeKey(scope, officeFilter)}:{TzKey(timeZoneOffsetMinutes)}";

    public static string StatisticsKey(
        OfficeScope scope,
        Guid? officeFilter,
        DateTime fromLocal,
        DateTime toLocal,
        string workerFilterKey,
        string accountFilterKey,
        string vacancyFilterKey) =>
        $"{StatisticsPrefix}{ScopeKey(scope, officeFilter)}:{fromLocal:yyyyMMdd}:{toLocal:yyyyMMdd}:{workerFilterKey}:{accountFilterKey}:{vacancyFilterKey}";

    public static string AccountsKey(OfficeScope scope, Guid? officeFilter, Guid? workerId) =>
        $"{AccountsPrefix}{ScopeKey(scope, officeFilter)}:{workerId?.ToString("D") ?? "-"}";

    public static async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
        where T : class
    {
        if (TryGet<T>(key, out var cached))
        {
            return cached;
        }

        var gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (TryGet<T>(key, out cached))
            {
                return cached;
            }

            var version = CurrentVersion(key);
            var value = await factory();
            if (CurrentVersion(key) == version)
            {
                SetCore(key, value, ttl, version);
            }

            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public static void Set<T>(string key, T value, TimeSpan ttl)
        where T : class
    {
        SetCore(key, value, ttl, CurrentVersion(key));
    }

    public static void Invalidate(IReadOnlyList<PanelChangeKind> kinds)
    {
        var dashboard = false;
        var statistics = false;
        var accounts = false;
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case PanelChangeKind.Dashboard:
                case PanelChangeKind.NavBadges:
                    dashboard = true;
                    break;
                case PanelChangeKind.Responses:
                case PanelChangeKind.Errors:
                    dashboard = true;
                    accounts = true;
                    break;
                case PanelChangeKind.Accounts:
                    dashboard = true;
                    accounts = true;
                    break;
                case PanelChangeKind.Workers:
                    accounts = true;
                    break;
                case PanelChangeKind.Statistics:
                    statistics = true;
                    break;
            }
        }

        if (dashboard)
        {
            InvalidateDashboard();
        }

        if (statistics)
        {
            InvalidateStatistics();
        }

        if (accounts)
        {
            InvalidateAccounts();
        }
    }

    public static void InvalidateDashboard()
    {
        Interlocked.Increment(ref _dashboardVersion);
        RemovePrefix(DashboardPrefix);
    }

    public static void InvalidateStatistics()
    {
        Interlocked.Increment(ref _statisticsVersion);
        RemovePrefix(StatisticsPrefix);
    }

    public static void InvalidateAccounts()
    {
        Interlocked.Increment(ref _accountsVersion);
        RemovePrefix(AccountsPrefix);
    }

    public static void Clear()
    {
        Interlocked.Increment(ref _dashboardVersion);
        Interlocked.Increment(ref _statisticsVersion);
        Interlocked.Increment(ref _accountsVersion);
        Entries.Clear();
    }

    private static bool TryGet<T>(string key, out T value)
        where T : class
    {
        value = null!;
        if (!Entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresUtc <= DateTime.UtcNow || entry.Version != CurrentVersion(key))
        {
            Entries.TryRemove(key, out _);
            return false;
        }

        if (entry.Value is not T typed)
        {
            Entries.TryRemove(key, out _);
            return false;
        }

        value = typed;
        return true;
    }

    private static void SetCore<T>(string key, T value, TimeSpan ttl, long version)
        where T : class
    {
        Entries[key] = new CacheEntry(value, DateTime.UtcNow.Add(ttl), version);
        if (Entries.Count > 256)
        {
            PruneExpired();
        }
    }

    private static long CurrentVersion(string key)
    {
        if (key.StartsWith(StatisticsPrefix, StringComparison.Ordinal))
        {
            return Volatile.Read(ref _statisticsVersion);
        }

        if (key.StartsWith(AccountsPrefix, StringComparison.Ordinal))
        {
            return Volatile.Read(ref _accountsVersion);
        }

        return Volatile.Read(ref _dashboardVersion);
    }

    private static void RemovePrefix(string prefix)
    {
        foreach (var key in Entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                Entries.TryRemove(key, out _);
            }
        }
    }

    private static void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in Entries)
        {
            if (pair.Value.ExpiresUtc <= now)
            {
                Entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string ScopeKey(OfficeScope scope, Guid? officeFilter)
    {
        var effectiveOfficeId = scope.ResolveFilter(officeFilter);
        return $"{(scope.IsGlobalAdmin ? "1" : "0")}:{effectiveOfficeId?.ToString("D") ?? "-"}";
    }

    private static string TzKey(int? timeZoneOffsetMinutes) =>
        timeZoneOffsetMinutes?.ToString() ?? "local";

    private sealed record CacheEntry(object Value, DateTime ExpiresUtc, long Version);
}
