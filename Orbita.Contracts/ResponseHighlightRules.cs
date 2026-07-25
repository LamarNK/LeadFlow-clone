namespace Orbita.Contracts;

public static class ResponseHighlightRules
{
    private static readonly string[] AllowedBuckets =
    [
        "18-24",
        "25-34",
        "35-44",
        "45+",
        "63+"
    ];

    public static IReadOnlyList<string> HighlightAgeBucketOptions => AllowedBuckets;

    public static string NormalizeBucketsCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return string.Empty;
        }

        var selected = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAllowedBucket)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join(',', selected);
    }

    public static bool IsHighlighted(int? age, bool enabled, string? csv, out string? label)
    {
        label = null;
        if (!enabled || age is not int knownAge)
        {
            return false;
        }

        foreach (var bucket in NormalizeBucketsCsv(csv).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MatchesBucket(knownAge, bucket))
            {
                label = bucket;
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedBucket(string bucket) => AllowedBuckets.Contains(bucket, StringComparer.Ordinal);

    private static bool MatchesBucket(int age, string bucket) => bucket switch
    {
        "18-24" => age is >= 18 and <= 24,
        "25-34" => age is >= 25 and <= 34,
        "35-44" => age is >= 35 and <= 44,
        "45+" => age >= 45,
        "63+" => age >= 63,
        _ => false
    };
}
