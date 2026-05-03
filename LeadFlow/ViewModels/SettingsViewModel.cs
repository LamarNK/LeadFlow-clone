using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
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
    private const string FixedAvitoProfileUrl = "https://www.avito.ru/profile";
    private AppSettings _settings = new();

    public ObservableCollection<AvitoAccount> Accounts { get; } = [];

    [ObservableProperty]
    private AvitoAccount? selectedAccount;

    [RelayCommand]
    public async Task LoadAsync()
    {
        _settings = await settingsService.LoadAsync(CancellationToken.None);
        Accounts.Clear();
        foreach (var account in _settings.Avito.Accounts)
        {
            account.AvitoResponsesUrl = FixedAvitoProfileUrl;
            Accounts.Add(account);
        }

        SelectedAccount = Accounts.FirstOrDefault();
    }

    [RelayCommand]
    public void AddAccount()
    {
        var accountNumber = Accounts.Count + 1;
        var account = new AvitoAccount
        {
            DisplayName = $"Аккаунт {accountNumber}",
            AvitoResponsesUrl = FixedAvitoProfileUrl,
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
        RefreshSelectedAccountState();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        _settings.Avito.Accounts.Clear();
        foreach (var account in Accounts)
        {
            account.AvitoResponsesUrl = FixedAvitoProfileUrl;

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

    public string FixedProfileUrl => FixedAvitoProfileUrl;

    public string SelectedAccountName => SelectedAccount?.DisplayName ?? "Аккаунт не выбран";

    public string SelectedAccountStatusText => SelectedAccount?.Status switch
    {
        AvitoAccountStatus.Authorized => "Готов к работе",
        AvitoAccountStatus.Monitoring => "Сейчас мониторится",
        AvitoAccountStatus.RequiresLogin => "Нужно войти",
        AvitoAccountStatus.RequiresManualAction => "Нужно ручное действие",
        AvitoAccountStatus.Paused => "Приостановлен",
        AvitoAccountStatus.Error => "Есть ошибка",
        _ => "Пока не настроен"
    };

    public string SelectedAccountStateHint => SelectedAccount is null
        ? "Добавьте первый аккаунт Avito, чтобы подготовить его к мониторингу."
        : SelectedAccount.IsEnabled
            ? "Аккаунт участвует в мониторинге и использует отдельный профиль браузера."
            : "Аккаунт сохранён, но сейчас исключён из мониторинга.";

    public string SelectedAccountToggleText => SelectedAccount?.IsEnabled == true ? "Выключить аккаунт" : "Включить аккаунт";

    public string SelectedAccountAuthCheckText => FormatDateTime(SelectedAccount?.LastAuthCheckAt, "Авторизация ещё не проверялась");

    public string SelectedAccountMonitoringText => FormatDateTime(SelectedAccount?.LastMonitoringAt, "Мониторинг ещё не запускался");

    public string SelectedAccountErrorText => string.IsNullOrWhiteSpace(SelectedAccount?.LastErrorMessage)
        ? "Ошибок не зафиксировано"
        : SelectedAccount!.LastErrorMessage;

    partial void OnSelectedAccountChanged(AvitoAccount? value)
    {
        CollectionViewSource.GetDefaultView(Accounts)?.Refresh();
        OnPropertyChanged(nameof(SelectedAccountName));
        OnPropertyChanged(nameof(SelectedAccountStatusText));
        OnPropertyChanged(nameof(SelectedAccountStateHint));
        OnPropertyChanged(nameof(SelectedAccountToggleText));
        OnPropertyChanged(nameof(SelectedAccountAuthCheckText));
        OnPropertyChanged(nameof(SelectedAccountMonitoringText));
        OnPropertyChanged(nameof(SelectedAccountErrorText));
    }

    private static string FormatDateTime(DateTime? value, string fallback) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : fallback;

    private void RefreshSelectedAccountState()
    {
        CollectionViewSource.GetDefaultView(Accounts)?.Refresh();
        OnSelectedAccountChanged(SelectedAccount);
    }
}
