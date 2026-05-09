using System.Collections.Generic;
using System.Text.RegularExpressions;
using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

/// <summary>
/// Парсит модалку Avito Pro «Выбор профиля» (URL <c>/profile/pro/items#profile/switch?withEntities=true</c>):
/// разбирает карточки <c>data-marker="component-profile-switch/profile-{Id}"</c>, имя в <c>&lt;h5&gt;</c>,
/// категорию в <c>&lt;p&gt;</c> и пометку текущего профиля по классу <c>ProfileCard-module-isCurrent-...</c>.
/// </summary>
public static class AvitoSubProfilesParser
{
    private static readonly Regex CardRegex = new(
        @"data-marker=""component-profile-switch/profile-(?<id>\d+)""(?<rest>[\s\S]*?)(?=data-marker=""component-profile-switch/profile-\d+""|<a\s+data-marker=""component-profile-switch/add""|</body|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsCurrentClassRegex = new(
        @"ProfileCard-module-isCurrent",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex H5NameRegex = new(
        @"<h5\b[^>]*>(?<name>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PCategoryRegex = new(
        @"<p\b[^>]*>(?<cat>[^<]+)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<AvitoSubProfile> Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<AvitoSubProfile>();
        }

        var result = new List<AvitoSubProfile>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in CardRegex.Matches(html))
        {
            var id = match.Groups["id"].Value;
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var nameMatch = H5NameRegex.Match(rest);
            var name = nameMatch.Success ? Decode(nameMatch.Groups["name"].Value).Trim() : string.Empty;
            var categoryMatch = PCategoryRegex.Match(rest);
            var category = categoryMatch.Success ? Decode(categoryMatch.Groups["cat"].Value).Trim() : string.Empty;
            var isCurrent = IsCurrentClassRegex.IsMatch(rest);

            result.Add(new AvitoSubProfile
            {
                Id = id,
                Name = name,
                Category = category,
                IsCurrent = isCurrent
            });
        }

        return result;
    }

    private static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return raw
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal);
    }
}
