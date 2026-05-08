using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Browser;
using LeadFlow.Views;

namespace LeadFlow.ViewModels;

public partial class SettingsViewModel(
    ISettingsService settingsService,
    AppRepository repository,
    IBrowserProfileService profileService,
    IBrowserProfileArchiveService profileArchiveService,
    IWindowService windowService) : ObservableObject
{
    private const string FixedAvitoProfileUrl = "https://www.avito.ru/profile";
    private AppSettings _settings = new();
    private readonly HashSet<string> _pendingProfileDeletions = new(StringComparer.OrdinalIgnoreCase);
    private AvitoAccount? _selectedAccountPropertySource;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ObservableCollection<AvitoAccount> Accounts { get; } = [];

    [ObservableProperty]
    private AvitoAccount? selectedAccount;

    [ObservableProperty]
    private bool isExportingProfile;

    [ObservableProperty]
    private bool isImportingProfile;

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
    }

    [RelayCommand]
    public async Task AddAccount()
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
        await SaveAsync();
    }

    [RelayCommand]
    public async Task DeleteAccount()
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
        await SaveAsync();
    }

    [RelayCommand]
    public async Task ToggleAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        SelectedAccount.IsEnabled = !SelectedAccount.IsEnabled;
        RefreshSelectedAccountState();
        await SaveAsync();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        string[]? profileDirsToDelete = null;
        try
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
            if (_pendingProfileDeletions.Count > 0)
            {
                profileDirsToDelete = _pendingProfileDeletions.ToArray();
                _pendingProfileDeletions.Clear();
            }
        }
        finally
        {
            _saveGate.Release();
        }

        if (profileDirsToDelete is { Length: > 0 })
        {
            ScheduleProfileDirectoryDeletion(profileDirsToDelete);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedAccountBrowserOrSettings))]
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

    [RelayCommand(CanExecute = nameof(CanOpenSelectedAccountBrowserOrSettings))]
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

    [RelayCommand(CanExecute = nameof(CanOpenSelectedAccountBrowserOrSettings))]
    public Task OpenAccountSettingsAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return Task.CompletedTask;
        }

        return windowService.ShowAccountSettingsAsync(owner, SelectedAccount, CancellationToken.None);
    }

    /// <summary>Во время экспорта или импорта профиля нельзя открывать Avito и карточку настроек этого аккаунта.</summary>
    private bool CanOpenSelectedAccountBrowserOrSettings() =>
        SelectedAccount is not null && !IsExportingProfile && !IsImportingProfile;

    [RelayCommand(CanExecute = nameof(CanExportProfileArchive))]
    private async Task ExportProfileArchiveAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return;
        }

        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Архив профиля (*.zip)|*.zip",
                FileName = $"LeadFlow-Avito-{SanitizeFileName(SelectedAccount.DisplayName)}-profile.zip",
                DefaultExt = ".zip",
            };

            if (dlg.ShowDialog() != true)
            {
                return;
            }

            var confirm = MessageBox.Show(
                owner,
                "Перед экспортом будут закрыты вкладки окна Avito для этого аккаунта (если открыты), "
                + "чтобы браузер не удерживал файлы папки профиля.\n\nПродолжить экспорт?",
                "Экспорт профиля",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            windowService.CloseAvitoBrowserTabsForAccount(SelectedAccount.Id);
            await Task.Delay(250).ConfigureAwait(true);

            var progressVm = new ExportProgressViewModel(SelectedAccount.DisplayName);
            var progressWindow = new ExportProgressWindow
            {
                Owner = owner,
                DataContext = progressVm,
            };

            try
            {
                IsExportingProfile = true;
                progressWindow.Show();
                await progressWindow.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
                var progress = new Progress<int>(pct => progressVm.Percent = pct);
                await profileArchiveService.ExportAsync(SelectedAccount, dlg.FileName, progress, CancellationToken.None);

                MessageBox.Show(
                    owner,
                    "Папка профиля WebView2 и настройки аккаунта записаны в ZIP.",
                    "Экспорт профиля",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            finally
            {
                progressWindow.Close();
                IsExportingProfile = false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Экспорт профиля", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportProfileArchive))]
    private async Task ImportProfileArchiveAsync(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Filter = "Архив профиля (*.zip)|*.zip",
            Multiselect = false,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        await ImportProfileArchiveCoreAsync(owner, dlg.FileName);
    }

    /// <summary>Drag-and-drop: первый .zip из списка путей.</summary>
    public async Task ImportProfileArchiveFromDroppedPathsAsync(IReadOnlyList<string> paths, Window? dialogOwner = null)
    {
        var owner = dialogOwner ?? Application.Current?.MainWindow;
        var zip = paths.FirstOrDefault(static p =>
            p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        if (zip is null)
        {
            MessageBox.Show(
                owner,
                "Перетащите один файл .zip — архив профиля LeadFlow.",
                "Импорт профиля",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await ImportProfileArchiveCoreAsync(owner, zip);
    }

    private async Task ImportProfileArchiveCoreAsync(Window? owner, string zipPath)
    {
        AvitoProfileArchiveManifest manifest;
        try
        {
            manifest = await profileArchiveService.ReadManifestAsync(zipPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Импорт профиля", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var suggestedName = SuggestImportedAccountDisplayName(manifest, zipPath);
        var r = MessageBox.Show(
            owner,
            $"Будет создан новый аккаунт «{suggestedName}» с профилем и настройками из архива (как при экспорте).\n\nПродолжить?",
            "Импорт профиля",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (r != MessageBoxResult.Yes)
        {
            return;
        }

        var newAccount = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = suggestedName,
            AvitoResponsesUrl = FixedAvitoProfileUrl,
            Status = AvitoAccountStatus.RequiresLogin,
        };

        var profile = profileService.GetProfile(newAccount);
        newAccount.BrowserProfilePath = profile.ProfilePath;

        var progressVm = new ExportProgressViewModel(newAccount.DisplayName, "Распаковывается архив профиля…");
        var progressWindow = new ExportProgressWindow
        {
            Owner = owner,
            Title = "Импорт профиля",
            DataContext = progressVm,
        };

        try
        {
            IsImportingProfile = true;
            progressWindow.Show();
            await progressWindow.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var progress = new Progress<int>(pct => progressVm.Percent = pct);
            await profileArchiveService.ImportAsync(
                newAccount,
                zipPath,
                applyManifestToAccount: true,
                progress,
                CancellationToken.None);

            var newId = newAccount.Id;
            Accounts.Add(newAccount);
            await SaveAsync();
            await LoadAsync();
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == newId);

            MessageBox.Show(
                owner,
                "Новый аккаунт добавлен в список, настройки сохранены.",
                "Импорт профиля",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Импорт профиля", MessageBoxButton.OK, MessageBoxImage.Error);
            try
            {
                if (!Accounts.Contains(newAccount)
                    && !string.IsNullOrWhiteSpace(newAccount.BrowserProfilePath))
                {
                    ScheduleProfileDirectoryDeletion([newAccount.BrowserProfilePath]);
                }
            }
            catch
            {
            }
        }
        finally
        {
            progressWindow.Close();
            IsImportingProfile = false;
        }
    }

    private bool CanExportProfileArchive() =>
        SelectedAccount is not null && !IsExportingProfile && !IsImportingProfile;

    private bool CanImportProfileArchive() => !IsExportingProfile && !IsImportingProfile;

    private static string SuggestImportedAccountDisplayName(AvitoProfileArchiveManifest manifest, string zipPath)
    {
        var fromManifest = manifest.SourceDisplayName?.Trim();
        if (!string.IsNullOrEmpty(fromManifest))
        {
            return fromManifest;
        }

        var fn = Path.GetFileNameWithoutExtension(zipPath);
        if (!string.IsNullOrEmpty(fn))
        {
            return SanitizeFileName(fn);
        }

        return "Импорт профиля";
    }

    partial void OnIsExportingProfileChanged(bool value)
    {
        NotifyProfileArchiveAndAccountBrowserCommands();
    }

    partial void OnIsImportingProfileChanged(bool value)
    {
        NotifyProfileArchiveAndAccountBrowserCommands();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "account";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
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
        NotifyProfileArchiveAndAccountBrowserCommands();
    }

    private void NotifyProfileArchiveAndAccountBrowserCommands()
    {
        ExportProfileArchiveCommand.NotifyCanExecuteChanged();
        ImportProfileArchiveCommand.NotifyCanExecuteChanged();
        AuthorizeCommand.NotifyCanExecuteChanged();
        OpenAvitoProfileCommand.NotifyCanExecuteChanged();
        OpenAccountSettingsCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// Удаление каталога профиля WebView2 может занять много времени; выполняется в фоне, чтобы не блокировать UI.
    /// </summary>
    private void ScheduleProfileDirectoryDeletion(string[] profilePaths)
    {
        _ = Task.Run(() =>
        {
            foreach (var profilePath in profilePaths)
            {
                try
                {
                    profileService.DeleteProfile(profilePath);
                }
                catch
                {
                }
            }
        });
    }

}
