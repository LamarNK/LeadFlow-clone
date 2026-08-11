using System.Text;

namespace Orbita.Contracts;

/// <summary>
/// Resolves the candidate's current civil time from the city stored in the CRM card.
/// Russia has used fixed civil offsets since 2014, so these mappings deliberately do
/// not depend on the server operating system's time-zone database.
/// </summary>
public static class CrmClientTimeResolver
{
    private sealed record CityOffset(int OffsetMinutes, string[] Tokens);

    // Keep specific localities before broad region names.
    private static readonly IReadOnlyList<CityOffset> CityOffsets =
    [
        new(120, ["калининград", "калининградская область"]),

        new(180, [
            "москва", "московская область", "санкт петербург", "ленинградская область",
            "сочи", "красная поляна", "краснодар", "ростов", "воронеж", "волгоград",
            "белгород", "брянск", "владимир", "вологда", "иваново", "калуга", "киров",
            "кострома", "курск", "липецк", "мурманск", "нижний новгород", "новгород",
            "орел", "псков", "рязань", "смоленск", "тамбов", "тверь", "тула",
            "ярославль", "архангельск", "карелия", "коми", "чувашия", "мордовия",
            "марий эл", "дагестан", "чечня", "ингушетия", "кабардино", "карачаево",
            "северная осетия", "адыгея", "ставрополь", "крым", "севастополь",
            "новодорожкино"
        ]),

        new(240, [
            "самара", "самарская область", "саратов", "саратовская область", "ульяновск",
            "ульяновская область", "ижевск", "удмуртия", "астрахань", "астраханская область"
        ]),

        new(300, [
            "екатеринбург", "свердловская область", "челябинск", "челябинская область",
            "уфа", "башкортостан", "пермь", "пермский край", "тюмень", "тюменская область",
            "оренбург", "оренбургская область", "курган", "курганская область", "ханты мансийск",
            "югра", "ямало ненецкий", "салехард"
        ]),

        new(360, ["омск", "омская область"]),

        new(420, [
            "новосибирск", "новосибирская область", "болотное", "томск", "томская область",
            "кемерово", "кемеровская область", "кузбасс", "новокузнецк", "красноярск",
            "красноярский край", "барнаул", "алтайский край", "горно алтайск", "республика алтай",
            "хакасия", "абакан", "тыва", "тува", "кызыл"
        ]),

        new(480, [
            "иркутск", "иркутская область", "улан удэ", "бурятия", "чита", "забайкальский край"
        ]),

        new(540, [
            "якутск", "саха", "амурская область", "благовещенск", "тында"
        ]),

        new(600, [
            "владивосток", "приморский край", "хабаровск", "хабаровский край", "еврейская автономная"
        ]),

        new(660, [
            "магадан", "магаданская область", "сахалин", "южно сахалинск"
        ]),

        new(720, [
            "камчатка", "петропавловск камчатский", "чукотка", "анадырь"
        ])
    ];

    public static CrmClientTimeDto? Resolve(string? city, DateTime utcNow)
    {
        var normalized = Normalize(city);
        if (normalized.Length == 0)
        {
            return null;
        }

        var match = CityOffsets.FirstOrDefault(entry =>
            entry.Tokens.Any(token => ContainsToken(normalized, token)));
        if (match is null)
        {
            return null;
        }

        var utc = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(utc.AddMinutes(match.OffsetMinutes), DateTimeKind.Unspecified);
        var hours = match.OffsetMinutes / 60;
        var minutes = Math.Abs(match.OffsetMinutes % 60);
        var label = minutes == 0 ? $"UTC{hours:+#;-#;0}" : $"UTC{hours:+#;-#;0}:{minutes:00}";
        return new CrmClientTimeDto(match.OffsetMinutes, local, label, city!.Trim());
    }

    private static bool ContainsToken(string city, string token)
    {
        var normalizedToken = Normalize(token);
        if (city.Equals(normalizedToken, StringComparison.Ordinal))
        {
            return true;
        }

        return $" {city} ".Contains($" {normalizedToken} ", StringComparison.Ordinal)
               || city.Contains(normalizedToken, StringComparison.Ordinal);
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
