using Microsoft.Web.WebView2.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LeadFlow.Models;
using LeadFlow.Services.AntiDetect;

namespace LeadFlow.Services.Browser;

public sealed class WebView2PageAutomationService : IWebPageAutomationService
{
    private bool _stealthScriptsInjected = false;

    /// <summary>
    /// Навигация с предварительной инъекцией stealth-скриптов.
    /// </summary>
    public async Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken)
    {
        // Инжектируем stealth-скрипты один раз при первой навигации сессии
        await EnsureStealthScriptsInjectedAsync(session, cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.AttachedView?.CoreWebView2?.Navigate(url);
        });

        session.CurrentUrl = url;
    }

    public async Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken)
    {
        return await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.AttachedView?.CoreWebView2 is not CoreWebView2 core)
            {
                return string.Empty;
            }

            return await core.ExecuteScriptAsync(script);
        }).Task.Unwrap();
    }

    /// <summary>
    /// Устанавливает User-Agent для сессии (если не установлен ранее).
    /// </summary>
    public async Task SetUserAgentAsync(BrowserAccountSession session, string userAgent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userAgent) || session.AttachedView?.CoreWebView2 is not CoreWebView2 core)
            return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // WebView2 позволяет менять UA через Settings
                core.Settings.UserAgent = userAgent;
            }
            catch { /* Игнорируем ошибки установки UA */ }
        });
    }

    /// <summary>
    /// Инжектирует stealth-скрипты в документ при создании.
    /// Выполняется только один раз за сессию.
    /// </summary>
    private async Task EnsureStealthScriptsInjectedAsync(BrowserAccountSession session, CancellationToken cancellationToken)
    {
        if (_stealthScriptsInjected || session.AttachedView?.CoreWebView2 is not CoreWebView2 core)
            return;

        // Получаем фингерпринт из аккаунта сессии
        var fingerprint = session.Account != null 
            ? BuildFingerprintFromAccount(session.Account) 
            : null;

        var stealthScript = StealthScripts.GetMainStealthScript(fingerprint);

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Инжектируем скрипт — он выполнится при каждой загрузке любого документа
                await core.AddScriptToExecuteOnDocumentCreatedAsync(stealthScript);
                _stealthScriptsInjected = true;
            }
            catch (Exception ex)
            {
                // Логируем ошибку, но не прерываем работу — сайт может работать и без скриптов
                System.Diagnostics.Debug.WriteLine($"[AntiDetect] Script injection failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Строит объект фингерпринта из полей аккаунта.
    /// </summary>
    private static AccountFingerprint? BuildFingerprintFromAccount(AvitoAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AssignedUserAgent))
            return null;

        var parts = account.ScreenResolution?.Split('x') ?? new[] { "1920", "1080" };
        var width = int.TryParse(parts[0], out var w) ? w : 1920;
        var height = int.TryParse(parts[1], out var h) ? h : 1080;

        return new AccountFingerprint
        {
            UserAgent = account.AssignedUserAgent,
            ScreenResolution = account.ScreenResolution ?? "1920x1080",
            Timezone = account.Timezone ?? "Europe/Moscow",
            Languages = account.Languages ?? "ru-RU,ru,en-US,en",
            ViewportWidth = width - 20,
            ViewportHeight = height - 100,
            ColorDepth = 24,
            DeviceMemory = 8,
            HardwareConcurrency = 8
        };
    }

    /// <summary>
    /// Симулирует "человеческую" задержку перед действием.
    /// Используется для обхода поведенческого детекта.
    /// </summary>
    public static async Task HumanDelayAsync(int minMs = 800, int maxMs = 2500)
    {
        var delay = Random.Shared.Next(minMs, maxMs + 1);
        await Task.Delay(delay);
    }

    /// <summary>
    /// Симулирует плавный скролл к элементу (человеческое поведение).
    /// </summary>
    public async Task ScrollToElementAsync(BrowserAccountSession session, string selector, CancellationToken cancellationToken)
    {
        var escapedSelector = EscapeSelector(selector);
        var duration = Random.Shared.Next(500, 1500);
        
        var script = $@"
(function() {{
    const el = document.querySelector('{escapedSelector}');
    if (!el) return false;
    
    // Плавный скролл с рандомной скоростью
    const duration = {duration};
    const start = window.scrollY;
    const target = el.getBoundingClientRect().top + window.scrollY - 100;
    const distance = target - start;
    let startTime = null;
    
    function step(timestamp) {{
        if (!startTime) startTime = timestamp;
        const progress = Math.min((timestamp - startTime) / duration, 1);
        // Ease-in-out для естественности
        const ease = progress < 0.5 
            ? 2 * progress * progress 
            : 1 - Math.pow(-2 * progress + 2, 2) / 2;
        
        window.scrollTo(0, start + distance * ease);
        if (progress < 1) requestAnimationFrame(step);
    }}
    requestAnimationFrame(step);
    return true;
}})();";
        
        await ExecuteScriptAsync(session, script, cancellationToken);
        await HumanDelayAsync(300, 800); // Небольшая пауза после скролла
    }

    private static string EscapeSelector(string selector) => 
        selector.Replace("'", "\\'").Replace("\"", "\\\"");
}
