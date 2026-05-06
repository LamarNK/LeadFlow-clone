namespace LeadFlow.Models;

/// <summary>Запись в списке сохранённых прокси (сериализуется в <see cref="AvitoAccount.ProxyPresetsJson"/>).</summary>
public sealed class SavedProxyPreset
{
    public string Label { get; set; } = string.Empty;
    public string ProxyType { get; set; } = "http";
    public string Address { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? RotationUrl { get; set; }
}
