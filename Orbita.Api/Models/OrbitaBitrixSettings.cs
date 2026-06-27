namespace Orbita.Api.Models;

public sealed class OrbitaBitrixSettings
{
    public string EntityType { get; set; } = "Deal";
    public int ResponsibleId { get; set; }
    public string LeadSource { get; set; } = "Авито";
    public string DealIdempotencyUfCode { get; set; } = string.Empty;
    public string DealAgeUfCode { get; set; } = "UF_CRM_1777753181424";
    public string DealProfessionUfCode { get; set; } = "UF_CRM_1777753209215";
    public string DealCityUfCode { get; set; } = "UF_CRM_1777753293892";
}