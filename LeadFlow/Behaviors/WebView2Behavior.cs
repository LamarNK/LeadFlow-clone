using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using LeadFlow.Services.Browser;

namespace LeadFlow.Behaviors;

public static class WebView2Behavior
{
    public static readonly DependencyProperty SessionProperty =
        DependencyProperty.RegisterAttached(
            "Session",
            typeof(BrowserAccountSession),
            typeof(WebView2Behavior),
            new PropertyMetadata(null, OnSessionChanged));

    public static void SetSession(DependencyObject element, BrowserAccountSession? value) => element.SetValue(SessionProperty, value);
    public static BrowserAccountSession? GetSession(DependencyObject element) => (BrowserAccountSession?)element.GetValue(SessionProperty);

    private static async void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 webView || e.NewValue is not BrowserAccountSession session)
        {
            return;
        }

        await session.AttachAsync(webView, CancellationToken.None);
    }
}
