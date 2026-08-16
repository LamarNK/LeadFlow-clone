using System.Text;
using System.Text.Json;

namespace Orbita.Contracts;

public sealed record ResponseHighlightTarget(Guid AccountId, string? SubProfileId = null)
{
    public string ToFormValue()
    {
        if (string.IsNullOrWhiteSpace(SubProfileId))
        {
            return AccountId.ToString("N");
        }

        var encodedSubProfileId = Convert.ToBase64String(Encoding.UTF8.GetBytes(SubProfileId.Trim()));
        return $"{AccountId:N}:{encodedSubProfileId}";
    }
}

public static class ResponseHighlightRules
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] AllowedBuckets =
    [
        "18-24",
        "25-34",
        "35-44",
        "45+",
        "63+"
    ];

    public static IReadOnlyList<string> HighlightAgeBucketOptions => AllowedBuckets;

    public static IReadOnlyList<ResponseHighlightTarget> ParseTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<ResponseHighlightTarget>>(json, JsonOptions) ?? [])
                .Where(x => x.AccountId != Guid.Empty)
                .Select(x => new ResponseHighlightTarget(
                    x.AccountId,
                    string.IsNullOrWhiteSpace(x.SubProfileId) ? null : x.SubProfileId.Trim()))
                .Where(x => x.SubProfileId is null or { Length: <= 512 })
                .Distinct()
                .Take(200)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? NormalizeTargetsJson(string? json) => SerializeTargets(ParseTargets(json));

    public static string? NormalizeTargetsFormValues(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var targets = values
            .Select(TryParseFormValue)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .Take(200)
            .ToList();

        return SerializeTargets(targets);
    }

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

    public static bool IsHighlighted(
        int? age,
        bool enabled,
        string? ageBucketsCsv,
        Guid accountId,
        string? subProfileId,
        string? targetsJson,
        out string? label)
    {
        if (IsHighlighted(age, enabled, ageBucketsCsv, out label))
        {
            return true;
        }

        if (!enabled)
        {
            return false;
        }

        foreach (var target in ParseTargets(targetsJson))
        {
            if (target.AccountId != accountId)
            {
                continue;
            }

            if (target.SubProfileId is null)
            {
                label = "Профиль";
                return true;
            }

            if (string.Equals(target.SubProfileId, subProfileId, StringComparison.Ordinal))
            {
                label = "Субпрофиль";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Возвращает все причины, по которым отклик нужно выделить. Новый критерий
    /// добавляется отдельной меткой, не меняя отображение уже существующих.
    /// </summary>
    public static IReadOnlyList<string> GetHighlightLabels(
        int? age,
        bool enabled,
        string? ageBucketsCsv,
        Guid accountId,
        string? subProfileId,
        string? targetsJson)
    {
        if (!enabled)
        {
            return [];
        }

        var labels = new List<string>();
        if (IsHighlighted(age, enabled, ageBucketsCsv, out var ageBucket) && !string.IsNullOrWhiteSpace(ageBucket))
        {
            labels.Add($"Возраст: {ageBucket}");
        }

        foreach (var target in ParseTargets(targetsJson))
        {
            if (target.AccountId != accountId)
            {
                continue;
            }

            if (target.SubProfileId is null)
            {
                labels.Add("Профиль Avito");
                continue;
            }

            if (string.Equals(target.SubProfileId, subProfileId, StringComparison.Ordinal))
            {
                labels.Add("Субпрофиль Avito");
            }
        }

        return labels.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Возвращает ключ автоматически назначаемого цвета для метки отклика.
    /// Палитра совпадает с последовательностью цветов этапов воронки CRM.
    /// </summary>
    public static string GetHighlightTone(string? label) => label switch
    {
        "Возраст: 18-24" => "stage-1",
        "Возраст: 25-34" => "stage-2",
        "Возраст: 35-44" => "stage-3",
        "Возраст: 45+" => "stage-4",
        "Возраст: 63+" => "stage-5",
        "Профиль Avito" => "stage-6",
        "Субпрофиль Avito" => "stage-7",
        _ => "default"
    };

    private static string? SerializeTargets(IReadOnlyList<ResponseHighlightTarget> targets) =>
        targets.Count == 0 ? null : JsonSerializer.Serialize(targets, JsonOptions);

    private static ResponseHighlightTarget? TryParseFormValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (!Guid.TryParseExact(parts[0], "N", out var accountId))
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return new ResponseHighlightTarget(accountId);
        }

        try
        {
            var subProfileId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            return string.IsNullOrWhiteSpace(subProfileId) || subProfileId.Length > 512
                ? null
                : new ResponseHighlightTarget(accountId, subProfileId.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
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
