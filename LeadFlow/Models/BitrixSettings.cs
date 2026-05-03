namespace LeadFlow.Models;

public sealed class BitrixSettings
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string EntityType { get; set; } = "Lead";
    public int ResponsibleId { get; set; }
    public string LeadSource { get; set; } = "Авито";
    public bool CheckDuplicatesInBitrix { get; set; } = true;
}
