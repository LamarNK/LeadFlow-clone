namespace Orbita.Api.Models;

public sealed class BitrixLeadPreview
{
    public string Title { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string SecondName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string Source { get; set; } = "Авито";
    public string Comments { get; set; } = string.Empty;
}