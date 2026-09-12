namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Совместимость страницы объявлений с парсером Avito Pro вакансий.
/// Парсер срока рассчитан только на Pro-профили «Работа» с <c>item-snippet/{id}</c>.
/// </summary>
public static class AvitoProVacancyLayout
{
    public const string Supported = "Supported";
    public const string UnsupportedProfileLayout = "UnsupportedProfileLayout";
    public const string NotApplicable = "NotApplicable";

    public static bool HasProMarkup(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("data-marker=\"profile-items-tab\"", StringComparison.Ordinal)
            || html.Contains("data-marker=\"profile-items-tab/tab(active)\"", StringComparison.Ordinal)
            || html.Contains("data-marker=\"item-snippet/", StringComparison.Ordinal);
    }

    public static bool HasViewLink(string? html) =>
        !string.IsNullOrWhiteSpace(html)
        && html.Contains("data-marker=\"view-link\"", StringComparison.Ordinal);

    public static string Classify(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return NotApplicable;
        }

        if (HasProMarkup(html))
        {
            return Supported;
        }

        return UnsupportedProfileLayout;
    }

    public static bool IsSupported(string? kind) =>
        string.Equals(kind, Supported, StringComparison.Ordinal);
}
