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

            var candidateIndex = FindCandidateIndex(tokens, blockStart, index);
            if (candidateIndex is null)
            {
                blockStart = index + 1;
                continue;
            }

            // Everything before the selected candidate belongs to the section.
            // Taking the last such line preserves the most specific heading when
            // a file contains several headings before its first candidate.
            if (candidateIndex.Value > blockStart)
            {
                vacancy = Truncate(tokens[candidateIndex.Value - 1].Value, 512);
            }

            if (!seenPhones.Add(normalizedPhone!))
            {
                duplicateRows++;
                blockStart = index + 1;
                continue;
            }

            entries.Add(new CrmLeadFileImportEntry(
                Truncate(tokens[candidateIndex.Value].Value, 256),
                tokens[index].Value,
                vacancy,
                tokens[candidateIndex.Value].Line));
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

    private static int? FindCandidateIndex(Token[] tokens, int start, int phoneIndex)
    {
        int? result = null;
        var bestScore = int.MinValue;

        for (var index = start; index < phoneIndex; index++)
        {
            var score = ScoreCandidateName(tokens[index].Value);
            // On an equal score prefer the line closest to the phone. This keeps
            // one-word names working while an uppercase section remains a heading.
            if (score >= bestScore)
            {
                bestScore = score;
                result = index;
            }
        }

        return bestScore > int.MinValue ? result : null;
    }

    private static int ScoreCandidateName(string value)
    {
        if (CandidateAnnotationWords().IsMatch(value))
        {
            return int.MinValue;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || words.Length > 6
            || words.Any(word => !word.Any(char.IsLetter)
                                 || word.Any(character => !char.IsLetter(character)
                                                          && character is not '-' and not '\'')))
        {
            return int.MinValue;
        }

        var score = Math.Min(words.Length, 4);
        score += words.Count(word => char.IsUpper(word[0]));

        // Section headings are commonly written in uppercase. They remain valid
        // fallbacks, but lose to a following name even when that name has one word.
        if (value.Any(char.IsLetter) && value.Where(char.IsLetter).All(char.IsUpper))
        {
            score -= 2;
        }

        return score;
    }

    [GeneratedRegex(@"^\s*\+?[\d\s()\-]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLikeLine();

    [GeneratedRegex(@"\b(?:номер|телефон|контакт|пометк\w*|примечани\w*|комментари\w*|временн\w*|дополнительн\w*|основн\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CandidateAnnotationWords();

    private sealed record Token(string Value, int Line);
}

public sealed record CrmLeadFileParseResult(
    IReadOnlyList<CrmLeadFileImportEntry> Entries,
    int DuplicateRowsInFile);
