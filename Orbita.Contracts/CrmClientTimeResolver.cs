using System.IO.Compression;
using System.Text;

namespace Orbita.Contracts;

/// <summary>
/// Resolves the candidate's current civil time from the locality stored in the CRM card.
/// The embedded dictionary is generated from GeoNames and contains only normalized
/// locality aliases and the corresponding fixed Russian civil offset.
/// </summary>
public static class CrmClientTimeResolver
{
    private const string ResourceName = "Orbita.Contracts.Data.russian-place-timezones.tsv.gz";

    private static readonly IReadOnlyDictionary<string, int> FallbackOffsets =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["калининград"] = 120,
            ["москва"] = 180,
            ["санкт петербург"] = 180,
            ["новодорожкино"] = 180,
            ["самара"] = 240,
            ["екатеринбург"] = 300,
            ["омск"] = 360,
            ["новосибирск"] = 420,
            ["болотное"] = 420,
            ["тында"] = 540,
            ["владивосток"] = 600,
            ["магадан"] = 660,
            ["анадырь"] = 720
        };

    private static readonly string[] LocalityPrefixes =
    [
        "поселок городского типа",
        "рабочий поселок",
        "населенный пункт",
        "городской поселок",
        "железнодорожная станция",
        "станция",
        "деревня",
        "поселок",
        "станица",
        "хутор",
        "село",
        "аул",
        "улус",
        "город",
        "пгт",
        "рп",
        "нп",
        "г"
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, int>> CityOffsets =
        new(LoadOffsets, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CrmClientTimeDto? Resolve(string? city, DateTime utcNow)
    {
        if (!TryResolveOffset(city, out var offsetMinutes))
        {
            return null;
        }

        var utc = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(utc.AddMinutes(offsetMinutes), DateTimeKind.Unspecified);
        var hours = offsetMinutes / 60;
        var minutes = Math.Abs(offsetMinutes % 60);
        var label = minutes == 0 ? $"UTC{hours:+#;-#;0}" : $"UTC{hours:+#;-#;0}:{minutes:00}";
        return new CrmClientTimeDto(offsetMinutes, local, label, city!.Trim());
    }

    private static bool TryResolveOffset(string? city, out int offsetMinutes)
    {
        offsetMinutes = default;
        if (string.IsNullOrWhiteSpace(city))
        {
            return false;
        }

        var offsets = CityOffsets.Value;
        foreach (var candidate in GetLookupCandidates(city))
        {
            if (offsets.TryGetValue(candidate, out offsetMinutes))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetLookupCandidates(string value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawCandidate in EnumerateRawCandidates(value))
        {
            var normalized = Normalize(rawCandidate);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                yield return normalized;
            }

            var withoutPrefix = RemoveLocalityPrefix(normalized);
            if (withoutPrefix.Length > 0 && seen.Add(withoutPrefix))
            {
                yield return withoutPrefix;
            }
        }
    }

    private static IEnumerable<string> EnumerateRawCandidates(string value)
    {
        yield return value;

        foreach (var segment in value.Split([',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return segment;
        }
    }

    private static string RemoveLocalityPrefix(string value)
    {
        var result = value;
        var removed = true;
        while (removed)
        {
            removed = false;
            foreach (var prefix in LocalityPrefixes)
            {
                if (result.Equals(prefix, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (!result.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    continue;
                }

                result = result[(prefix.Length + 1)..].TrimStart();
                removed = true;
                break;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, int> LoadOffsets()
    {
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in FallbackOffsets)
        {
            offsets[pair.Key] = pair.Value;
        }

        try
        {
            using var resource = typeof(CrmClientTimeResolver).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return offsets;
            }

            using var gzip = new GZipStream(resource, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.LastIndexOf('\t');
                if (separator <= 0 || !int.TryParse(line.AsSpan(separator + 1), out var offsetMinutes))
                {
                    continue;
                }

                offsets[line[..separator]] = offsetMinutes;
            }
        }
        catch (InvalidDataException)
        {
            // Keep the small built-in fallback if a deployment contains a damaged resource.
        }
        catch (IOException)
        {
            // Keep the application usable if the embedded resource cannot be read.
        }

        return offsets;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousSpace = true;
        foreach (var raw in value.Trim().ToLowerInvariant())
        {
            var ch = raw == 'ё' ? 'е' : raw;
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                builder.Append(' ');
                previousSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
