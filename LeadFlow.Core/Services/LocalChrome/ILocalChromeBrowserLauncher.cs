using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Запуск установленного Chrome/Chromium с отдельным <c>User Data</c> профилем.
/// Не вызывает AdsPower и Multilogin.
/// </summary>
public interface ILocalChromeBrowserLauncher
{
    Task<IBrowser> LaunchAsync(
        LocalChromeLaunchOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class LocalChromeLaunchOptions
{
    public string UserDataDir { get; init; } = string.Empty;

    public string? ExecutablePath { get; init; }

    public Guid AccountId { get; init; }

    /// <summary>HTTP-прокси <c>host:port</c>. Без схемы и без userinfo.</summary>
    public bool ProxyEnabled { get; init; }

    public string? ProxyServer { get; init; }

    public string? ProxyUsername { get; init; }

    public string? ProxyPassword { get; init; }

    public string[]? ChromiumArgs =>
        Orbita.Contracts.LocalChromeProxyRules.ToChromiumArgs(ProxyEnabled, ProxyServer);

    public bool HasProxyCredentials =>
        ProxyEnabled
        && (!string.IsNullOrEmpty(ProxyUsername) || !string.IsNullOrEmpty(ProxyPassword));
}
