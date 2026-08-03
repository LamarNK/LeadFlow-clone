using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public sealed record BitrixAvitoCommentData(
    int? Age,
    string Profession,
    string City);

public static partial class BitrixAvitoCommentParser
{
    public static BitrixAvitoCommentData Parse(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments))
        {
            return new(null, string.Empty, string.Empty);
        }

        var ageValue = Extract(comments, "Возраст");
        int? age = int.TryParse(
            ageValue,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedAge)
            && parsedAge is > 0 and <= 120
                ? parsedAge
                : null;

        return new(
            age,
            Extract(comments, "Вакансия"),
            Extract(comments, "Город"));
    }

    private static string Extract(string comments, string fieldName)
    {
        var match = FieldLineRegex().Match(comments);
        while (match.Success)
        {
            if (string.Equals(
                    match.Groups["name"].Value.Trim(),
                    fieldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["value"].Value.Trim();
            }

            match = match.NextMatch();
        }

        return string.Empty;
    }

    [GeneratedRegex(
        @"^\s*(?<name>Возраст|Вакансия|Город)\s*:\s*(?<value>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FieldLineRegex();
}
