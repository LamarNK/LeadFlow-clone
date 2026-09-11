namespace Orbita.Contracts;

/// <summary>
/// Normalizes Responses catalog filters (worker, account, CRM destination).
/// Empty means "all"; a single legacy query value still works.
/// </summary>
public static class ResponseCatalogFilterValues
{
    public static IReadOnlyList<Guid> MergeIds(IReadOnlyList<Guid>? ids, Guid? single)
    {
        var result = new List<Guid>();
        if (ids is not null)
        {
            foreach (var id in ids)
            {
                if (id != Guid.Empty && !result.Contains(id))
                {
                    result.Add(id);
                }
            }
        }

        if (single is Guid one && one != Guid.Empty && !result.Contains(one))
        {
            result.Add(one);
        }

        return result;
    }

    public static IReadOnlyList<string> MergeValues(IReadOnlyList<string>? values, string? single)
    {
        var result = new List<string>();
        AddValues(result, values);
        AddValue(result, single);
        return result;
    }

    public static string CacheKey(IReadOnlyList<Guid> ids) =>
        ids.Count == 0 ? string.Empty : string.Join(',', ids.OrderBy(id => id));

    public static string CacheKey(IReadOnlyList<string> values) =>
        values.Count == 0
            ? string.Empty
            : string.Join(',', values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static void AddValues(List<string> result, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            AddValue(result, value);
        }
    }

    private static void AddValue(List<string> result, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (!result.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(part);
            }
        }
    }
}
