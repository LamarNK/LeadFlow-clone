using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow;
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
    private string _savedAccountsSnapshot = string.Empty;
    private readonly HashSet<string> _pendingProfileDeletions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _committedProfileDeletions = new(StringComparer.OrdinalIgnoreCase);
    private AvitoAccount? _selectedAccountPropertySource;

    public ObservableCollection<AvitoAccount> Accounts { get; } = [];

    [ObservableProperty]
    private AvitoAccount? selectedAccount;

    [RelayCommand]
    public async Task LoadAsync()
    {
        repository.AccountPersisted -= OnAccountPersisted;
        repository.AccountPersisted += OnAccountPersisted;

        _settings = await settingsService.LoadAsync(CancellationToken.None);
        var persistedAccounts = await repository.GetAccountsAsync(CancellationToken.None);
        var persistedById = persistedAccounts.ToDictionary(a => a.Id);

        _pendingProfileDeletions.Clear();
        Accounts.Clear();
        foreach (var account in _settings.Avito.Accounts)
        {
            account.AvitoResponsesUrl = FixedAvitoProfileUrl;
            if (persistedById.TryGetValue(account.Id, out var fromDb))
            {
                account.MergePersistedSnapshotFrom(fromDb);
            }

            Accounts.Add(account);
        }

        SelectedAccount = Accounts.FirstOrDefault();
        UpdateSavedSnapshot();
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

        if (!string.IsNullOrWhiteSpace(SelectedAccount.BrowserProfilePath))
        {
            _pendingProfileDeletions.Add(SelectedAccount.BrowserProfilePath);
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
        var persistedAccounts = await repository.GetAccountsAsync(CancellationToken.None);
        var currentAccountIds = Accounts.Select(account => account.Id).ToHashSet();

        foreach (var persistedAccount in persistedAccounts)
        {
            if (!currentAccountIds.Contains(persistedAccount.Id))
            {
                await repository.DeleteAccountAsync(persistedAccount.Id, CancellationToken.None);
            }
        }

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
        foreach (var profilePath in _pendingProfileDeletions)
        {
            _committedProfileDeletions.Add(profilePath);
        }

        _pendingProfileDeletions.Clear();
        UpdateSavedSnapshot();
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
    public async Task OpenAvitoProfileAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return;
        }

        await SaveAsync();
        await windowService.ShowAvitoProfileAsync(owner, SelectedAccount, CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    public Task OpenAccountSettingsAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return Task.CompletedTask;
        }

        return windowService.ShowAccountSettingsAsync(owner, SelectedAccount, CancellationToken.None);
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

    /// <summary>
    /// Кнопка входа в Avito нужна только пока аккаунт не в рабочем авторизованном состоянии.
    /// </summary>
    public bool ShowAuthorizeButton =>
        SelectedAccount is not null &&
        SelectedAccount.Status is not (
            AvitoAccountStatus.Authorized or
            AvitoAccountStatus.Monitoring or
            AvitoAccountStatus.Paused);

    public bool HasUnsavedChanges() => BuildAccountsSnapshot() != _savedAccountsSnapshot;

    public void DeleteCommittedProfiles()
    {
        foreach (var profilePath in _committedProfileDeletions.ToArray())
        {
            try
            {
                profileService.DeleteProfile(profilePath);
            }
            catch
            {
            }
        }

        _committedProfileDeletions.Clear();
    }

    public void DetachPersistenceListener()
    {
        repository.AccountPersisted -= OnAccountPersisted;
        DetachSelectedAccountPropertyListener();
    }

    private void OnAccountPersisted(object? sender, AvitoAccount snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            var local = Accounts.FirstOrDefault(a => a.Id == snapshot.Id);
            if (local is null)
            {
                return;
            }

            local.MergePersistedSnapshotFrom(snapshot);
            if (SelectedAccount?.Id == local.Id)
            {
                RefreshSelectedAccountState();
            }
        });
    }

    partial void OnSelectedAccountChanged(AvitoAccount? value)
    {
        DetachSelectedAccountPropertyListener();
        if (value is not null)
        {
            value.PropertyChanged += SelectedAccountOnPropertyChanged;
            _selectedAccountPropertySource = value;
        }

        RefreshSelectedAccountPresentation();
    }

    private void DetachSelectedAccountPropertyListener()
    {
        if (_selectedAccountPropertySource is not null)
        {
            _selectedAccountPropertySource.PropertyChanged -= SelectedAccountOnPropertyChanged;
            _selectedAccountPropertySource = null;
        }
    }

    private void SelectedAccountOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AvitoAccount acc || acc != SelectedAccount)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(RefreshSelectedAccountPresentation);
            return;
        }

        RefreshSelectedAccountPresentation();
    }

    private void RefreshSelectedAccountPresentation()
    {
        CollectionViewSource.GetDefaultView(Accounts)?.Refresh();
        OnPropertyChanged(nameof(SelectedAccountName));
        OnPropertyChanged(nameof(SelectedAccountStatusText));
        OnPropertyChanged(nameof(SelectedAccountStateHint));
        OnPropertyChanged(nameof(SelectedAccountToggleText));
        OnPropertyChanged(nameof(SelectedAccountAuthCheckText));
        OnPropertyChanged(nameof(SelectedAccountMonitoringText));
        OnPropertyChanged(nameof(SelectedAccountErrorText));
        OnPropertyChanged(nameof(ShowAuthorizeButton));
    }

    private static string FormatDateTime(DateTime? value, string fallback) =>
        value.HasValue ? value.Value.ToLocalTimeFromStoredUtc().ToString("dd.MM.yyyy HH:mm") : fallback;

    private void RefreshSelectedAccountState()
    {
        CollectionViewSource.GetDefaultView(Accounts)?.Refresh();
        RefreshSelectedAccountPresentation();
    }

    private void UpdateSavedSnapshot()
    {
        _savedAccountsSnapshot = BuildAccountsSnapshot();
    }

    private string BuildAccountsSnapshot()
    {
        var snapshot = Accounts
            .OrderBy(account => account.Id)
            .Select(account => new
            {
                account.Id,
                account.DisplayName,
                AvitoResponsesUrl = FixedAvitoProfileUrl,
                account.BrowserProfilePath,
                account.IsEnabled,
                account.BrowserName,
                account.BrowserVersion,
                account.UserAgentDevice,
                account.UseWindowsOs,
                account.WindowsVersion,
                account.UseMacOs,
                account.MacOsVersion,
                account.UseLinuxOs,
                account.LinuxVersion,
                account.UseAndroidOs,
                account.AndroidVersion,
                account.UseIosOs,
                account.IosVersion,
                account.AssignedUserAgent,
                account.CookiesJson,
                account.Notes,
                Status = account.Status.ToString(),
                account.LastAuthCheckAt,
                account.LastMonitoringAt,
                account.LastErrorMessage
            });

        return JsonSerializer.Serialize(snapshot);
    }
}
