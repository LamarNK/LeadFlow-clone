using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Browser;

namespace LeadFlow.ViewModels;

public partial class AvitoAuthViewModel(
    IBrowserSessionService browserSessionService,
    IProfileCookiesService profileCookiesService,
    IAvitoPageReaderService pageReaderService,
    ISettingsService settingsService,
    AppRepository repository) : ObservableObject
{
    private AvitoAccount? _account;
    private CancellationTokenSource? _authorizationMonitoringCts;
    private bool _authorizationPersisted;
    private bool _monitorAuthorization;
    private BrowserAccountSession? _subscribedSession;

    [ObservableProperty]
    private string accountName = string.Empty;

    [ObservableProperty]
    private string windowTitle = "Авторизация Avito";

    [ObservableProperty]
    private string currentUrl = string.Empty;

    [ObservableProperty]
    private string addressBarUrl = string.Empty;

    [ObservableProperty]
    private string authorizationStatus = "Ожидание";

    [ObservableProperty]
    private BrowserAccountSession? session;

    /// <summary>Видимость WebView2 этой вкладки (перекрывающиеся контролы в одном окне).</summary>
    [ObservableProperty]
    private bool isActiveTab;

    /// <summary>Идентификатор аккаунта вкладки (поиск уже открытой вкладки).</summary>
    public Guid? AccountKey => _account?.Id;

    public void ConfigureForAuthorization(AvitoAccount account)
    {
        Configure(account, true);
    }

    public void ConfigureForProfile(AvitoAccount account)
    {
        Configure(account, false);
    }

    public void ConfigureForProfile(AvitoAccount account, string initialUrl)
    {
        Configure(account, false, initialUrl);
    }

    private void Configure(AvitoAccount account, bool monitorAuthorization)
    {
        Configure(account, monitorAuthorization, null);
    }

    private void Configure(AvitoAccount account, bool monitorAuthorization, string? initialUrl)
    {
        StopMonitoring();
        _account = account;
        _monitorAuthorization = monitorAuthorization;
        _authorizationPersisted = false;
        AccountName = account.DisplayName;
        var startUrl = string.IsNullOrWhiteSpace(initialUrl) ? account.AvitoResponsesUrl : initialUrl;
        CurrentUrl = startUrl;
        AddressBarUrl = startUrl;
        WindowTitle = monitorAuthorization
            ? $"Авторизация Avito — {account.DisplayName}"
            : $"Avito — {account.DisplayName}";
        AuthorizationStatus = monitorAuthorization
            ? "Ожидание"
            : "Открываем окно Avito с сохранённым браузерным профилем аккаунта.";
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
        Session.CurrentUrl = CurrentUrl;
        CurrentUrl = Session.CurrentUrl;
        AddressBarUrl = Session.CurrentUrl;
        if (_monitorAuthorization)
        {
            AuthorizationStatus = "Войдите в аккаунт Авито вручную. После успешного входа профиль будет сохранён.";
            StartMonitoring();
        }
        else
        {
            AuthorizationStatus = "Окно Avito открыто. Используется отдельный браузерный профиль выбранного аккаунта.";
        }
    }

    [RelayCommand]
    public async Task CheckAuthorizationAsync()
        => await CheckAuthorizationCoreAsync(CancellationToken.None, persistOnSuccessOnly: false);

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    public void NavigateBack()
    {
        Session?.GoBack();
        SyncAddressFromSession();
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    public void NavigateForward()
    {
        Session?.GoForward();
        SyncAddressFromSession();
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public void ReloadPage()
    {
        Session?.Reload();
        SyncAddressFromSession();
    }

    [RelayCommand]
    public void NavigateToAddress()
    {
        if (string.IsNullOrWhiteSpace(AddressBarUrl))
        {
            return;
        }

        Session?.Navigate(AddressBarUrl);
        SyncAddressFromSession();
    }

    /// <summary>Переход по URL извне (например, та же вкладка выбрана повторно из мониторинга).</summary>
    public void ApplyExternalNavigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        AddressBarUrl = url.Trim();
        CurrentUrl = AddressBarUrl;
        if (Session is not null)
        {
            Session.CurrentUrl = CurrentUrl;
        }

        NavigateToAddress();
    }

    public bool CanNavigateBack() => Session?.CanGoBack == true;

    public bool CanNavigateForward() => Session?.CanGoForward == true;

    public void StopMonitoring()
    {
        _authorizationMonitoringCts?.Cancel();
        _authorizationMonitoringCts?.Dispose();
        _authorizationMonitoringCts = null;
    }

    /// <summary>Закрывает мониторинг и отключает WebView2 (только с UI-потока).</summary>
    public void ReleaseBrowser()
    {
        StopMonitoring();
        if (Session is null)
        {
            return;
        }

        Session.DisposeWebView();
        Session = null;
    }

    private void StartMonitoring()
    {
        StopMonitoring();
        _authorizationMonitoringCts = new CancellationTokenSource();
        _ = MonitorAuthorizationAsync(_authorizationMonitoringCts.Token);
    }

    private async Task MonitorAuthorizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CheckAuthorizationCoreAsync(cancellationToken, persistOnSuccessOnly: true);
                if (_authorizationPersisted)
                {
                    StopMonitoring();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAuthorizationCoreAsync(CancellationToken cancellationToken, bool persistOnSuccessOnly)
    {
        if (Session is null || _account is null)
        {
            return;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var result = await pageReaderService.CheckAuthorizationAsync(Session, settings.AvitoSelectors, cancellationToken);
        CurrentUrl = result.CurrentUrl;
        AddressBarUrl = result.CurrentUrl;
        AuthorizationStatus = result.StatusMessage;

        _account.LastAuthCheckAt = DateTime.UtcNow;
        _account.BrowserProfilePath = Session.ProfilePath;
        _account.LastErrorMessage = string.Empty;

        if (result.IsAuthorized)
        {
            if (!string.IsNullOrWhiteSpace(result.ProfileName))
            {
                _account.DisplayName = result.ProfileName.Trim();
                AccountName = _account.DisplayName;
            }

            _account.Status = AvitoAccountStatus.Authorized;
            _account.CookiesJson = await profileCookiesService.ReadCurrentProfileCookiesAsJsonAsync(_account, cancellationToken);
            _account.ImportCookiesOnNextStart = false;
            await PersistAccountAsync(cancellationToken);
            _authorizationPersisted = true;
            AuthorizationStatus = $"Авторизация успешна. Профиль сохранён: {Session.ProfilePath}";
            return;
        }

        if (result.RequiresManualAction)
        {
            _account.Status = AvitoAccountStatus.RequiresManualAction;
            _account.LastErrorMessage = result.StatusMessage;
        }
        else
        {
            _account.Status = AvitoAccountStatus.RequiresLogin;
            _account.LastErrorMessage = result.StatusMessage;
        }

        if (!persistOnSuccessOnly)
        {
            await PersistAccountAsync(cancellationToken);
        }
    }

    private async Task PersistAccountAsync(CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        await repository.SaveAccountAsync(_account, cancellationToken);
    }

    partial void OnSessionChanged(BrowserAccountSession? value)
    {
        if (_subscribedSession is not null)
        {
            _subscribedSession.PropertyChanged -= OnSessionPropertyChanged;
        }

        _subscribedSession = value;

        if (_subscribedSession is not null)
        {
            _subscribedSession.PropertyChanged += OnSessionPropertyChanged;
            SyncAddressFromSession();
        }

        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsActiveTabChanged(bool value)
    {
        if (!value || Session is null || Session.IsInitialized)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(RetrySessionBindingForWebView, DispatcherPriority.Loaded);
    }

    /// <summary>Повторяет привязку Session → WebView2, если вкладка стала активной до завершения инициализации.</summary>
    private void RetrySessionBindingForWebView()
    {
        if (!IsActiveTab || Session is null || Session.IsInitialized)
        {
            return;
        }

        var s = Session;
        Session = null;
        Session = s;
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserAccountSession.CurrentUrl))
        {
            SyncAddressFromSession();
        }

        if (e.PropertyName is nameof(BrowserAccountSession.CurrentUrl)
            or nameof(BrowserAccountSession.CanGoBack)
            or nameof(BrowserAccountSession.CanGoForward))
        {
            NavigateBackCommand.NotifyCanExecuteChanged();
            NavigateForwardCommand.NotifyCanExecuteChanged();
        }
    }

    private void SyncAddressFromSession()
    {
        if (Session is null)
        {
            return;
        }

        CurrentUrl = Session.CurrentUrl;
        AddressBarUrl = Session.CurrentUrl;
    }
}
