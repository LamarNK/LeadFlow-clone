using LeadFlow.Models;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LeadFlow.Services.Avito;

public class AvitoParserService
{
    /// <summary>
    /// В URL карточки на Авито вакансии и прочие «рабочие» категории попадают под сегмент <c>/rabota/</c>.
    /// Товары и услуги (не вакансии) учитывать не нужно.
    /// </summary>
    private static bool IsJobSectionListing(string? relativeOrAbsoluteHref)
    {
        if (string.IsNullOrEmpty(relativeOrAbsoluteHref))
        {
            return false;
        }

        return relativeOrAbsoluteHref.Contains("/rabota/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractItemListingHref(string snippetHtml)
    {
        var m = Regex.Match(
            snippetHtml,
            @"class=""item-preview-root[^""]*""\s+href=""([^""]+)""",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        m = Regex.Match(
            snippetHtml,
            @"href=""([^""]+)""[^>]*class=""[^""]*item-preview-root",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        m = Regex.Match(snippetHtml, @"item-preview-root-[A-Za-z0-9_]+[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string ExtractTitle(string snippetHtml)
    {
        var title = ExtractSingle(snippetHtml, @"styles-module-root_preset_black[^>]*>([^<]+)</a>");
        if (!string.IsNullOrEmpty(title))
        {
            return title;
        }

        return ExtractSingle(snippetHtml, @"class=""styles-title-UJzSB"">([^<]+)");
    }

    private static string ExtractCity(string snippetHtml)
    {
        var city = ExtractSingle(snippetHtml, @"geo-root-p3kEY[^>]*>[\s\S]*?<span>([^<]+)</span>");
        if (!string.IsNullOrEmpty(city))
        {
            return city;
        }

        return ExtractSingle(snippetHtml, @"class=""styles-address-I7r1Q"">([^<]+)");
    }

    private static int ExtractCounterAfterIcon(string snippetHtml, string iconName)
    {
        var pattern = $@"data-icon-name=""{Regex.Escape(iconName)}""[\s\S]*?<p[^>]*>(\d+)</p>";
        var m = Regex.Match(snippetHtml, pattern, RegexOptions.Singleline);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static void FillViewsContactsFavorites(AvitoAdStatus ad, string snippetHtml)
    {
        var views = ExtractCounterAfterIcon(snippetHtml, "visibility");
        var contacts = ExtractCounterAfterIcon(snippetHtml, "person");
        var favorites = ExtractCounterAfterIcon(snippetHtml, "favorite");

        if (views == 0 && contacts == 0)
        {
            views = ParseInt(ExtractSingle(snippetHtml, @"role-marker=""views"">.*?<span[^>]*>(\d+)"));
            contacts = ParseInt(ExtractSingle(snippetHtml, @"role-marker=""contacts"">.*?<span[^>]*>(\d+)"));
        }

        ad.Views = views;
        ad.Contacts = contacts;
        ad.Favorites = favorites;
    }

    public ProfileResult ParseProfilePage(string html, Guid? accountId = null)
    {
        var result = new ProfileResult();
        if (string.IsNullOrEmpty(html)) return result;

        // 1️⃣ Счётчики из вкладок (только цифры)
        result.ActiveCount = ExtractCounter(html, "tab(active)");
        result.BlockedCount = ExtractCounter(html, "tab(rejected)");
        result.DraftsCount = ExtractCounter(html, "tab(drafts)");

        // 2️⃣ Парсинг активных объявлений (только вакансии / раздел «Работа» на Авито)
        var snippetMatches = Regex.Matches(html, @"data-marker=""item-snippet/(\d+)""");

        for (var i = 0; i < snippetMatches.Count; i++)
        {
            var match = snippetMatches[i];
            if (!match.Success) continue;
            var id = match.Groups[1].Value;

            var startIndex = match.Index;
            var endIndex = i + 1 < snippetMatches.Count
                ? snippetMatches[i + 1].Index
                : Math.Min(startIndex + 12000, html.Length);

            var snippetHtml = html.Substring(startIndex, endIndex - startIndex);

            var listingHref = ExtractItemListingHref(snippetHtml);
            if (!IsJobSectionListing(listingHref))
            {
                continue;
            }

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty };
            ad.Title = ExtractTitle(snippetHtml);
            ad.City = ExtractCity(snippetHtml);

            // Пропускаем заблокированные
            if (snippetHtml.Contains("styles-status-name_red-", StringComparison.Ordinal))
            {
                continue;
            }

            FillViewsContactsFavorites(ad, snippetHtml);

            result.ActiveAds.Add(ad);
        }

        return result;
    }

    private int ExtractCounter(string html, string tabMarker)
    {
        var pattern = $@"data-marker=""profile-items-tab/{tabMarker}"".*?class=""[^""]*styles-module-counter[^""]*"".*?>(\d+)<";
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string ExtractSingle(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;
}

public class ProfileResult
{
    public int ActiveCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();
}
