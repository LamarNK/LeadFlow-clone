using Orbita.Contracts;

namespace LeadFlow.Core.Models;

public sealed class AppSettings
{
    public bool DemoModeEnabled { get; set; } = true;
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>SQLCipher passphrase (stored inside encrypted settings file). Base64-encoded random bytes.</summary>
    public string DatabaseEncryptionKey { get; set; } = string.Empty;
    public DuplicateScope DuplicateScope { get; set; } = DuplicateScope.GlobalAcrossAllAccounts;
    public MonitoringSafetyOptions MonitoringSafety { get; set; } = new();
    public BitrixSettings Bitrix { get; set; } = new();
    public AvitoSelectorOptions AvitoSelectors { get; set; } = new();
    public AvitoSettings Avito { get; set; } = new();

    /// <summary>Фильтры сбора откликов (пол/возраст). На desktop по умолчанию выключены; воркер берёт из панели.</summary>
    public ResponseCollectionFilters ResponseFilters { get; set; } = ResponseCollectionFilters.Disabled;
}
