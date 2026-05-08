using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text.Json;
using LeadFlow.Services.AntiDetect;

namespace LeadFlow.Services.Browser;

public partial class BrowserAccountSession : ObservableObject
{
    private bool _cookiesImportedInCurrentSession;
    private bool _clientHintsHeaderHookRegistered;
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
    public IProxyCheckService? ProxyCheckService { get; init; }
    public Func<CancellationToken, Task>? PersistAccountAsync { get; init; }
    public CoreWebView2Environment? Environment { get; private set; }
    public WebView2? AttachedView { get; private set; }

    public async Task AttachAsync(WebView2 view, CancellationToken cancellationToken)
    {
        AttachedView = view;
        Directory.CreateDirectory(ProfilePath);

        // === АНТИ-ДЕТЕКТ: Создаём окружение с прокси и настройками ===
        Environment = await CreateEnvironmentWithSettingsAsync();
        
        await view.EnsureCoreWebView2Async(Environment);

        if (view.CoreWebView2 is { } coreForProxyAuth)
        {
            coreForProxyAuth.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
            coreForProxyAuth.NewWindowRequested += OnNewWindowRequested;
        }

        await TryResolveTimezoneFromProxyAsync(cancellationToken).ConfigureAwait(true);

        view.CoreWebView2.SourceChanged += (_, _) => UpdateNavigationState();
        view.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationState();
        
        // UA, stealth (если задан) и блок геолокации — до навигации
        await ApplyFingerprintSettingsAsync(view, cancellationToken).ConfigureAwait(true);

        // Импорт cookies из JSON выполняем только по явному одноразовому флагу.
        if (view.CoreWebView2 is not null && Account.ImportCookiesOnNextStart && !_cookiesImportedInCurrentSession)
        {
            var report = await WebViewSessionCookies.ApplyFromStoredCookiesJsonAsync(view.CoreWebView2, Account.CookiesJson, cancellationToken)
                .ConfigureAwait(true);
            StatusText = report.Summary ?? "Импорт cookies завершён.";
            System.Diagnostics.Debug.WriteLine($"[Cookies] {StatusText}");
            _cookiesImportedInCurrentSession = true;

            if (PersistAccountAsync is not null)
            {
                try
                {
                    Account.ImportCookiesOnNextStart = false;
                    await PersistAccountAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // Семантика "одноразово" считается подтверждённой только после сохранения.
                    Account.ImportCookiesOnNextStart = true;
                    _cookiesImportedInCurrentSession = false;
                    System.Diagnostics.Debug.WriteLine($"[Cookies] Failed to persist ImportCookiesOnNextStart reset: {ex.Message}");
                }
            }
            else
            {
                // Без персистентности не подтверждаем сброс флага.
                _cookiesImportedInCurrentSession = false;
            }
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

    private async Task TryResolveTimezoneFromProxyAsync(CancellationToken cancellationToken)
    {
        if (ProxyCheckService is null || string.IsNullOrWhiteSpace(Account.ProxyAddress))
        {
            return;
        }

        try
        {
            var ip = await ProxyCheckService.CheckPublicIpAsync(Account, cancellationToken).ConfigureAwait(true);
            var timezone = await ProxyCheckService.ResolveTimezoneByIpAsync(ip, cancellationToken).ConfigureAwait(true);

            if (Account.UseIpTimezone)
            {
                Account.Timezone = timezone;
                if (PersistAccountAsync is not null)
                {
                    await PersistAccountAsync(cancellationToken).ConfigureAwait(true);
                }
            }

            StatusText = $"Прокси IP: {ip}. Часовой пояс: {timezone}.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProxyGeoIP] Auto timezone resolve failed: {ex.Message}");
        }
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

            TryRegisterClientHintsHeaderOverride(core);

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

        // Smoke-check консистентности ключевых fingerprint-сигналов в рантайме.
        try
        {
            var smokeScript = """
                (() => {
                    return {
                        webdriver: navigator.webdriver,
                        ua: navigator.userAgent,
                        platform: navigator.platform,
                        languages: navigator.languages,
                        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                        deviceMemory: navigator.deviceMemory,
                        hardwareConcurrency: navigator.hardwareConcurrency
                    };
                })();
                """;
            var raw = await core.ExecuteScriptAsync(smokeScript).WaitAsync(cancellationToken).ConfigureAwait(true);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("webdriver", out var webdriverEl) && webdriverEl.ValueKind != JsonValueKind.False)
            {
                System.Diagnostics.Debug.WriteLine("[AntiDetect] Smoke-check mismatch: navigator.webdriver is not false.");
            }

            if (root.TryGetProperty("ua", out var uaEl)
                && root.TryGetProperty("platform", out var platformEl)
                && uaEl.ValueKind == JsonValueKind.String
                && platformEl.ValueKind == JsonValueKind.String)
            {
                var ua = uaEl.GetString() ?? string.Empty;
                var platform = platformEl.GetString() ?? string.Empty;
                if ((ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) && !platform.Equals("Win32", StringComparison.OrdinalIgnoreCase))
                    || (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) && !platform.Equals("MacIntel", StringComparison.OrdinalIgnoreCase)))
                {
                    System.Diagnostics.Debug.WriteLine($"[AntiDetect] Smoke-check mismatch: UA/platform inconsistent ({ua} / {platform}).");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AntiDetect] Smoke-check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Подмена Sec-CH-UA-Platform / Platform-Version в исходящих запросах: иначе на Win11-хосте сайты видят 15.0.0 при выбранной в аккаунте Win10.
    /// </summary>
    private void TryRegisterClientHintsHeaderOverride(CoreWebView2 core)
    {
        if (_clientHintsHeaderHookRegistered)
        {
            return;
        }

        var ch = ClientHintsSpoof.GetForAccount(Account);
        if (string.IsNullOrEmpty(ch.Platform) && string.IsNullOrEmpty(ch.PlatformVersion) && string.IsNullOrEmpty(ch.Mobile))
        {
            return;
        }

        try
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AntiDetect] AddWebResourceRequestedFilter failed: {ex.Message}");
            return;
        }

        var browserCh = SecChUaHeaders.FromUserAgent(Account.AssignedUserAgent);

        _clientHintsHeaderHookRegistered = true;
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(ch.Platform))
                {
                    e.Request.Headers.SetHeader("Sec-CH-UA-Platform", $"\"{ch.Platform}\"");
                }

                if (!string.IsNullOrEmpty(ch.PlatformVersion))
                {
                    e.Request.Headers.SetHeader("Sec-CH-UA-Platform-Version", $"\"{ch.PlatformVersion}\"");
                }

                if (!string.IsNullOrEmpty(ch.Mobile))
                {
                    e.Request.Headers.SetHeader("Sec-CH-UA-Mobile", ch.Mobile);
                }

                if (browserCh is not null)
                {
                    e.Request.Headers.SetHeader("Sec-CH-UA", browserCh.SecChUa);
                    e.Request.Headers.SetHeader("Sec-CH-UA-Full-Version-List", browserCh.SecChUaFullVersionList);
                }
            }
            catch
            {
                // заголовок может отсутствовать или быть неизменяемым для части запросов
            }
        };
    }

    /// <summary>
    /// Ответ на Basic (в т.ч. 407 Proxy-Authenticate). Учётные данные в --proxy-server Chromium не передаём —
    /// иначе часто <c>ERR_NO_SUPPORTED_PROXIES</c>.
    /// </summary>
    private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Account.ProxyAddress)
            || string.IsNullOrWhiteSpace(Account.ProxyUsername)
            || string.Equals(Account.ProxyType, "socks5", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Response.UserName = Account.ProxyUsername.Trim();
        e.Response.Password = Account.ProxyPassword ?? string.Empty;
    }

    /// <summary>
    /// Ссылки с target=_blank и window.open — в одном окне WebView2, иначе переход «теряется».
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (AttachedView?.CoreWebView2 is not { } core || string.IsNullOrWhiteSpace(e.Uri))
        {
            return;
        }

        var raw = e.Uri.Trim();
        if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        core.Navigate(NormalizeUrl(raw));
    }

    /// <summary>Освобождает WebView2 (вызов с UI-потока), чтобы профиль не держал файлы открытыми.</summary>
    public void DisposeWebView()
    {
        if (AttachedView is not { } view)
        {
            return;
        }

        try
        {
            if (view.CoreWebView2 is { } core)
            {
                core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
                core.NewWindowRequested -= OnNewWindowRequested;
            }
        }
        catch
        {
        }

        try
        {
            view.Dispose();
        }
        catch
        {
        }

        AttachedView = null;
        Environment = null;
        IsInitialized = false;
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
        if (trimmedUrl.StartsWith("//", StringComparison.Ordinal))
        {
            trimmedUrl = $"https:{trimmedUrl}";
        }

        if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return $"https://{trimmedUrl}";
    }
}
