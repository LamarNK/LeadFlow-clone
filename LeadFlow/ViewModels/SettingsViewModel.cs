using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Browser;

namespace LeadFlow.ViewModels;

public partial class SettingsViewModel(
    ISettingsService settingsService,
    AppRepository repository,
    IBrowserProfileService profileService,
    IWindowService windowService) : ObservableObject
{
    private AppSettings _settings = new();

    public ObservableCollection<AvitoAccount> Accounts { get; } = [];

    [ObservableProperty]
    private AvitoAccount? selectedAccount;

    [ObservableProperty]
    private string webhookUrl = string.Empty;

    [ObservableProperty]
    private bool demoModeEnabled;

    [ObservableProperty]
    private int checkIntervalSeconds = 60;

    [ObservableProperty]
    private string databasePath = string.Empty;

    [ObservableProperty]
    private string responseListSelector = string.Empty;

    [RelayCommand]
    public async Task LoadAsync()
    {
        _settings = await settingsService.LoadAsync(CancellationToken.None);
        Accounts.Clear();
        foreach (var account in _settings.Avito.Accounts)
        {
            Accounts.Add(account);
        }

        WebhookUrl = _settings.Bitrix.WebhookUrl;
        DemoModeEnabled = _settings.DemoModeEnabled;
        CheckIntervalSeconds = _settings.MonitoringSafety.CheckIntervalSeconds;
        DatabasePath = _settings.DatabasePath;
        ResponseListSelector = _settings.AvitoSelectors.ResponseListSelector;
        SelectedAccount = Accounts.FirstOrDefault();
    }

    [RelayCommand]
    public void AddAccount()
    {
        var account = new AvitoAccount
        {
            DisplayName = "Новый аккаунт",
            AvitoResponsesUrl = "https://www.avito.ru/profile/responds",
            Status = AvitoAccountStatus.RequiresLogin
        };

        var profile = profileService.GetProfile(account);
        account.BrowserProfilePath = profile.ProfilePath;
        Accounts.Add(account);
        SelectedAccount = account;
    }

    [RelayCommand]
    public void DeleteAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        Accounts.Remove(SelectedAccount);
        SelectedAccount = Accounts.FirstOrDefault();
    }

    [RelayCommand]
    public void ToggleAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        SelectedAccount.IsEnabled = !SelectedAccount.IsEnabled;
        OnPropertyChanged(nameof(SelectedAccount));
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        _settings.Bitrix.WebhookUrl = WebhookUrl;
        _settings.DemoModeEnabled = DemoModeEnabled || string.IsNullOrWhiteSpace(WebhookUrl);
        _settings.MonitoringSafety.CheckIntervalSeconds = CheckIntervalSeconds;
        _settings.DatabasePath = DatabasePath;
        _settings.AvitoSelectors.ResponseListSelector = ResponseListSelector;
        _settings.Avito.Accounts.Clear();
        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.BrowserProfilePath))
            {
                account.BrowserProfilePath = profileService.GetProfile(account).ProfilePath;
            }

            _settings.Avito.Accounts.Add(account);
            await repository.SaveAccountAsync(account, CancellationToken.None);
        }

        await settingsService.SaveAsync(_settings, CancellationToken.None);
    }

    [RelayCommand]
    public async Task AuthorizeAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return;
        }

        await SaveAsync();
        await windowService.ShowAvitoAuthAsync(owner, SelectedAccount, CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    public async Task CheckAuthorizationAsync()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        SelectedAccount.LastAuthCheckAt = DateTime.UtcNow;
        SelectedAccount.Status = Directory.Exists(SelectedAccount.BrowserProfilePath)
            ? AvitoAccountStatus.Authorized
            : AvitoAccountStatus.RequiresLogin;
        await repository.SaveAccountAsync(SelectedAccount, CancellationToken.None);
        await SaveAsync();
    }
}
