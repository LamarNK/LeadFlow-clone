using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using LeadFlow.Services.AntiDetect;

namespace LeadFlow.Services.Browser;

public partial class BrowserAccountSession : ObservableObject
{
    [ObservableProperty]
    private string currentUrl = string.Empty;

    [ObservableProperty]
    private bool isInitialized;

    [ObservableProperty]
    private string statusText = "Ожидание инициализации браузера";

    public required AvitoAccount Account { get; init; }
    public required string ProfilePath { get; init; }
    public CoreWebView2Environment? Environment { get; private set; }
    public WebView2? AttachedView { get; private set; }

    public async Task AttachAsync(WebView2 view, CancellationToken cancellationToken)
    {
        AttachedView = view;
        Directory.CreateDirectory(ProfilePath);

        // === АНТИ-ДЕТЕКТ: Создаём окружение с прокси и настройками ===
        Environment = await CreateEnvironmentWithSettingsAsync();
        
        await view.EnsureCoreWebView2Async(Environment);
        
        // === Применяем User-Agent из фингерпринта ===
        ApplyFingerprintSettings(view);
        
        view.Source = new Uri(Account.AvitoResponsesUrl);
        CurrentUrl = Account.AvitoResponsesUrl;
        IsInitialized = true;
        StatusText = "Браузер готов";
    }

    /// <summary>
    /// Создаёт CoreWebView2Environment с учётом прокси и других настроек аккаунта.
    /// </summary>
    private async Task<CoreWebView2Environment> CreateEnvironmentWithSettingsAsync()
    {
        var options = new CoreWebView2EnvironmentOptions();
        
        // === Прокси настройка ===
        if (!string.IsNullOrWhiteSpace(Account.ProxyAddress))
        {
            var proxyArg = Account.ProxyType == "socks5" 
                ? $"--proxy-server=socks5://{Account.ProxyAddress}" 
                : $"--proxy-server={Account.ProxyAddress}";
            options.AdditionalBrowserArguments = proxyArg;
        }

        return await CoreWebView2Environment.CreateAsync(null, ProfilePath, options);
    }

    /// <summary>
    /// Применяет настройки фингерпринта к WebView2 контролу.
    /// </summary>
    private void ApplyFingerprintSettings(WebView2 view)
    {
        // Применяем User-Agent если он задан в аккаунте
        if (!string.IsNullOrWhiteSpace(Account.AssignedUserAgent) && view.CoreWebView2 != null)
        {
            try
            {
                view.CoreWebView2.Settings.UserAgent = Account.AssignedUserAgent;
            }
            catch { /* Игнорируем ошибки — UA может не поддерживаться в данной версии */ }
        }

        // === Дополнительные настройки для консистентности фингерпринта ===
        if (view.CoreWebView2 != null)
        {
            // Отключаем геолокацию и уведомления через инъекцию скриптов (совместимо со всеми версиями WebView2)
            // SetPreferenceAsync доступен только в WebView2 SDK 2.0+, поэтому используем универсальный подход
            _ = view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                // Блокируем запросы на геолокацию
                if (navigator.permissions && navigator.permissions.query) {
                    const originalQuery = navigator.permissions.query.bind(navigator.permissions);
                    navigator.permissions.query = function(parameters) {
                        if (parameters.name === 'geolocation' || parameters.name === 'notifications') {
                            return Promise.resolve({ state: 'denied', name: parameters.name });
                        }
                        return originalQuery(parameters);
                    };
                }
                // Переопределяем navigator.geolocation
                if (navigator.geolocation) {
                    navigator.geolocation.getCurrentPosition = function(success, error, options) {
                        if (error) error({ code: 1, message: 'Geolocation disabled by anti-detect' });
                    };
                    navigator.geolocation.watchPosition = function(success, error, options) {
                        if (error) error({ code: 1, message: 'Geolocation disabled by anti-detect' });
                        return -1;
                    };
                }
            ");
        }
    }
}
