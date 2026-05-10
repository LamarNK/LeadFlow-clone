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

    private static bool IsRecruiterCandidatesHref(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return false;
        }

        return href.Contains("cv2Vacancy", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/all/rezume", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Публичная карточка вакансии в URL обычно заканчивается на <c>_{itemId}</c> (или содержит <c>/..._{itemId}</c>).
    /// Ссылка «Подходящие кандидаты» передаёт id в query (<c>cv2Vacancy=</c>) — её отсекаем отдельно.
    /// </summary>
    private static bool ListingHrefContainsItemId(string href, string itemId)
    {
        if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        // .../slug_8126932974 или .../8126932974 — после id должен быть конец или разделитель (не 81269329740).
        for (var i = 0; i < href.Length; i++)
        {
            if (href[i] != '_' && href[i] != '/')
            {
                continue;
            }

            var start = i + 1;
            if (start + itemId.Length > href.Length)
            {
                continue;
            }

            var slice = href.AsSpan(start, itemId.Length);
            if (!slice.Equals(itemId.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var after = start + itemId.Length;
            if (after >= href.Length)
            {
                return true;
            }

            var c = href[after];
            if (c is '?' or '/' or '#' or '&')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPreferredJobListingHref(string href, string itemId) =>
        IsJobSectionListing(href)
        && !IsRecruiterCandidatesHref(href)
        && ListingHrefContainsItemId(href, itemId);

    private static bool IsAcceptableJobListingHref(string href) =>
        IsJobSectionListing(href) && !IsRecruiterCandidatesHref(href);

    private static IEnumerable<string> EnumerateDoubleQuotedHrefs(string snippetHtml)
    {
        foreach (Match m in Regex.Matches(snippetHtml, @"\bhref\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static IEnumerable<string> CollectViewLinkHrefs(string snippetHtml)
    {
        var patterns = new[]
        {
            @"data-marker\s*=\s*""view-link""[^>]*\bhref\s*=\s*""([^""]+)""",
            @"\bhref\s*=\s*""([^""]+)""[^>]*data-marker\s*=\s*""view-link""",
        };

        foreach (var pattern in patterns)
        {
            foreach (Match m in Regex.Matches(snippetHtml, pattern, RegexOptions.IgnoreCase))
            {
                yield return m.Groups[1].Value.Trim();
            }
        }
    }

    private static string? TryPickHref(Func<string, bool> predicate, IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            var t = c.Trim();
            if (string.IsNullOrEmpty(t))
            {
                continue;
            }

            if (predicate(t))
            {
                return t;
            }
        }

        return null;
    }

    private static string? TryLegacyItemPreviewHref(string snippetHtml)
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
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string ExtractItemListingHref(string snippetHtml, string itemId)
    {
        var viewLinkHrefs = CollectViewLinkHrefs(snippetHtml);
        var allHrefs = EnumerateDoubleQuotedHrefs(snippetHtml);

        // 1) Явный view-link и путь карточки с id объявления (отсекаем /all/rezume?cv2Vacancy=… и прочее).
        var preferred = TryPickHref(h => IsPreferredJobListingHref(h, itemId), viewLinkHrefs)
            ?? TryPickHref(h => IsPreferredJobListingHref(h, itemId), allHrefs);

        if (!string.IsNullOrEmpty(preferred))
        {
            return preferred;
        }

        // 2) Первый подходящий view-link без проверки id (старые/редкие шаблоны URL).
        var anyView = TryPickHref(IsAcceptableJobListingHref, viewLinkHrefs);
        if (!string.IsNullOrEmpty(anyView))
        {
            return anyView;
        }

        // 3) Любой /vakansii/|/rabota/ в сниппете, кроме кабинетных «кандидатов».
        var anyJob = TryPickHref(IsAcceptableJobListingHref, allHrefs);
        if (!string.IsNullOrEmpty(anyJob))
        {
            return anyJob;
        }

        // 4) Старые классы превью
        return TryLegacyItemPreviewHref(snippetHtml) ?? "";
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

    /// <summary>
    /// Парсит «Активные» вкладку: счётчики всех вкладок + список активных вакансий.
    /// Заблокированные карточки пропускаем — их парсит <see cref="ParseBlockedTabPage"/> на вкладке «С ошибками».
    /// </summary>
    public ProfileResult ParseProfilePage(string html, Guid? accountId = null)
    {
        var result = new ProfileResult();
        if (string.IsNullOrEmpty(html)) return result;

        // 1️⃣ Счётчики из вкладок (только цифры)
        result.ActiveCount = ExtractCounter(html, "tab(active)");
        result.BlockedCount = ExtractCounter(html, "tab(rejected)");
        result.DraftsCount = ExtractCounter(html, "tab(drafts)");

        // 2️⃣ Парсинг активных объявлений (только вакансии / раздел «Работа» на Авито)
        foreach (var (id, snippetHtml) in EnumerateItemSnippets(html))
        {
            var listingHref = ExtractItemListingHref(snippetHtml, id);
            if (!IsJobSectionListing(listingHref))
            {
                continue;
            }

            // Заблокированные снимки на этой вкладке игнорируем — их везде по пути «С ошибками».
            if (snippetHtml.Contains("styles-status-name_red-", StringComparison.Ordinal))
            {
                continue;
            }

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty };
            ad.Title = ExtractTitle(snippetHtml);
            ad.City = ExtractCity(snippetHtml);
            ad.Url = NormalizeAvitoHref(listingHref);
            FillViewsContactsFavorites(ad, snippetHtml);
            result.ActiveAds.Add(ad);
        }

        return result;
    }

    /// <summary>
    /// Парсит вкладку «С ошибками» (<c>tab(rejected)</c>): возвращает список заблокированных вакансий со статусом и датой удаления.
    /// </summary>
    public IReadOnlyList<AvitoAdStatus> ParseBlockedTabPage(string html, Guid? accountId = null)
    {
        var result = new List<AvitoAdStatus>();
        if (string.IsNullOrEmpty(html)) return result;

        foreach (var (id, snippetHtml) in EnumerateItemSnippets(html))
        {
            var listingHref = ExtractItemListingHref(snippetHtml, id);
            if (!IsJobSectionListing(listingHref))
            {
                continue;
            }

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty };
            ad.Title = ExtractTitle(snippetHtml);
            ad.City = ExtractCity(snippetHtml);
            ad.Url = NormalizeAvitoHref(listingHref);
            ad.Status = ExtractBlockedStatusName(snippetHtml);
            ad.DeleteDate = ExtractBlockedDeleteDate(snippetHtml);
            FillViewsContactsFavorites(ad, snippetHtml);
            result.Add(ad);
        }

        return result;
    }

    /// <summary>
    /// Авито часто отдаёт ссылки в виде <c>//www.avito.ru/...</c> или <c>/profile/...</c>.
    /// Нормализуем в полный <c>https://www.avito.ru/...</c>, чтобы можно было открыть напрямую из UI.
    /// </summary>
    private static string NormalizeAvitoHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;

        var trimmed = href.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://www.avito.ru" + trimmed;
        }

        return trimmed;
    }

    private static IEnumerable<(string Id, string SnippetHtml)> EnumerateItemSnippets(string html)
    {
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

            yield return (id, html.Substring(startIndex, endIndex - startIndex));
        }
    }

    /// <summary>
    /// Извлекает текст из <c>&lt;span class="styles-status-name_red-..."&gt;Заблокировано&lt;/span&gt;</c>.
    /// Если ничего не нашли — возвращаем «Заблокировано» по умолчанию.
    /// </summary>
    private static string ExtractBlockedStatusName(string snippetHtml)
    {
        var match = Regex.Match(
            snippetHtml,
            @"<span[^>]*\bstyles-status-name_red-[^""]*""[^>]*>([^<]+)</span>",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Заблокировано";
    }

    /// <summary>
    /// «Заблокировано, удалится навсегда 22 мая в 20:50» — забираем хвост после запятой.
    /// На реальной странице структура такая:
    /// <code>
    /// &lt;span class="styles-status-EGgGM"&gt;
    ///     &lt;div class="styles-catch-block-EZLET"&gt;
    ///         &lt;span class="styles-status-name_red-..."&gt;Заблокировано&lt;/span&gt;
    ///     &lt;/div&gt;, удалится навсегда 22 мая в 20:50
    /// &lt;/span&gt;
    /// </code>
    /// — после внутренних <c>&lt;/span&gt;&lt;/div&gt;</c> идёт текст с запятой.
    /// </summary>
    private static string ExtractBlockedDeleteDate(string snippetHtml)
    {
        var match = Regex.Match(
            snippetHtml,
            @"styles-status-name_red-[^""]*""[^>]*>[^<]+</span>\s*(?:</div>\s*)?(?<rest>[^<]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        var rest = match.Groups["rest"].Value;
        // Обычно формат «, удалится навсегда 22 мая в 20:50» — убираем ведущую запятую/пробелы.
        return rest.TrimStart(',', ' ', '\t', '\n', '\r').Trim();
    }

    /// <summary>
    /// Достаёт число с счётчика вкладки <c>profile-items-tab/tab(...)</c> (Активные/С ошибками/Черновики и т.п.).
    /// ВАЖНО: маркер вида <c>tab(rejected)</c> содержит литеральные скобки — обязательно экранируем через
    /// <see cref="Regex.Escape(string)"/>, иначе они интерпретируются как regex-группа и счётчик уходит в 0,
    /// а из-за этого мониторинг даже не открывает вкладку «С ошибками».
    /// </summary>
    private int ExtractCounter(string html, string tabMarker)
    {
        var escapedMarker = Regex.Escape(tabMarker);
        var pattern = $@"data-marker=""profile-items-tab/{escapedMarker}"".*?class=""[^""]*styles-module-counter[^""]*"".*?>(\d+)<";
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string ExtractSingle(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
