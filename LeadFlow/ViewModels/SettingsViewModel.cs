using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;



using LeadFlow.Services;

using LeadFlow.Services.Browser;
using LeadFlow.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.ViewModels;

public partial class SettingsViewModel(
    ISettingsService settingsService,
    AppRepository repository,
    IBrowserProfileService profileService,
    IBrowserProfileArchiveService profileArchiveService,
    IWindowService windowService,
    IServiceProvider serviceProvider) : ObservableObject
{
    private const string FixedAvitoProfileUrl = "https://www.avito.ru/profile";
    private AppSettings _settings = new();
    private readonly HashSet<string> _pendingProfileDeletions = new(StringComparer.OrdinalIgnoreCase);
    private AvitoAccount? _selectedAccountPropertySource;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly HashSet<Guid> _dirtyAccountIds = [];
    private readonly HashSet<Guid> _persistedAccountIds = [];
    private bool _suspendDirtyTracking;
    private bool _isHydratingAccounts;

    /// <summary>Были ли реальные записи на диск за время открытого окна настроек.</summary>
    public bool SessionPersistedChanges { get; private set; }

    public ObservableCollection<AvitoAccount> Accounts { get; } = [];

    public int AccountsCount => Accounts.Count;

    public bool HasSelectedAccount => SelectedAccount is not null;

    [ObservableProperty]
    private AvitoAccount? selectedAccount;

    [ObservableProperty]
    private bool isExportingProfile;

    [ObservableProperty]
    private bool isImportingProfile;

    [ObservableProperty]
    private bool isAddMenuOpen;

    /// <summary>URL входящего вебхука Bitrix24; синхронизируется с <see cref="AppSettings.Bitrix"/> при загрузке и сохранении.</summary>
    [ObservableProperty]
    private string bitrixWebhookUrl = string.Empty;

    /// <summary>Сколько аккаунтов Авито обрабатывать параллельно в мониторинге (1…10).</summary>
    [ObservableProperty]
    private int maxConcurrentAccounts = 1;

    [RelayCommand]
    public async Task LoadAsync()
    {
        repository.AccountPersisted -= OnAccountPersisted;
        repository.AccountPersisted += OnAccountPersisted;

        var selectedAccountId = SelectedAccount?.Id;
        _settings = await settingsService.LoadAsync(CancellationToken.None);
        BitrixWebhookUrl = _settings.Bitrix.WebhookUrl ?? string.Empty;
        MaxConcurrentAccounts = _settings.MonitoringSafety.MaxConcurrentAccounts;
        var persistedAccounts = await repository.GetAccountsForSettingsAsync(CancellationToken.None);

        _pendingProfileDeletions.Clear();
        foreach (var account in Accounts.ToArray())
        {
            DetachAccountDirtyTracking(account);
        }

        SessionPersistedChanges = false;
        _isHydratingAccounts = true;
        _suspendDirtyTracking = true;
        try
        {
            Accounts.Clear();
            foreach (var account in persistedAccounts)
            {
                account.AvitoResponsesUrl = FixedAvitoProfileUrl;
                AttachAccountDirtyTracking(account);
                Accounts.Add(account);
            }

            OnPropertyChanged(nameof(AccountsCount));

            _dirtyAccountIds.Clear();
            _persistedAccountIds.Clear();
            _persistedAccountIds.UnionWith(persistedAccounts.Select(static account => account.Id));
        }
        finally
        {
            _suspendDirtyTracking = false;
        }

        SelectedAccount = selectedAccountId.HasValue
            ? Accounts.FirstOrDefault(account => account.Id == selectedAccountId.Value) ?? Accounts.FirstOrDefault()
            : Accounts.FirstOrDefault();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            _ = dispatcher.InvokeAsync(
                () => _isHydratingAccounts = false,
                DispatcherPriority.Loaded);
        }
        else
        {
            _isHydratingAccounts = false;
        }
    }

    /// <summary>Есть ли несохранённые изменения (для закрытия окна без лишнего I/O).</summary>
    public bool HasPendingChanges()
    {
        if (_dirtyAccountIds.Count > 0 || _pendingProfileDeletions.Count > 0)
        {
            return true;
        }

        if (_settings.Avito.Accounts.Count > 0)
        {
            return true;
        }

        var normalizedWebhookUrl = BitrixWebhookUrl?.Trim() ?? string.Empty;
        if (!string.Equals(_settings.Bitrix.WebhookUrl, normalizedWebhookUrl, StringComparison.Ordinal))
        {
            return true;
        }

        if (_settings.MonitoringSafety.MaxConcurrentAccounts != Math.Clamp(MaxConcurrentAccounts, 1, 10))
        {
            return true;
        }

        var currentIds = Accounts.Select(static account => account.Id).ToHashSet();
        return currentIds.Count != _persistedAccountIds.Count
               || currentIds.Any(id => !_persistedAccountIds.Contains(id));
    }

    [RelayCommand]
    public async Task AddLocalAccountAsync()
    {
        IsAddMenuOpen = false;
        var accountNumber = Accounts.Count + 1;
        var account = new AvitoAccount
        {
            DisplayName = $"Аккаунт {accountNumber}",
            AvitoResponsesUrl = FixedAvitoProfileUrl,
            Status = AvitoAccountStatus.RequiresLogin,
            ProfileProvider = AvitoProfileProvider.Local
        };

        var profile = profileService.GetProfile(account);
        account.BrowserProfilePath = profile.ProfilePath;
        AttachAccountDirtyTracking(account);
        Accounts.Add(account);
        OnPropertyChanged(nameof(AccountsCount));
        _dirtyAccountIds.Add(account.Id);
        SelectedAccount = account;
        await SaveAsync();
    }

    [RelayCommand]
    public async Task AddFromAdsPowerAsync(Window? owner)
    {
        IsAddMenuOpen = false;
        owner ??= Application.Current?.MainWindow;
        var picker = ActivatorUtilities.CreateInstance<AdsPowerProfilePickerWindow>(serviceProvider);
        if (owner is not null)
        {
            picker.Owner = owner;
        }

        if (picker.ShowDialog() != true || picker.Result is not { } r)
        {
            return;
        }

        if (Accounts.Any(a =>
                a.ProfileProvider == AvitoProfileProvider.AdsPower
                && string.Equals(a.AdsPowerProfileId, r.UserId, StringComparison.Ordinal)))
        {
            MessageBox.Show(
                owner,
                "Этот профиль AdsPower уже есть в списке.",
                "Добавление аккаунта",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var n = Accounts.Count + 1;
        var account = new AvitoAccount
        {
            DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? $"AdsPower {n}" : r.DisplayName,
            AvitoResponsesUrl = FixedAvitoProfileUrl,
            Status = AvitoAccountStatus.RequiresLogin,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = r.UserId,
            AdsPowerProfileName = r.DisplayName,
            AdsPowerApiBaseUrl = r.ApiBaseUrl,
            AdsPowerApiKey = r.ApiKey,
            BrowserProfilePath = string.Empty
        };

        AttachAccountDirtyTracking(account);
        Accounts.Add(account);
        OnPropertyChanged(nameof(AccountsCount));
        _dirtyAccountIds.Add(account.Id);
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

        if (SelectedAccount.ProfileProvider == AvitoProfileProvider.Local
            && !string.IsNullOrWhiteSpace(SelectedAccount.BrowserProfilePath))
        {
            _pendingProfileDeletions.Add(SelectedAccount.BrowserProfilePath);
        }

        DetachAccountDirtyTracking(SelectedAccount);
        Accounts.Remove(SelectedAccount);
        OnPropertyChanged(nameof(AccountsCount));
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
        await SaveAsync();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!HasPendingChanges())
        {
            return;
        }

        await _saveGate.WaitAsync();
        string[]? profileDirsToDelete = null;
        try
        {
            foreach (var account in Accounts)
            {
                account.AvitoResponsesUrl = FixedAvitoProfileUrl;

                if (account.ProfileProvider == AvitoProfileProvider.Local)
                {
                    if (string.IsNullOrWhiteSpace(account.BrowserProfilePath))
                    {
                        account.BrowserProfilePath = profileService.GetProfile(account).ProfilePath;
                    }
                }
                else
                {
                    account.BrowserProfilePath = string.Empty;
                }
            }

            var normalizedWebhookUrl = BitrixWebhookUrl?.Trim() ?? string.Empty;
            var normalizedMaxConcurrent = Math.Clamp(MaxConcurrentAccounts, 1, 10);
            var settingsChanged =
                !string.Equals(_settings.Bitrix.WebhookUrl, normalizedWebhookUrl, StringComparison.Ordinal)
                || _settings.MonitoringSafety.MaxConcurrentAccounts != normalizedMaxConcurrent;
            _settings.Bitrix.WebhookUrl = normalizedWebhookUrl;
            _settings.MonitoringSafety.MaxConcurrentAccounts = normalizedMaxConcurrent;
            MaxConcurrentAccounts = normalizedMaxConcurrent;

            var currentAccountIds = Accounts.Select(static account => account.Id).ToHashSet();
            var removedAccountIds = _persistedAccountIds
                .Where(id => !currentAccountIds.Contains(id))
                .ToArray();
            var accountsToPersist = Accounts
                .Where(account => _dirtyAccountIds.Contains(account.Id) || !_persistedAccountIds.Contains(account.Id))
                .ToList();
            var compactLegacySettingsFile = _settings.Avito.Accounts.Count > 0;

            if (removedAccountIds.Length > 0)
            {
                await repository.DeleteAccountsAsync(removedAccountIds, CancellationToken.None);
                _persistedAccountIds.ExceptWith(removedAccountIds);
            }

            if (accountsToPersist.Count > 0)
            {
                await repository.SaveSettingsSessionAccountsAsync(accountsToPersist, CancellationToken.None);
                foreach (var account in accountsToPersist)
                {
                    _persistedAccountIds.Add(account.Id);
                    _dirtyAccountIds.Remove(account.Id);
                }

                SessionPersistedChanges = true;
            }

            if (settingsChanged || compactLegacySettingsFile)
            {
                await settingsService.SaveAsync(_settings, CancellationToken.None);
                if (compactLegacySettingsFile)
                {
                    _settings.Avito.Accounts.Clear();
                }

                SessionPersistedChanges = true;
            }

            if (removedAccountIds.Length > 0)
            {
                SessionPersistedChanges = true;
            }

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
    public async Task OpenAccountSettingsAsync(Window? owner)
    {
        if (owner is null || SelectedAccount is null)
        {
            return;
        }

        var fullAccount = await repository.GetAccountByIdAsync(SelectedAccount.Id, CancellationToken.None);
        if (fullAccount is null)
        {
            return;
        }

        await windowService.ShowAccountSettingsAsync(owner, fullAccount, CancellationToken.None);
        await LoadAsync();
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
            ProfileProvider = AvitoProfileProvider.Local
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
            AttachAccountDirtyTracking(newAccount);
            Accounts.Add(newAccount);
            OnPropertyChanged(nameof(AccountsCount));
            _dirtyAccountIds.Add(newId);
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
        SelectedAccount is not null
        && SelectedAccount.ProfileProvider == AvitoProfileProvider.Local
        && !IsExportingProfile
        && !IsImportingProfile;

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

    /// <summary>Имя профиля, прочитанное со страницы Avito (показываем рядом с DisplayName, оба значения сохраняются).</summary>
    public string SelectedAccountAvitoProfileText =>
        string.IsNullOrWhiteSpace(SelectedAccount?.AvitoProfileName)
            ? string.Empty
            : $"Avito: {SelectedAccount!.AvitoProfileName}";

    public bool HasAvitoProfileName => !string.IsNullOrWhiteSpace(SelectedAccount?.AvitoProfileName);

    /// <summary>Краткая шапка над списком суб-профилей: «Профилей AdsPower: 10».</summary>
    public string SelectedAccountSubProfilesHeader =>
        SelectedAccount?.SubProfilesCount > 0
            ? $"Профилей AdsPower: {SelectedAccount.SubProfilesCount}"
            : string.Empty;

    public bool HasSubProfiles => SelectedAccount?.SubProfilesCount > 0;

    public IReadOnlyList<AvitoSubProfile> SelectedAccountSubProfiles =>
        SelectedAccount?.SubProfiles ?? Array.Empty<AvitoSubProfile>();

    public string SelectedAccountErrorText
    {
        get
        {
            var account = SelectedAccount;
            if (account is null)
            {
                return "Ошибок не зафиксировано";
            }

            if (account.HasSubProfileIssues)
            {
                return account.SubProfileIssuesSummary;
            }

            return string.IsNullOrWhiteSpace(account.LastErrorMessage)
                ? "Ошибок не зафиксировано"
                : account.LastErrorMessage;
        }
    }

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
        foreach (var account in Accounts.ToArray())
        {
            DetachAccountDirtyTracking(account);
        }

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

            _suspendDirtyTracking = true;
            try
            {
                local.MergePersistedSnapshotFrom(snapshot);
            }
            finally
            {
                _suspendDirtyTracking = false;
            }
        });
    }

    private void AttachAccountDirtyTracking(AvitoAccount account)
    {
        account.PropertyChanged -= AccountOnPropertyChanged;
        account.PropertyChanged += AccountOnPropertyChanged;
    }

    private void DetachAccountDirtyTracking(AvitoAccount account)
    {
        account.PropertyChanged -= AccountOnPropertyChanged;
        _dirtyAccountIds.Remove(account.Id);
    }

    private void AccountOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspendDirtyTracking || _isHydratingAccounts || sender is not AvitoAccount account)
        {
            return;
        }

        _dirtyAccountIds.Add(account.Id);
    }

    partial void OnSelectedAccountChanged(AvitoAccount? value)
    {
        DetachSelectedAccountPropertyListener();
        if (value is not null)
        {
            value.PropertyChanged += SelectedAccountOnPropertyChanged;
            _selectedAccountPropertySource = value;
        }

        OnPropertyChanged(nameof(HasSelectedAccount));
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
            _ = dispatcher.BeginInvoke(() =>
            {
                RefreshSelectedAccountPresentation(e.PropertyName);
                if (string.Equals(e.PropertyName, nameof(AvitoAccount.ProfileProvider), StringComparison.Ordinal))
                {
                    NotifyProfileArchiveAndAccountBrowserCommands();
                }
            });
            return;
        }

        RefreshSelectedAccountPresentation(e.PropertyName);
        if (string.Equals(e.PropertyName, nameof(AvitoAccount.ProfileProvider), StringComparison.Ordinal))
        {
            NotifyProfileArchiveAndAccountBrowserCommands();
        }
    }

    private void RefreshSelectedAccountPresentation(string? propertyName = null)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            OnPropertyChanged(nameof(SelectedAccountName));
            OnPropertyChanged(nameof(SelectedAccountStatusText));
            OnPropertyChanged(nameof(SelectedAccountStateHint));
            OnPropertyChanged(nameof(SelectedAccountToggleText));
            OnPropertyChanged(nameof(SelectedAccountAuthCheckText));
            OnPropertyChanged(nameof(SelectedAccountMonitoringText));
            OnPropertyChanged(nameof(SelectedAccountErrorText));
            OnPropertyChanged(nameof(SelectedAccountAvitoProfileText));
            OnPropertyChanged(nameof(HasAvitoProfileName));
            OnPropertyChanged(nameof(SelectedAccountSubProfilesHeader));
            OnPropertyChanged(nameof(HasSubProfiles));
            OnPropertyChanged(nameof(SelectedAccountSubProfiles));
            OnPropertyChanged(nameof(ShowAuthorizeButton));
            return;
        }

        switch (propertyName)
        {
            case nameof(AvitoAccount.DisplayName):
                OnPropertyChanged(nameof(SelectedAccountName));
                break;
            case nameof(AvitoAccount.Status):
                OnPropertyChanged(nameof(SelectedAccountStatusText));
                OnPropertyChanged(nameof(ShowAuthorizeButton));
                break;
            case nameof(AvitoAccount.IsEnabled):
                OnPropertyChanged(nameof(SelectedAccountStateHint));
                OnPropertyChanged(nameof(SelectedAccountToggleText));
                break;
            case nameof(AvitoAccount.LastAuthCheckAt):
                OnPropertyChanged(nameof(SelectedAccountAuthCheckText));
                break;
            case nameof(AvitoAccount.LastMonitoringAt):
                OnPropertyChanged(nameof(SelectedAccountMonitoringText));
                break;
            case nameof(AvitoAccount.LastErrorMessage):
                OnPropertyChanged(nameof(SelectedAccountErrorText));
                break;
            case nameof(AvitoAccount.AvitoProfileName):
                OnPropertyChanged(nameof(SelectedAccountAvitoProfileText));
                OnPropertyChanged(nameof(HasAvitoProfileName));
                break;
            case nameof(AvitoAccount.SubProfilesJson):
            case nameof(AvitoAccount.SubProfiles):
            case nameof(AvitoAccount.SubProfilesCount):
            case nameof(AvitoAccount.HasSubProfiles):
            case nameof(AvitoAccount.HasSubProfileIssues):
            case nameof(AvitoAccount.SubProfileIssuesSummary):
                OnPropertyChanged(nameof(SelectedAccountSubProfilesHeader));
                OnPropertyChanged(nameof(HasSubProfiles));
                OnPropertyChanged(nameof(SelectedAccountSubProfiles));
                OnPropertyChanged(nameof(SelectedAccountErrorText));
                break;
        }
    }

    private static string FormatDateTime(DateTime? value, string fallback) =>
        value.HasValue ? value.Value.ToLocalTimeFromStoredUtc().ToString("dd.MM.yyyy HH:mm") : fallback;

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
