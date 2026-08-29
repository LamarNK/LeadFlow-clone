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
        var blockStart = 0;

        for (var index = 0; index < tokens.Length; index++)
        {
            if (!TryNormalizePhone(tokens[index].Value, out var normalizedPhone))
            {
                continue;
            }

            var candidate = FindCandidate(tokens, blockStart, index);
            if (candidate is null)
            {
                blockStart = index + 1;
                continue;
            }

            // Everything before the selected candidate belongs to the section.
            // Taking the last such line preserves the most specific heading when
            // a file contains several headings before its first candidate.
            if (candidate.Index > blockStart)
            {
                vacancy = Truncate(tokens[candidate.Index - 1].Value, 512);
            }

            if (!seenPhones.Add(normalizedPhone!))
            {
                duplicateRows++;
                blockStart = index + 1;
                continue;
            }

            entries.Add(new CrmLeadFileImportEntry(
                Truncate(candidate.Name, 256),
                tokens[index].Value,
                vacancy,
                tokens[candidate.Index].Line));
            blockStart = index + 1;

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

    private static CandidateMatch? FindCandidate(Token[] tokens, int start, int phoneIndex)
    {
        // Lead files are structured as heading -> candidate -> optional annotations -> phone.
        // Walking backwards prevents a better-capitalized heading/profession from winning over
        // a short, lowercase or otherwise imperfect candidate name closer to the phone.
        for (var index = phoneIndex - 1; index >= start; index--)
        {
            if (TryNormalizeCandidateName(tokens[index].Value, out var normalizedName))
            {
                return new CandidateMatch(index, normalizedName!);
            }
        }

        return null;
    }

    private static bool TryNormalizeCandidateName(string value, out string? normalized)
    {
        normalized = null;
        if (CandidateAnnotationWords().IsMatch(value))
        {
            return false;
        }

        var rawWords = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = rawWords
            .Select(word => word.TrimEnd('.', ',', ';', ':'))
            .ToArray();
        if (words.Length == 0 || words.Length > 6
            || words.Any(word => !word.Any(char.IsLetter)
                                 || word.Any(character => !char.IsLetter(character)
                                                          && character is not '-' and not '\'')))
        {
            return false;
        }

        // Dots and similar separators are common in manually prepared full names
        // ("Иванов. Иван. Иванович"). Do not let them invalidate the name, but avoid
        // treating a single punctuated abbreviation such as "г." as a candidate.
        if (rawWords.Any(word => !string.Equals(word, word.TrimEnd('.', ',', ';', ':'), StringComparison.Ordinal))
            && words.Length < 2)
        {
            return false;
        }

        normalized = string.Join(' ', words);
        return true;
    }

    [GeneratedRegex(@"^\s*\+?[\d\s()\-]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLikeLine();

    [GeneratedRegex(@"\b(?:номер|телефон|контакт|пометк\w*|примечани\w*|комментари\w*|временн\w*|дополнительн\w*|основн\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CandidateAnnotationWords();

    private sealed record CandidateMatch(int Index, string Name);

    private sealed record Token(string Value, int Line);
}

public sealed record CrmLeadFileParseResult(
    IReadOnlyList<CrmLeadFileImportEntry> Entries,
    int DuplicateRowsInFile);
