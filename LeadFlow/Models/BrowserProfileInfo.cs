namespace LeadFlow.Models;

public sealed class BrowserProfileInfo
{
    public Guid AccountId { get; set; }
    public string ProfilePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    
    // === АНТИ-ДЕТЕКТ: Параметры фингерпринта для инициализации браузера ===
    public string? UserAgent { get; set; }
    public string? ProxyAddress { get; set; }
    public string ProxyType { get; set; } = "http";
}
