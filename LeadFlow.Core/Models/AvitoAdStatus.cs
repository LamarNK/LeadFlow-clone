namespace LeadFlow.Core.Models;

public class AvitoAdStatus
{
    public Guid AccountId { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string City { get; set; } = "";
    public string AddressText { get; set; } = "";
    public string DistrictText { get; set; } = "";
    public string Salary { get; set; } = "";
    public int Views { get; set; }
    public int Contacts { get; set; }
    public int Favorites { get; set; }
    public string Status { get; set; } = "Активно";
    public string DeleteDate { get; set; } = "";

    // Url можно либо задать парсером (точный slug-ссылка из карточки), либо оставить пустым —
    // тогда геттер построит fallback-ссылку по Id (Avito принимает /item/{id} и редиректит на реальную страницу).
    private string? _url;

    public string Url
    {
        get => string.IsNullOrEmpty(_url)
            ? (string.IsNullOrEmpty(Id) ? string.Empty : $"https://www.avito.ru/item/{Id}")
            : _url;
        set => _url = value;
    }

    /// <summary>Href из <c>a[data-marker="view-link"]</c> без fallback на <c>/item/{id}</c>.</summary>
    public string ExplicitListingUrl => _url ?? string.Empty;

    public string? UrlParseError { get; set; }

    public int DaysOnAvito { get; set; }

    /// <summary>True, если «N день/дня/дней на Авито» удалось прочитать из карточки.</summary>
    public bool HasDaysOnAvito { get; set; }
}
