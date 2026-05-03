using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Browser;

namespace LeadFlow.ViewModels;

public partial class AvitoAuthViewModel(
    IBrowserSessionService browserSessionService,
    IAvitoPageReaderService pageReaderService,
    ISettingsService settingsService) : ObservableObject
{
    private AvitoAccount? _account;

    [ObservableProperty]
    private string accountName = string.Empty;

    [ObservableProperty]
    private string currentUrl = string.Empty;

    [ObservableProperty]
    private string authorizationStatus = "Ожидание";

    [ObservableProperty]
    private BrowserAccountSession? session;

    public void Configure(AvitoAccount account)
    {
        _account = account;
        AccountName = account.DisplayName;
        CurrentUrl = account.AvitoResponsesUrl;
        InitializeCommand.Execute(null);
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_account is null)
        {
            return;
        }

        Session = await browserSessionService.CreateSessionAsync(_account, CancellationToken.None);
        CurrentUrl = Session.CurrentUrl;
        AuthorizationStatus = "Войдите в аккаунт Авито вручную. После успешного входа профиль будет сохранён.";
    }

    [RelayCommand]
    public async Task CheckAuthorizationAsync()
    {
        if (Session is null)
        {
            return;
        }

        var settings = await settingsService.LoadAsync(CancellationToken.None);
        var result = await pageReaderService.CheckAuthorizationAsync(Session, settings.AvitoSelectors, CancellationToken.None);
        CurrentUrl = result.CurrentUrl;
        AuthorizationStatus = result.StatusMessage;
    }
}
