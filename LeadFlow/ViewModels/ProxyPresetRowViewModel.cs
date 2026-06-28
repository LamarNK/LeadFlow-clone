using CommunityToolkit.Mvvm.ComponentModel;


namespace LeadFlow.ViewModels;

public partial class ProxyPresetRowViewModel : ObservableObject
{
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string proxyType = "http";
    [ObservableProperty] private string address = "";
    [ObservableProperty] private string? username;
    [ObservableProperty] private string? password;
    [ObservableProperty] private string? rotationUrl;

    public static ProxyPresetRowViewModel FromModel(SavedProxyPreset p) => new()
    {
        Label = p.Label ?? "",
        ProxyType = string.Equals(p.ProxyType, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http",
        Address = p.Address ?? "",
        Username = p.Username,
        Password = p.Password,
        RotationUrl = p.RotationUrl
    };

    public SavedProxyPreset ToModel() => new()
    {
        Label = Label.Trim(),
        ProxyType = ProxyType,
        Address = Address.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        Password = string.IsNullOrWhiteSpace(Password) ? null : Password,
        RotationUrl = string.IsNullOrWhiteSpace(RotationUrl) ? null : RotationUrl.Trim()
    };
}
