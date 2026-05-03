using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace LeadFlow.Services.Browser;

public sealed class BackgroundWebViewHost : IAsyncDisposable
{
    private readonly Window _window;

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
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = new BackgroundWebViewHost();
            host._window.Show();
            host._window.Hide();
            return host;
        });
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
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Browser.Dispose();
            _window.Close();
        });
    }
}
