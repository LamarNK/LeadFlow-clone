namespace LeadFlow.Core.Models;

public class AvitoAdStatus
{
    public Guid AccountId { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string City { get; set; } = "";
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

    public int DaysOnAvito { get; set; }
}
