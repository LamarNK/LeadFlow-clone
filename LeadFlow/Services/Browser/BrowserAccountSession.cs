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
    private bool canGoBack;

    [ObservableProperty]
    private bool canGoForward;

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

        view.CoreWebView2.SourceChanged += (_, _) => UpdateNavigationState();
        view.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationState();
        
        // UA, stealth (если задан) и блок геолокации — до навигации
        await ApplyFingerprintSettingsAsync(view, cancellationToken).ConfigureAwait(true);

        // Куки из сохранённого JSON аккаунта (редактируемое поле в настройках) — до первой навигации
        if (view.CoreWebView2 is not null)
        {
            await WebViewSessionCookies.ApplyFromStoredCookiesJsonAsync(view.CoreWebView2, Account.CookiesJson, cancellationToken)
                .ConfigureAwait(true);
        }

        var initialUrl = string.IsNullOrWhiteSpace(CurrentUrl)
            ? Account.AvitoResponsesUrl
            : CurrentUrl;
        view.Source = new Uri(initialUrl);
        CurrentUrl = initialUrl;
        UpdateNavigationState();
        IsInitialized = true;
        StatusText = "Браузер готов";
    }

    public void Navigate(string url)
    {
        if (AttachedView?.CoreWebView2 is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var normalizedUrl = NormalizeUrl(url);
        AttachedView.CoreWebView2.Navigate(normalizedUrl);
        CurrentUrl = normalizedUrl;
    }

    public void GoBack()
    {
        if (AttachedView?.CoreWebView2?.CanGoBack != true)
        {
            return;
        }

        AttachedView.CoreWebView2.GoBack();
        UpdateNavigationState();
    }

    public void GoForward()
    {
        if (AttachedView?.CoreWebView2?.CanGoForward != true)
        {
            return;
        }

        AttachedView.CoreWebView2.GoForward();
        UpdateNavigationState();
    }

    public void Reload()
    {
        AttachedView?.CoreWebView2?.Reload();
        UpdateNavigationState();
    }

    /// <summary>
    /// Создаёт CoreWebView2Environment с учётом прокси и других настроек аккаунта.
    /// </summary>
    private async Task<CoreWebView2Environment> CreateEnvironmentWithSettingsAsync()
    {
        var options = new CoreWebView2EnvironmentOptions();
        var args = ChromiumLaunchArgumentsBuilder.Build(Account);
        if (!string.IsNullOrWhiteSpace(args))
        {
            options.AdditionalBrowserArguments = args;
        }

        return await CoreWebView2Environment.CreateAsync(null, ProfilePath, options);
    }

    /// <summary>
    /// User-Agent, stealth по полям аккаунта (при непустом UA) и блок геолокации.
    /// </summary>
    private async Task ApplyFingerprintSettingsAsync(WebView2 view, CancellationToken cancellationToken)
    {
        if (view.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Account.AssignedUserAgent))
        {
            try
            {
                core.Settings.UserAgent = Account.AssignedUserAgent;
            }
            catch { /* UA может не поддерживаться в данной версии */ }

            var fingerprint = AccountFingerprintBuilder.FromAccount(Account);
            var stealthScript = StealthScripts.GetMainStealthScript(fingerprint);
            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(stealthScript)
                    .WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AntiDetect] Stealth injection failed: {ex.Message}");
            }
        }

        const string geoBlockScript = """
            if (navigator.permissions && navigator.permissions.query) {
                const originalQuery = navigator.permissions.query.bind(navigator.permissions);
                navigator.permissions.query = function(parameters) {
                    if (parameters.name === 'geolocation' || parameters.name === 'notifications') {
                        return Promise.resolve({ state: 'denied', name: parameters.name });
                    }
                    return originalQuery(parameters);
                };
            }
            if (navigator.geolocation) {
                navigator.geolocation.getCurrentPosition = function(success, error, options) {
                    if (error) error({ code: 1, message: 'Geolocation disabled by anti-detect' });
                };
                navigator.geolocation.watchPosition = function(success, error, options) {
                    if (error) error({ code: 1, message: 'Geolocation disabled by anti-detect' });
                    return -1;
                };
            }
            """;

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(geoBlockScript)
                .WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AntiDetect] Geoblock injection failed: {ex.Message}");
        }
    }

    private void UpdateNavigationState()
    {
        if (AttachedView?.CoreWebView2 is not CoreWebView2 core)
        {
            CanGoBack = false;
            CanGoForward = false;
            return;
        }

        CurrentUrl = core.Source ?? CurrentUrl;
        CanGoBack = core.CanGoBack;
        CanGoForward = core.CanGoForward;
    }

    private static string NormalizeUrl(string url)
    {
        var trimmedUrl = url.Trim();
        if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return $"https://{trimmedUrl}";
    }
}
