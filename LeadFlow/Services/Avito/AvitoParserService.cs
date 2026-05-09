using LeadFlow.Models;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LeadFlow.Services.Avito;

public class AvitoParserService
{
    /// <summary>
    /// В URL карточки на Авито вакансии и сопутствующие «рабочие» категории попадают
    /// под сегменты <c>/vakansii/</c> (Avito Pro) или <c>/rabota/</c> (старая разметка).
    /// Товары и услуги (не вакансии) учитывать не нужно.
    /// </summary>
    private static bool IsJobSectionListing(string? relativeOrAbsoluteHref)
    {
        if (string.IsNullOrEmpty(relativeOrAbsoluteHref))
        {
            return false;
        }

        return relativeOrAbsoluteHref.Contains("/vakansii/", StringComparison.OrdinalIgnoreCase)
            || relativeOrAbsoluteHref.Contains("/rabota/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractItemListingHref(string snippetHtml)
    {
        // Новая разметка Avito Pro: <a data-marker="view-link" ... href="...">
        var m = Regex.Match(
            snippetHtml,
            @"data-marker=""view-link""[^>]*href=""([^""]+)""",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        m = Regex.Match(
            snippetHtml,
            @"href=""([^""]+)""[^>]*data-marker=""view-link""",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        // Старые варианты разметки
        m = Regex.Match(
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
        // Новая разметка Avito Pro: заголовок внутри <a data-marker="view-link"><span ...>Заголовок</span></a>
        var title = ExtractSingle(
            snippetHtml,
            @"data-marker=""view-link""[^>]*>[\s\S]*?<span[^>]*>([^<]+)</span>");
        if (!string.IsNullOrEmpty(title))
        {
            return title;
        }

        title = ExtractSingle(snippetHtml, @"styles-module-root_preset_black[^>]*>([^<]+)</a>");
        if (!string.IsNullOrEmpty(title))
        {
            return title;
        }

        return ExtractSingle(snippetHtml, @"class=""styles-title-[A-Za-z0-9_-]+""[^>]*>([^<]+)");
    }

    private static string ExtractCity(string snippetHtml)
    {
        var city = ExtractSingle(snippetHtml, @"geo-root-[A-Za-z0-9_-]+[^>]*>[\s\S]*?<span[^>]*>([^<]+)</span>");
        if (!string.IsNullOrEmpty(city))
        {
            return city;
        }

        return ExtractSingle(snippetHtml, @"class=""styles-address-[A-Za-z0-9_-]+""[^>]*>([^<]+)");
    }

    private static int ExtractCounterAfterIcon(string snippetHtml, params string[] iconNames)
    {
        foreach (var iconName in iconNames)
        {
            // Новая разметка: значение в <span>...</span> после <svg data-icon-name="...">
            var spanPattern = $@"data-icon-name=""{Regex.Escape(iconName)}""[\s\S]*?<span[^>]*>\s*(\d+)";
            var m = Regex.Match(snippetHtml, spanPattern, RegexOptions.Singleline);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value);
            }

            // Старая разметка: значение в <p>...</p>
            var pPattern = $@"data-icon-name=""{Regex.Escape(iconName)}""[\s\S]*?<p[^>]*>\s*(\d+)\s*</p>";
            m = Regex.Match(snippetHtml, pPattern, RegexOptions.Singleline);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value);
            }
        }

        return 0;
    }

    private static int ExtractCounterByRoleMarker(string snippetHtml, string roleMarker)
    {
        // Новая Avito Pro разметка: <div role-marker="views"> ... <span ...>2</span> ...
        var pattern = $@"role-marker=""{Regex.Escape(roleMarker)}""[^>]*>[\s\S]*?<span[^>]*>\s*(\d+)";
        var m = Regex.Match(snippetHtml, pattern, RegexOptions.Singleline);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static void FillViewsContactsFavorites(AvitoAdStatus ad, string snippetHtml)
    {
        // Сначала пытаемся новую разметку через role-marker (наиболее надёжно для /profile/pro/items).
        var views = ExtractCounterByRoleMarker(snippetHtml, "views");
        var contacts = ExtractCounterByRoleMarker(snippetHtml, "contacts");
        var favorites = ExtractCounterByRoleMarker(snippetHtml, "favorites");

        // Фолбэк на иконки (поддерживаем и новые, и старые имена иконок).
        if (views == 0)
        {
            views = ExtractCounterAfterIcon(snippetHtml, "visiblefilled", "visibility", "view");
        }
        if (contacts == 0)
        {
            contacts = ExtractCounterAfterIcon(snippetHtml, "user", "person");
        }
        if (favorites == 0)
        {
            favorites = ExtractCounterAfterIcon(snippetHtml, "favoritesfilled", "favorite");
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
}

public class ProfileResult
{
    public int ActiveCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();
}
