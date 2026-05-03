namespace LeadFlow.Services.Bitrix;

public sealed class BitrixCreateLeadRequest
{
    public string Title { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string SecondName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}
