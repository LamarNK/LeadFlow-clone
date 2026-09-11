namespace Orbita.Contracts;

/// <summary>
/// Values accepted by the Responses status filter.
/// The default selection deliberately omits duplicate responses.
/// </summary>
public static class ResponseStatusFilterValues
{
    public const string All = "all";
    public const string None = "none";
    public const string ExcludeDuplicates = "exclude-duplicates";
    public const string DefaultSelection = "unique,sent,action_required,error";

    public static readonly IReadOnlyList<string> AllValues =
    [
        "unique",
        "duplicate",
        "sent",
        "action_required",
        "error"
    ];

    public static readonly IReadOnlyList<string> DefaultValues =
    [
        "unique",
        "sent",
        "action_required",
        "error"
    ];

    public static IReadOnlyList<string> Parse(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return DefaultValues;
        }

        var values = status
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (values.Contains(All, StringComparer.Ordinal))
        {
            return AllValues;
        }

        if (values.Contains(None, StringComparer.Ordinal))
        {
            return [];
        }

        if (values.Contains(ExcludeDuplicates, StringComparer.Ordinal))
        {
            return DefaultValues;
        }

        return values
            .Where(value => AllValues.Contains(value, StringComparer.Ordinal))
            .ToList();
    }

    public static string Normalize(string? status)
    {
        var values = Parse(status);
        if (values.Count == 0)
        {
            return None;
        }

        return values.Count == AllValues.Count
            ? All
            : string.Join(',', values);
    }

    public static bool IsDefault(string? status) =>
        Parse(status).SequenceEqual(DefaultValues);
}
