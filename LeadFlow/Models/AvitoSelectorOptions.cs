namespace LeadFlow.Models;

public sealed class AvitoSelectorOptions
{
    public string ResponseListSelector { get; set; } = "[data-marker='chat-list']";
    public string ResponseItemSelector { get; set; } = "[data-marker='chat-item']";
    public string FullNameSelector { get; set; } = "[data-marker='buyer-name']";
    public string PhoneSelector { get; set; } = "[href^='tel:']";
    public string CitySelector { get; set; } = "[data-marker='item-view/location']";
    public string VacancySelector { get; set; } = "h1";
    public string AgeSelector { get; set; } = "[data-marker='age']";
    public string SourceLinkSelector { get; set; } = "a[href*='avito.ru']";
    public string ExtractionScript { get; set; } =
        "(() => ({ fullName: document.querySelector(\"[data-marker='buyer-name']\")?.textContent?.trim() ?? '', phone: document.querySelector(\"[href^='tel:']\")?.textContent?.trim() ?? '', city: document.querySelector(\"[data-marker='item-view/location']\")?.textContent?.trim() ?? '', vacancy: document.querySelector('h1')?.textContent?.trim() ?? '', age: document.querySelector(\"[data-marker='age']\")?.textContent?.trim() ?? '', sourceUrl: window.location.href, rawText: document.body.innerText }))();";
    public DateTime? LastSuccessfulUseAt { get; set; }
}
