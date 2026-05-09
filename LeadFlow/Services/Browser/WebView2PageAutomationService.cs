using Microsoft.Web.WebView2.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LeadFlow.Services.Browser;

public sealed class WebView2PageAutomationService : IWebPageAutomationService
{
    /// <summary>
    /// Навигация (stealth и геолокация уже регистрируются в <see cref="BrowserAccountSession.AttachAsync"/>).
    /// </summary>
    public async Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken)
    {
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
    /// Симулирует "человеческую" задержку перед действием.
    /// Используется для обхода поведенческого детекта.
    /// </summary>
    public static async Task HumanDelayAsync(int minMs = 1200, int maxMs = 4000)
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
        var duration = Random.Shared.Next(700, 2200);
        
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
        await HumanDelayAsync(500, 1200); // Небольшая пауза после скролла
    }

    private static string EscapeSelector(string selector) => 
        selector.Replace("'", "\\'").Replace("\"", "\\\"");
}
