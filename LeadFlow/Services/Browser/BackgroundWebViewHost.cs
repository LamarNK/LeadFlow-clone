using System.Windows;

using Microsoft.Web.WebView2.Wpf;

namespace LeadFlow.Services.Browser;

public sealed class BackgroundWebViewHost : IBackgroundWebViewHost
{
    private const int MaxConcurrentHosts = 10;
    private static readonly SemaphoreSlim HostSemaphore = new(MaxConcurrentHosts, MaxConcurrentHosts);
    private readonly Window _window;
    private bool _ownsSemaphore;

    private BackgroundWebViewHost()
    {
        Browser = new WebView2();
        _window = new Window
        {
            Width = 1200,
            Height = 900,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = Browser
        };
    }

    public WebView2 Browser { get; }

    public static async Task<BackgroundWebViewHost> CreateAsync(CancellationToken cancellationToken)
    {
        await HostSemaphore.WaitAsync(cancellationToken);
        try
        {
            await GlobalLogger.Instance.LogAsync(
                "Creating background WebView2 host.",
                DeskLinkAuditLogLevel.Debug);

            var host = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var createdHost = new BackgroundWebViewHost
                {
                    _ownsSemaphore = true
                };
                createdHost._window.Show();
                createdHost._window.Hide();
                return createdHost;
            });

            await GlobalLogger.Instance.LogAsync(
                "Background WebView2 host created.",
                DeskLinkAuditLogLevel.Debug);

            return host;
        }
        catch
        {
            HostSemaphore.Release();
            throw;
        }
    }

    public async Task AttachAsync(BrowserAccountSession session, CancellationToken cancellationToken)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.AttachAsync(Browser, cancellationToken);
        }).Task.Unwrap();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Browser.Dispose();
                _window.Close();
            });

            await GlobalLogger.Instance.LogAsync(
                "Background WebView2 host disposed.",
                DeskLinkAuditLogLevel.Debug);
        }
        finally
        {
            if (_ownsSemaphore)
            {
                _ownsSemaphore = false;
                HostSemaphore.Release();
            }
        }
    }
}
