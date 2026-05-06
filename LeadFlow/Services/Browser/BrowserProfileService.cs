using System.IO;
using System.Threading.Tasks;
using LeadFlow.Models;
using LeadFlow.Services.AntiDetect;
using Microsoft.Web.WebView2.Core;

namespace LeadFlow.Services.Browser;

public sealed class BrowserProfileService : IBrowserProfileService
{
    private readonly ISettingsService? _settingsService;

    public BrowserProfileService(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public BrowserProfileInfo GetProfile(AvitoAccount account)
    {
        var path = Path.Combine(
            JsonSettingsService.GetDataDirectoryPath(),
            "Profiles",
            "Avito",
            account.Id.ToString());

        Directory.CreateDirectory(path);

        // === АНТИ-ДЕТЕКТ: Генерация фингерпринта при первом создании ===
        EnsureFingerprintGenerated(account);

        return new BrowserProfileInfo
        {
            AccountId = account.Id,
            ProfilePath = path,
            Exists = Directory.Exists(path),
            UserAgent = account.AssignedUserAgent,
            ProxyAddress = account.ProxyAddress,
            ProxyType = account.ProxyType
        };
    }

    /// <summary>
    /// Гарантирует, что у аккаунта сгенерирован фингерпринт.
    /// Вызывается при каждом обращении к профилю.
    /// </summary>
    private void EnsureFingerprintGenerated(AvitoAccount account)
    {
        // Если UA уже задан — фингерпринт уже сгенерирован ранее
        if (!string.IsNullOrWhiteSpace(account.AssignedUserAgent))
            return;

        // Получаем версию WebView2 для генерации совместимого UA
        var webView2Version = GetWebView2Version();
        
        // Генерируем и применяем фингерпринт
        var fingerprint = FingerprintGenerator.GenerateFingerprint(webView2Version);
        fingerprint.ApplyToAccount(account);
        
        // Сохраняем изменения, если есть доступ к репозиторию/сервису настроек
        // (в реальном приложении здесь должен быть вызов репозитория)
    }

    private static string GetWebView2Version()
    {
        try
        {
            // Получаем версию установленного WebView2 без блокирующего ожидания async-API.
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version) ? "125.0.0.0" : version;
        }
        catch
        {
            // Фолбэк на известную стабильную версию
            return "125.0.0.0";
        }
    }

    /// <summary>
    /// Создаёт CoreWebView2Environment с учётом прокси и других настроек аккаунта.
    /// </summary>
    public async Task<CoreWebView2Environment> CreateEnvironmentAsync(AvitoAccount account)
    {
        var options = new CoreWebView2EnvironmentOptions();
        var args = ChromiumLaunchArgumentsBuilder.Build(account);
        if (!string.IsNullOrWhiteSpace(args))
        {
            options.AdditionalBrowserArguments = args;
        }

        var profilePath = GetProfile(account).ProfilePath;
        return await CoreWebView2Environment.CreateAsync(null, profilePath, options);
    }

    public void DeleteProfile(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
        {
            return;
        }

        Directory.Delete(profilePath, recursive: true);
    }
}
