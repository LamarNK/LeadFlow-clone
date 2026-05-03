using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;

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
        Environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfilePath);
        await view.EnsureCoreWebView2Async(Environment);
        view.Source = new Uri(Account.AvitoResponsesUrl);
        CurrentUrl = Account.AvitoResponsesUrl;
        IsInitialized = true;
        StatusText = "Браузер готов";
    }
}
