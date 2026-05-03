namespace LeadFlow.Models;

public sealed class AppSettings
{
    public bool DemoModeEnabled { get; set; } = true;
    public string DatabasePath { get; set; } = string.Empty;
    public DuplicateScope DuplicateScope { get; set; } = DuplicateScope.GlobalAcrossAllAccounts;
    public MonitoringSafetyOptions MonitoringSafety { get; set; } = new();
    public BitrixSettings Bitrix { get; set; } = new();
    public AvitoSelectorOptions AvitoSelectors { get; set; } = new();
    public AvitoSettings Avito { get; set; } = new();
}
