using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class TableSort
{
    public static TableSortState Parse(
        string? sort,
        string? dir,
        TableSortState @default,
        IReadOnlySet<string> allowedColumns)
    {
        if (string.IsNullOrWhiteSpace(sort) || !allowedColumns.Contains(sort))
        {
            return @default;
        }

        var descending = ResolveDescending(sort, dir, @default);
        return TableSortState.Create(sort, descending);
    }

    public static (string Column, string Dir) Toggle(string column, TableSortState current)
    {
        if (string.Equals(current.Column, column, StringComparison.OrdinalIgnoreCase))
        {
            return (column, current.Descending ? "asc" : "desc");
        }

        return (column, "asc");
    }

    private static bool ResolveDescending(string sort, string? dir, TableSortState @default)
    {
        if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(sort, @default.Column, StringComparison.OrdinalIgnoreCase) && @default.Descending;
    }

    internal static class Accounts
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "account", "worker", "status", "balance", "responses", "unique", "errors", "activity"
        };

        public static readonly TableSortState Default = TableSortState.Create("account", descending: false);

        public static IEnumerable<AccountRowViewModel> Apply(
            IEnumerable<AccountRowViewModel> rows,
            TableSortState sort)
        {
            return sort.Column switch
            {
                "worker" => OrderString(rows, x => x.WorkerName, sort.Descending),
                "status" => OrderString(rows, x => x.StatusLabel, sort.Descending),
                "balance" => OrderDecimal(rows, x => x.Balance, sort.Descending),
                "responses" => OrderInt(rows, x => x.Responses, sort.Descending),
                "unique" => OrderInt(rows, x => x.UniqueResponses, sort.Descending),
                "errors" => OrderInt(rows, x => x.Errors, sort.Descending),
                "activity" => OrderDate(rows, x => x.LastActivityUtc, sort.Descending),
                _ => OrderString(rows, x => x.AccountName, sort.Descending)
            };
        }
    }

    internal static class Workers
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "name", "status", "accounts", "responses", "duplicates", "errors", "activity"
        };

        public static readonly TableSortState Default = TableSortState.Create("name", descending: false);

        public static IEnumerable<WorkerRowViewModel> Apply(
            IEnumerable<WorkerRowViewModel> rows,
            TableSortState sort)
        {
            return sort.Column switch
            {
                "status" => sort.Descending
                    ? rows.OrderBy(x => x.IsEnabled).ThenBy(x => x.IsOnline)
                    : rows.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.IsOnline),
                "accounts" => sort.Descending
                    ? rows.OrderByDescending(x => x.ActiveAccounts).ThenByDescending(x => x.TotalAccounts)
                    : rows.OrderBy(x => x.ActiveAccounts).ThenBy(x => x.TotalAccounts),
                "responses" => OrderInt(rows, x => x.Responses, sort.Descending),
                "duplicates" => OrderInt(rows, x => x.Duplicates, sort.Descending),
                "errors" => OrderInt(rows, x => x.Errors, sort.Descending),
                "activity" => OrderDate(rows, x => x.LastActivityUtc, sort.Descending),
                _ => OrderString(rows, x => x.DisplayName, sort.Descending)
            };
        }
    }

    internal static class DashboardWorkers
    {
        public static readonly HashSet<string> Columns = WorkerListPaging.SortColumns;

        public static readonly TableSortState Default = TableSortState.Create(
            WorkerListPaging.DefaultSort,
            descending: true);

        public static IEnumerable<DashboardWorkerRowViewModel> Apply(
            IEnumerable<DashboardWorkerRowViewModel> rows,
            TableSortState sort)
        {
            return sort.Column switch
            {
                "status" => sort.Descending
                    ? rows.OrderBy(x => x.IsMonitoringPaused).ThenBy(x => x.IsEnabled).ThenBy(x => x.IsOnline)
                    : rows.OrderByDescending(x => x.IsMonitoringPaused).ThenByDescending(x => x.IsEnabled).ThenByDescending(x => x.IsOnline),
                "activity" => OrderDate(rows, x => x.LastActivityUtc, sort.Descending),
                _ => OrderString(rows, x => x.DisplayName, sort.Descending)
            };
        }
    }

    internal static class Events
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "time", "type", "level", "account", "worker", "description"
        };

        public static readonly TableSortState Default = TableSortState.Create("time", descending: true);

        public static IEnumerable<EventRowViewModel> Apply(
            IEnumerable<EventRowViewModel> rows,
            TableSortState sort)
        {
            return sort.Column switch
            {
                "type" => OrderString(rows, x => x.EventTypeLabel, sort.Descending),
                "level" => OrderString(rows, x => x.LevelLabel, sort.Descending),
                "account" => OrderString(rows, x => x.AccountName ?? string.Empty, sort.Descending),
                "worker" => OrderString(rows, x => x.WorkerName, sort.Descending),
                "description" => OrderString(rows, x => x.Description, sort.Descending),
                _ => OrderDate(rows, x => x.OccurredAtUtc, sort.Descending)
            };
        }
    }

    internal static class Errors
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "time", "severity", "type", "message", "account", "worker", "count", "last"
        };

        public static readonly TableSortState Default = TableSortState.Create("time", descending: true);

        public static IEnumerable<ErrorRowViewModel> Apply(
            IEnumerable<ErrorRowViewModel> rows,
            TableSortState sort)
        {
            var ordered = sort.Column switch
            {
                "severity" => OrderString(rows, x => x.SeverityLabel, sort.Descending),
                "type" => OrderString(rows, x => x.ErrorTypeLabel, sort.Descending),
                "message" => OrderString(rows, x => x.Message, sort.Descending),
                "account" => OrderString(rows, x => x.AccountName ?? string.Empty, sort.Descending),
                "worker" => OrderString(rows, x => x.WorkerName, sort.Descending),
                "count" => OrderInt(rows, x => x.OccurrenceCount, sort.Descending),
                "last" => OrderDate(rows, x => x.LastSeenUtc, sort.Descending),
                _ => OrderDate(rows, x => x.OccurredAtUtc, sort.Descending)
            };

            return sort.Column is "time" or "last"
                ? ordered
                : sort.Descending
                    ? ordered.ThenByDescending(x => x.OccurredAtUtc)
                    : ordered.ThenBy(x => x.OccurredAtUtc);
        }
    }

    internal static class Responses
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "time", "responded", "vacancy", "author", "phone", "city", "age", "account", "status"
        };

        public static readonly TableSortState Default = TableSortState.Create("time", descending: true);

        public static IEnumerable<ResponseRowViewModel> Apply(
            IEnumerable<ResponseRowViewModel> rows,
            TableSortState sort)
        {
            var ordered = sort.Column switch
            {
                "vacancy" => OrderString(rows, x => x.Vacancy, sort.Descending),
                "author" => OrderString(rows, x => x.FullName, sort.Descending),
                "phone" => OrderString(rows, x => x.PhoneNormalized.Length > 0 ? x.PhoneNormalized : x.PhoneRaw, sort.Descending),
                "city" => OrderString(rows, x => x.City, sort.Descending),
                "age" => OrderNullableInt(rows, x => x.Age, sort.Descending),
                "account" => OrderString(rows, x => x.AccountName, sort.Descending),
                "status" => OrderString(rows, x => x.StatusLabel, sort.Descending),
                "responded" => sort.Descending
                    ? rows.OrderByDescending(x => x.CreatedAtUtc)
                    : rows.OrderBy(x => x.CreatedAtUtc),
                _ => sort.Descending
                    ? rows.OrderByDescending(x => x.CollectedAtUtc)
                    : rows.OrderBy(x => x.CollectedAtUtc)
            };

            return sort.Column is "time" or "responded"
                ? ordered
                : sort.Descending
                    ? ordered.ThenByDescending(x => x.CollectedAtUtc)
                    : ordered.ThenBy(x => x.CollectedAtUtc);
        }
    }

    internal static class WorkerAccounts
    {
        public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            "account", "status", "balance", "responses", "activity", "errors"
        };

        public static readonly TableSortState Default = TableSortState.Create("account", descending: false);

        public static IEnumerable<WorkerAccountRowViewModel> Apply(
            IEnumerable<WorkerAccountRowViewModel> rows,
            TableSortState sort)
        {
            return sort.Column switch
            {
                "status" => OrderString(rows, x => x.StatusLabel, sort.Descending),
                "balance" => OrderDecimalNullable(rows, x => x.Balance, sort.Descending),
                "responses" => OrderInt(rows, x => x.Responses, sort.Descending),
                "activity" => OrderDate(rows, x => x.LastActivityUtc, sort.Descending),
                "errors" => OrderInt(rows, x => x.Errors, sort.Descending),
                _ => OrderString(rows, x => x.DisplayName, sort.Descending)
            };
        }
    }

    private static IOrderedEnumerable<T> OrderString<T>(
        IEnumerable<T> rows,
        Func<T, string> key,
        bool descending) =>
        descending
            ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);

    private static IOrderedEnumerable<T> OrderInt<T>(
        IEnumerable<T> rows,
        Func<T, int> key,
        bool descending) =>
        descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static IOrderedEnumerable<T> OrderDecimal<T>(
        IEnumerable<T> rows,
        Func<T, decimal> key,
        bool descending) =>
        descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static IOrderedEnumerable<T> OrderDate<T>(
        IEnumerable<T> rows,
        Func<T, DateTime?> key,
        bool descending) =>
        descending
            ? rows.OrderByDescending(x => key(x) ?? DateTime.MinValue)
            : rows.OrderBy(x => key(x) ?? DateTime.MaxValue);

    private static IOrderedEnumerable<T> OrderDecimalNullable<T>(
        IEnumerable<T> rows,
        Func<T, decimal?> key,
        bool descending) =>
        descending
            ? rows.OrderByDescending(x => key(x) ?? decimal.MinValue)
            : rows.OrderBy(x => key(x) ?? decimal.MaxValue);

    private static IOrderedEnumerable<T> OrderNullableInt<T>(
        IEnumerable<T> rows,
        Func<T, int?> key,
        bool descending) =>
        descending
            ? rows.OrderByDescending(x => key(x) ?? int.MinValue)
            : rows.OrderBy(x => key(x) ?? int.MaxValue);
}