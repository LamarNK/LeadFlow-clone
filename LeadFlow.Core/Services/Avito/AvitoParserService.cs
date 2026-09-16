using LeadFlow.Core.Models;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

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

    /// <summary>
    /// Прямая ссылка на объявление только из <c>a[data-marker="view-link"]</c>.
    /// Другие ссылки карточки (кандидаты, чаты) игнорируются. URL не выдумывается.
    /// </summary>
    public static string ExtractListingUrl(string snippetHtml, out string? parseError)
    {
        parseError = null;
        if (string.IsNullOrWhiteSpace(snippetHtml))
        {
            parseError = "missing_view_link";
            return string.Empty;
        }

        var match = Regex.Match(
            snippetHtml,
            """<a\b[^>]*data-marker\s*=\s*["']view-link["'][^>]*\bhref\s*=\s*["'](?<href>[^"']+)["']""",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(
                snippetHtml,
                """<a\b[^>]*\bhref\s*=\s*["'](?<href>[^"']+)["'][^>]*data-marker\s*=\s*["']view-link["']""",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            parseError = "missing_view_link";
            return string.Empty;
        }

        var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
        if (string.IsNullOrWhiteSpace(href))
        {
            parseError = "empty_view_link_href";
            return string.Empty;
        }

        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUrl))
        {
            return absoluteUrl.ToString();
        }

        if (Uri.TryCreate(new Uri("https://www.avito.ru"), href, out var resolvedUrl))
        {
            return resolvedUrl.ToString();
        }

        parseError = "invalid_view_link_href";
        return string.Empty;
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
        if (!string.IsNullOrEmpty(city) && !LooksLikeStreetAddress(city))
        {
            return city;
        }

        var fromAddressClass = ExtractSingle(snippetHtml, @"class=""styles-address-[A-Za-z0-9_-]+""[^>]*>([^<]+)");
        if (!string.IsNullOrEmpty(fromAddressClass) && !LooksLikeStreetAddress(fromAddressClass))
        {
            return fromAddressClass;
        }

        return string.Empty;
    }

    private static string ExtractAddressText(string snippetHtml)
    {
        var address = ExtractSingle(snippetHtml, @"class=""styles-address-[A-Za-z0-9_-]+""[^>]*>([^<]+)");
        return LooksLikeStreetAddress(address) ? address : string.Empty;
    }

    private static string ExtractDistrictText(string snippetHtml)
    {
        foreach (Match leaf in Regex.Matches(
                     snippetHtml,
                     @"<(?:span|div)[^>]*>(?<text>[^<]+)</(?:span|div)>",
                     RegexOptions.IgnoreCase))
        {
            var text = NormalizeSpaces(leaf.Groups["text"].Value);
            if (Regex.IsMatch(text, @"^р-н\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeStreetAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Regex.IsMatch(
            text.Trim(),
            @"^(?:ул\.|улица\b|пр\.|пр-т\b|проспект\b|пер\.|переулок\b|ш\.|шоссе\b|наб\.|бул\.|пл\.|мкр\.?|проезд\b|тупик\b|линия\b|д\.\s*\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeSpaces(string text)
    {
        text = WebUtility.HtmlDecode(text ?? string.Empty);
        return Regex.Replace(text, @"\s+", " ").Trim();
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

    private static void FillPresentationFields(AvitoAdStatus ad, string snippetHtml)
    {
        var image = Regex.Match(
            snippetHtml,
            @"background-image\s*:\s*url\([^\r\n]*?(?<url>(?://|https?:)[^&""')]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (image.Success)
        {
            var imageUrl = WebUtility.HtmlDecode(image.Groups["url"].Value).Trim(' ', '\'', '"');
            ad.ImageUrl = imageUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + imageUrl : imageUrl;
        }

        var plain = Regex.Replace(snippetHtml, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = NormalizeSpaces(plain);
        var salary = Regex.Match(
            plain,
            @"(?<salary>(?:от\s*)?[\d\s\u00A0\u202F]{2,}(?:[–—-]\s*[\d\s\u00A0\u202F]+)?\s*₽(?:\s*(?:за\s+месяц|в\s+месяц))?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (salary.Success)
        {
            ad.Salary = NormalizeSpaces(salary.Groups["salary"].Value);
        }
    }

    private static void FillAgeAndStatus(AvitoAdStatus ad, string snippetHtml, DateTime capturedAtUtc)
    {
        var publishedText = ExtractRoleMarkerInnerText(snippetHtml, "offer/days-published");
        var age = TryParseAgeDays(publishedText) ?? TryParseAgeDays(snippetHtml);
        if (age is int days)
        {
            ad.DaysOnAvito = days;
            ad.HasDaysOnAvito = true;
        }

        var status = ExtractVisibleStatusText(snippetHtml);
        if (!string.IsNullOrWhiteSpace(status))
        {
            ad.Status = status;
        }

        if (TryParseActiveListExpiry(
                snippetHtml,
                capturedAtUtc,
                out var expiresAtUtc,
                out var remainingDays,
                out var expiryError))
        {
            ad.ExpiresAtUtc = expiresAtUtc;
            ad.RemainingDays = remainingDays;
        }
        else if (ExpectsActiveListExpiry(ad))
        {
            ad.ExpiryParseError = expiryError;
        }
    }

    /// <summary>
    /// Карточка в состоянии, в котором Avito обязан показывать «ещё N дней — до дата».
    /// У истёкших, снятых с публикации и неопубликованных карточек даты в разметке нет —
    /// это не ошибка парсинга.
    /// </summary>
    private static bool ExpectsActiveListExpiry(AvitoAdStatus ad) =>
        string.Equals(ad.SourceTab, AvitoAdStatus.ActiveTab, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(ad.Status) || string.Equals(ad.Status, "Активно", StringComparison.Ordinal));

    private static string ExtractRoleMarkerInnerText(string html, string roleMarker)
    {
        var match = Regex.Match(
            html,
            $@"role-marker=""{Regex.Escape(roleMarker)}""[^>]*>([\s\S]*?)</(?:div|span)>",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        var inner = Regex.Replace(match.Groups[1].Value, "<[^>]+>", " ");
        return NormalizeSpaces(inner);
    }

    internal static string ExtractVisibleStatusText(string snippetHtml)
    {
        if (string.IsNullOrWhiteSpace(snippetHtml))
        {
            return string.Empty;
        }

        foreach (Match leaf in Regex.Matches(
                     snippetHtml,
                     @"<(?:span|div)[^>]*>(?<text>[^<]+)</(?:span|div)>",
                     RegexOptions.IgnoreCase))
        {
            if (TryMatchListingStatus(leaf.Groups["text"].Value, out var status))
            {
                return status;
            }
        }

        var flattened = Regex.Replace(snippetHtml, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        flattened = Regex.Replace(flattened, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        flattened = Regex.Replace(flattened, "<[^>]+>", " ");
        return TryMatchListingStatus(flattened, out var fallback) ? fallback : string.Empty;
    }

    private static bool TryMatchListingStatus(string raw, out string status)
    {
        status = string.Empty;
        var text = NormalizeSpaces(raw);
        if (text.Length == 0)
        {
            return false;
        }

        if (!Regex.IsMatch(
                text,
                @"^(?:Скрыто\s*:|Остановлено\s*:|Заблокировано\b|На модерации\b|Отклонено\b|Снято с публикации\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        status = ClipListingStatus(text);
        return !string.IsNullOrWhiteSpace(status);
    }

    private static string ClipListingStatus(string text)
    {
        var stop = Regex.Match(
            text,
            @"\s+(?:\d+\s+(?:день|дня|дней)\s+на\s+Авито|нет новых чатов|Редактировать|Снять с публикации|Поднять просмотры|Запустить рассылку|Продвинуть)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (stop.Success)
        {
            text = text[..stop.Index];
        }

        text = text.Trim().TrimEnd(',', ';');
        if (text.Length > 120)
        {
            text = text[..120].Trim();
        }

        return text;
    }

    private static bool IsRejectedActiveListing(string status) =>
        status.StartsWith("Заблокировано", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Отклонено", StringComparison.OrdinalIgnoreCase);

    private static string ExtractMarkerInnerText(string html, string marker)
    {
        var match = Regex.Match(
            html,
            $@"data-marker=""{Regex.Escape(marker)}""[^>]*>([\s\S]*?)</(?:span|div|h1)>",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        var inner = Regex.Replace(match.Groups[1].Value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(inner).Trim();
    }

    /// <summary>
    /// Парсит «Активные» вкладку: счётчики всех вкладок + список активных вакансий.
    /// Заблокированные карточки пропускаем — их парсит <see cref="ParseBlockedTabPage"/> на вкладке «С ошибками».
    /// </summary>
    public ProfileResult ParseProfilePage(
        string html,
        Guid? accountId = null,
        DateTime? capturedAtUtc = null)
    {
        var result = new ProfileResult();
        if (string.IsNullOrEmpty(html))
        {
            result.ParseSuccess = false;
            result.ParseFailureReason = "empty_html";
            result.LayoutKind = AvitoProVacancyLayout.NotApplicable;
            return result;
        }

        result.LayoutKind = AvitoProVacancyLayout.Classify(html);
        if (!AvitoProVacancyLayout.IsSupported(result.LayoutKind))
        {
            result.ParseSuccess = false;
            result.ParseFailureReason = result.LayoutKind;
            return result;
        }

        result.ParseSuccess = true;
        result.PageLoadedSuccessfully = true;
        var capturedAt = capturedAtUtc ?? DateTime.UtcNow;

        // 1️⃣ Счётчики из вкладок (только цифры). Для «Активных» фиксируем, найден ли счётчик в HTML — иначе 0 ненадёжен.
        var (activeTabOk, activeCount) = TryExtractCounter(html, "tab(active)");
        result.ActiveTabCounterResolved = activeTabOk;
        result.ActiveCount = activeCount;
        result.BlockedCount = ExtractCounter(html, "tab(rejected)");
        result.UnpublishedCount = ExtractCounter(html, "tab(inactive)");
        result.DraftsCount = ExtractCounter(html, "tab(drafts)");

        // 2️⃣ Парсинг активных объявлений (только вакансии / раздел «Работа» на Авито)
        foreach (var (id, snippetHtml) in EnumerateItemSnippets(html))
        {
            result.ItemSnippetMarkersFound++;
            var listingUrl = ExtractListingUrl(snippetHtml, out var urlError);
            if (!string.IsNullOrEmpty(listingUrl) && !IsJobSectionListing(listingUrl))
            {
                continue;
            }

            var status = ExtractVisibleStatusText(snippetHtml);
            if (IsRejectedActiveListing(status))
            {
                continue;
            }

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty };
            ad.Title = ExtractTitle(snippetHtml);
            ad.City = ExtractCity(snippetHtml);
            ad.AddressText = ExtractAddressText(snippetHtml);
            ad.DistrictText = ExtractDistrictText(snippetHtml);
            ad.Url = listingUrl;
            ad.UrlParseError = urlError;
            FillViewsContactsFavorites(ad, snippetHtml);
            FillPresentationFields(ad, snippetHtml);
            FillAgeAndStatus(ad, snippetHtml, capturedAt);
            if (!string.IsNullOrWhiteSpace(status))
            {
                ad.Status = status;
            }

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
            var listingUrl = ExtractListingUrl(snippetHtml, out var urlError);
            if (string.IsNullOrEmpty(listingUrl))
            {
                listingUrl = NormalizeAvitoHref(ExtractItemListingHref(snippetHtml, id));
            }

            if (!IsJobSectionListing(listingUrl))
            {
                continue;
            }

            var ad = new AvitoAdStatus { Id = id, AccountId = accountId ?? Guid.Empty, SourceTab = AvitoAdStatus.ErrorTab };
            ad.Title = ExtractTitle(snippetHtml);
            ad.City = ExtractCity(snippetHtml);
            ad.AddressText = ExtractAddressText(snippetHtml);
            ad.DistrictText = ExtractDistrictText(snippetHtml);
            ad.Url = listingUrl;
            ad.UrlParseError = urlError;
            ad.Status = ExtractBlockedStatusName(snippetHtml);
            ad.ErrorReason = ExtractErrorReason(snippetHtml, ad.Status);
            ad.DeleteDate = ExtractBlockedDeleteDate(snippetHtml);
            FillViewsContactsFavorites(ad, snippetHtml);
            FillPresentationFields(ad, snippetHtml);
            FillAgeAndStatus(ad, snippetHtml, DateTime.UtcNow);
            if (string.IsNullOrWhiteSpace(ad.Status) || ad.Status == "Активно")
            {
                ad.Status = ExtractBlockedStatusName(snippetHtml);
            }

            result.Add(ad);
        }

        return result;
    }

    /// <summary>
    /// Парсит вкладку «Неопубликованные» (<c>tab(inactive)</c>). Карточка сохраняется даже
    /// если причина отсутствует: Avito иногда отдаёт только статус «Истёк срок размещения».
    /// Наличие <c>data-marker="publish-action"</c> используется как безопасная capability для
    /// последующего явного продления/публикации, но само действие здесь не выполняется.
    /// </summary>
    public IReadOnlyList<AvitoAdStatus> ParseUnpublishedTabPage(string html, Guid? accountId = null)
    {
        var result = new List<AvitoAdStatus>();
        if (string.IsNullOrWhiteSpace(html)) return result;

        foreach (var (id, snippetHtml) in EnumerateItemSnippets(html))
        {
            var listingUrl = ExtractListingUrl(snippetHtml, out var urlError);
            if (string.IsNullOrEmpty(listingUrl))
            {
                listingUrl = NormalizeAvitoHref(ExtractItemListingHref(snippetHtml, id));
            }

            // Не теряем карточку из-за неполной/новой URL-разметки: сохраняем её
            // с диагностикой, чтобы UI и последующий ручной разбор увидели причину.
            if (!IsJobSectionListing(listingUrl))
            {
                urlError ??= string.IsNullOrWhiteSpace(listingUrl)
                    ? "listing_url_missing"
                    : "listing_url_not_job_section";
            }

            var status = ExtractUnpublishedStatusText(snippetHtml);
            var ad = new AvitoAdStatus
            {
                Id = id,
                AccountId = accountId ?? Guid.Empty,
                SourceTab = AvitoAdStatus.UnpublishedTab,
                Title = ExtractTitle(snippetHtml),
                City = ExtractCity(snippetHtml),
                AddressText = ExtractAddressText(snippetHtml),
                DistrictText = ExtractDistrictText(snippetHtml),
                Url = listingUrl,
                UrlParseError = urlError,
                Status = string.IsNullOrWhiteSpace(status) ? "Не опубликовано" : status,
                ErrorReason = ExtractErrorReason(snippetHtml, status),
                CanPublish = Regex.IsMatch(snippetHtml, @"data-marker=""publish-action""", RegexOptions.IgnoreCase)
            };
            FillViewsContactsFavorites(ad, snippetHtml);
            FillPresentationFields(ad, snippetHtml);
            FillAgeAndStatus(ad, snippetHtml, DateTime.UtcNow);
            result.Add(ad);
        }

        return result;
    }

    public IReadOnlyList<AvitoAdListCard> ToListCards(ProfileResult profile)
    {
        if (profile.ActiveAds.Count == 0)
        {
            return [];
        }

        return ToListCards(profile.ActiveAds);
    }

    public IReadOnlyList<AvitoAdListCard> ToListCards(IEnumerable<AvitoAdStatus> ads)
    {
        return ads
            .Where(static ad => !string.IsNullOrWhiteSpace(ad.Id))
            .Select(ad => new AvitoAdListCard
            {
                AvitoItemId = ad.Id,
                Title = ad.Title,
                Href = ad.ExplicitListingUrl,
                Url = ad.ExplicitListingUrl,
                UrlParseError = ad.UrlParseError,
                AgeDays = ad.HasDaysOnAvito ? ad.DaysOnAvito : null,
                ExpiresAtUtc = ad.ExpiresAtUtc,
                RemainingDays = ad.RemainingDays,
                ExpiryParseError = ad.ExpiryParseError,
                StatusText = string.Equals(ad.Status, "Активно", StringComparison.Ordinal) ? string.Empty : ad.Status,
                SourceTab = ad.SourceTab,
                ErrorReason = ad.ErrorReason,
                CanPublish = ad.CanPublish,
                ImageUrl = ad.ImageUrl,
                Salary = ad.Salary,
                City = ad.City,
                AddressText = ad.AddressText,
                DistrictText = ad.DistrictText,
                Views = ad.Views,
                Contacts = ad.Contacts,
                Favorites = ad.Favorites
            })
            .ToList();
    }

    public AvitoAdDetailParseResult ParseItemDetailPage(string html, DateTime capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return AvitoAdPublicationDateParser.ToFailed("empty_detail_html");
        }

        var title = ExtractSingle(html, @"data-marker=""item-view/title-info""[^>]*>([^<]+)");
        var itemIdText = ExtractMarkerInnerText(html, "item-view/item-id");
        var lifeBarText = ExtractMarkerInnerText(html, "item-lifebar");
        var remainingDays = TryParseRemainingDays(lifeBarText);

        if (!AvitoAdPublicationDateParser.TryParseItemIdLine(
                itemIdText,
                capturedAtUtc,
                out var itemId,
                out var publishedAtUtc,
                out var error))
        {
            return new AvitoAdDetailParseResult
            {
                Success = false,
                FailureReason = error ?? "publication_date_unparsed",
                AvitoItemId = itemId,
                Title = title,
                RemainingDays = remainingDays,
                PublicationDateSource = AvitoAdPublicationDateSources.Unknown,
                RawItemIdText = itemIdText,
                RawLifeBarText = lifeBarText
            };
        }

        return new AvitoAdDetailParseResult
        {
            Success = true,
            AvitoItemId = itemId,
            Title = title,
            PublishedAtUtc = publishedAtUtc,
            PublicationDateSource = AvitoAdPublicationDateSources.Exact,
            RemainingDays = remainingDays,
            RawItemIdText = itemIdText,
            RawLifeBarText = lifeBarText
        };
    }

    public static int? TryParseAgeDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"(?<days>\d+)\s*(?:день|дня|дней)\s+на\s+Авито",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["days"].Value, out var days) ? days : null;
    }

    public static int? TryParseRemainingDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"Остал(?:ось|ся|ись)\s+(?<days>\d+)\s*(?:день|дня|дней)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["days"].Value, out var days) ? days : null;
    }

    private static string ExtractErrorReason(string snippetHtml, string? status)
    {
        var plain = Regex.Replace(snippetHtml ?? string.Empty, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = NormalizeSpaces(plain);

        var match = Regex.Match(plain,
            @"(?:причина|ошибка\s+(?:публикации|автопубликации)|не\s+удалось\s+опубликовать)\s*[:—-]?\s*(?<reason>[^.!?]{3,180})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups["reason"].Value.Trim();

        return status is not null && (status.Contains("отклон", StringComparison.OrdinalIgnoreCase)
            || status.Contains("ошиб", StringComparison.OrdinalIgnoreCase)
            || status.Contains("заблок", StringComparison.OrdinalIgnoreCase))
            ? status.Trim()
            : string.Empty;
    }

    private static string ExtractUnpublishedStatusText(string snippetHtml)
    {
        var plain = Regex.Replace(snippetHtml ?? string.Empty, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = NormalizeSpaces(plain);

        var match = Regex.Match(
            plain,
            @"(?<status>Истёк\s+срок\s+размещения|Снято\s+с\s+публикации|Ошибки\s+автопубликации|Ожидает\s+публикации|На\s+проверке|Не\s+опубликовано)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["status"].Value.Trim() : string.Empty;
    }

    /// <summary>
    /// Parses the exact expiry displayed in an active list card, for example:
    /// «Активно ещё 13 дней — до 26 сен, 10:23».
    /// </summary>
    public static bool TryParseActiveListExpiry(
        string? htmlOrText,
        DateTime capturedAtUtc,
        out DateTime expiresAtUtc,
        out int? remainingDays,
        out string? error)
    {
        expiresAtUtc = default;
        remainingDays = null;
        error = null;
        if (string.IsNullOrWhiteSpace(htmlOrText))
        {
            error = "list_expiry_text_empty";
            return false;
        }

        var plain = Regex.Replace(htmlOrText, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = NormalizeSpaces(plain);

        var match = Regex.Match(
            plain,
            @"(?:ещё\s+)?(?<remaining>\d+)\s*(?:день|дня|дней)\s*[—–-]\s*до\s*(?<day>\d{1,2})\s+(?<month>янв(?:аря)?|фев(?:раля)?|мар(?:та)?|апр(?:еля)?|мая|май|июн(?:я)?|июл(?:я)?|авг(?:уста)?|сен(?:тября)?|окт(?:ября)?|ноя(?:бря)?|дек(?:абря)?)\s*,?\s*(?<hour>\d{1,2}):(?<minute>\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            error = "list_expiry_unparsed";
            return false;
        }

        if (!int.TryParse(match.Groups["remaining"].Value, out var parsedRemaining)
            || !int.TryParse(match.Groups["day"].Value, out var day)
            || !int.TryParse(match.Groups["hour"].Value, out var hour)
            || !int.TryParse(match.Groups["minute"].Value, out var minute))
        {
            error = "list_expiry_numbers_unparsed";
            return false;
        }

        var month = match.Groups["month"].Value.ToLowerInvariant() switch
        {
            "янв" or "января" => 1,
            "фев" or "февраля" => 2,
            "мар" or "марта" => 3,
            "апр" or "апреля" => 4,
            "май" or "мая" => 5,
            "июн" or "июня" => 6,
            "июл" or "июля" => 7,
            "авг" or "августа" => 8,
            "сен" or "сентября" => 9,
            "окт" or "октября" => 10,
            "ноя" or "ноября" => 11,
            "дек" or "декабря" => 12,
            _ => 0
        };
        if (month == 0)
        {
            error = "list_expiry_unknown_month";
            return false;
        }

        var localNow = AvitoAdBusinessTime.LocalNow(capturedAtUtc);
        try
        {
            var localExpiry = new DateTime(
                localNow.Year,
                month,
                day,
                hour,
                minute,
                0,
                DateTimeKind.Unspecified);

            if (localExpiry.Date < localNow.Date)
            {
                localExpiry = localExpiry.AddYears(1);
            }

            expiresAtUtc = AvitoAdBusinessTime.ToUtc(localExpiry);
            remainingDays = parsedRemaining;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "list_expiry_invalid_calendar_date";
            return false;
        }
    }

    public static string ExtractItemSnippetHtml(string html, string itemId, int maxLength = 6000)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(itemId) || maxLength <= 0)
        {
            return string.Empty;
        }

        foreach (var (id, snippetHtml) in EnumerateItemSnippets(html))
        {
            if (!string.Equals(id, itemId, StringComparison.Ordinal))
            {
                continue;
            }

            return snippetHtml.Length <= maxLength
                ? snippetHtml
                : snippetHtml[..maxLength] + "\n<!-- truncated -->";
        }

        return string.Empty;
    }

    public static string ExtractActiveListStatusLine(string? htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText))
        {
            return string.Empty;
        }

        var plain = Regex.Replace(htmlOrText, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = NormalizeSpaces(plain);

        var match = Regex.Match(
            plain,
            @"(?<status>(?:Активно|Скрыто\s*:|Остановлено\s*:|Заблокировано\b|На модерации\b|Отклонено\b|Снято с публикации\b)[\s\S]{0,180}?)(?=\s+\d+\s+(?:день|дня|дней)\s+на\s+Авито|\s+нет новых чатов|\s+Редактировать|\s+Снять с публикации|\s+Продвинуть|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? match.Groups["status"].Value.Trim() : string.Empty;
    }

    public static string ExtractActiveListExpiryText(string? htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText))
        {
            return string.Empty;
        }

        var plain = Regex.Replace(htmlOrText, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = NormalizeSpaces(plain);

        var match = Regex.Match(
            plain,
            @"(?:ещё\s+)?\d+\s*(?:день|дня|дней)\s*[—–-]\s*до\s*\d{1,2}\s+[А-Яа-яЁё.]+\s*,?\s*\d{1,2}:\d{2}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? match.Value.Trim() : string.Empty;
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
                : html.Length;

            yield return (id, html.Substring(startIndex, endIndex - startIndex));
        }
    }

    /// <summary>
    /// Извлекает текст из <c>&lt;span class="styles-status-name_red-..."&gt;Заблокировано&lt;/span&gt;</c>.
    /// Если ничего не нашли — возвращаем «Заблокировано» по умолчанию.
    /// </summary>
    private static string ExtractBlockedStatusName(string snippetHtml)
    {
        var status = ExtractVisibleStatusText(snippetHtml);
        if (!string.IsNullOrWhiteSpace(status))
        {
            return status.StartsWith("Заблокировано", StringComparison.OrdinalIgnoreCase)
                ? "Заблокировано"
                : status;
        }

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
    private (bool Found, int Value) TryExtractCounter(string html, string tabMarker)
    {
        if (string.IsNullOrEmpty(html))
        {
            return (false, 0);
        }

        var escapedMarker = Regex.Escape(tabMarker);
        var pattern = $@"data-marker=""profile-items-tab/{escapedMarker}"".*?class=""[^""]*styles-module-counter[^""]*"".*?>(\d+)<";
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        if (!match.Success)
        {
            return (false, 0);
        }

        return (true, int.Parse(match.Groups[1].Value));
    }

    private int ExtractCounter(string html, string tabMarker) =>
        TryExtractCounter(html, tabMarker).Value;

    private static string ExtractSingle(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
