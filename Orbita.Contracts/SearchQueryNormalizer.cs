using System.Text;

namespace Orbita.Contracts;

public static class SearchQueryNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    public static IReadOnlyList<string> Tokenize(string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized is null)
        {
            return [];
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string ExtractDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var digits = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
            }
        }

        return digits.ToString();
    }

    public static string EscapeILikeLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    public static string ToILikePattern(string token) => $"%{EscapeILikeLiteral(token)}%";

    public static bool MatchesTokens(string? search, params string?[] fields)
    {
        var tokens = Tokenize(search);
        if (tokens.Count == 0)
        {
            return true;
        }

        var values = fields
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field!.ToLowerInvariant())
            .ToArray();

        if (values.Length == 0)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            var lowered = token.ToLowerInvariant();
            var digits = ExtractDigits(token);
            var matched = values.Any(value =>
                value.Contains(lowered, StringComparison.Ordinal)
                || (digits.Length >= 4 && ExtractDigits(value).Contains(digits, StringComparison.Ordinal)));

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    public static bool MatchesPhone(string? phoneRaw, string? phoneNormalized, string? search)
    {
        var digits = ExtractDigits(search);
        if (digits.Length < 4)
        {
            return false;
        }

        var rawDigits = ExtractDigits(phoneRaw);
        var normalizedDigits = ExtractDigits(phoneNormalized);
        return rawDigits.Contains(digits, StringComparison.Ordinal)
            || normalizedDigits.Contains(digits, StringComparison.Ordinal);
    }
}