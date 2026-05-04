namespace LeadFlow.Models;

public class AvitoAdStatus
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string City { get; set; } = "";
    public string Salary { get; set; } = "";
    public int Views { get; set; }
    public int Contacts { get; set; }
    public int Favorites { get; set; }
    public string Status { get; set; } = "Активно";
    public string DeleteDate { get; set; } = "";
    public string Url => $"https://www.avito.ru/item/{Id}";
    public int DaysOnAvito { get; set; }
}
