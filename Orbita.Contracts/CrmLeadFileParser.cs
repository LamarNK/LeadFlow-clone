using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>
/// Parses the simple office lead list: a vacancy heading followed by candidate
/// names and phone numbers. Empty lines are intentionally ignored.
/// </summary>
public static partial class CrmLeadFileParser
{
    public const int MaximumEntries = 1000;

    public static CrmLeadFileParseResult Parse(string? content)
    {
        var tokens = (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select((value, index) => new Token(value.Trim(), index + 1))
            .Where(x => x.Value.Length > 0)
            .ToArray();

        var entries = new List<CrmLeadFileImportEntry>();
        var seenPhones = new HashSet<string>(StringComparer.Ordinal);
        string? vacancy = null;
        var duplicateRows = 0;

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (TryNormalizePhone(token.Value, out _))
            {
                continue;
            }

            string? normalizedPhone = null;
            var nextIsPhone = index + 1 < tokens.Length
                              && TryNormalizePhone(tokens[index + 1].Value, out normalizedPhone);
            if (!nextIsPhone)
            {
                // Any text not immediately followed by a phone is a section
                // heading. This also safely ignores an incomplete candidate row.
                vacancy = Truncate(token.Value, 512);
                continue;
            }

            if (!seenPhones.Add(normalizedPhone!))
            {
                duplicateRows++;
                index++;
                continue;
            }

            entries.Add(new CrmLeadFileImportEntry(
                Truncate(token.Value, 256),
                tokens[index + 1].Value,
                vacancy,
                token.Line));
            index++;

            if (entries.Count > MaximumEntries)
            {
                throw new InvalidOperationException($"В одном файле допускается не более {MaximumEntries} лидов.");
            }
        }

        return new CrmLeadFileParseResult(entries, duplicateRows);
    }

    public static bool TryNormalizePhone(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || !PhoneLikeLine().IsMatch(value.Trim()))
        {
            return false;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits[0] == '9')
        {
            digits = $"7{digits}";
        }
        else if (digits.Length == 11 && digits[0] == '8')
        {
            digits = $"7{digits[1..]}";
        }

        if (digits.Length != 11 || digits[0] != '7')
        {
            return false;
        }

        normalized = digits;
        return true;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    [GeneratedRegex(@"^\s*\+?[\d\s()\-]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLikeLine();

    private sealed record Token(string Value, int Line);
}

public sealed record CrmLeadFileParseResult(
    IReadOnlyList<CrmLeadFileImportEntry> Entries,
    int DuplicateRowsInFile);
