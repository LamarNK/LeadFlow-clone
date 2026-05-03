namespace LeadFlow.Models;

public sealed class BrowserProfileInfo
{
    public Guid AccountId { get; set; }
    public string ProfilePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
}
