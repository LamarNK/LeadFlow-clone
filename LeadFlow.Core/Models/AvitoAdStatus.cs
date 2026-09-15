namespace LeadFlow.Core.Models;

public class AvitoAdStatus
{
    public const string ActiveTab = "active";
    public const string ErrorTab = "rejected";
    public const string UnpublishedTab = "inactive";

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
    /// <summary>Вкладка Avito, из которой получена карточка.</summary>
    public string SourceTab { get; set; } = ActiveTab;
    /// <summary>Человекочитаемая причина, по которой объявление не опубликовано или отклонено.</summary>
    public string ErrorReason { get; set; } = "";
    /// <summary>Avito показывает действие «Опубликовать» для неопубликованной карточки.</summary>
    public bool CanPublish { get; set; }
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

    /// <summary>Точный срок размещения, который Avito показывает прямо в карточке списка.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public int? RemainingDays { get; set; }

    public string? ExpiryParseError { get; set; }
}
