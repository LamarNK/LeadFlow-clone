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
    IAvitoPageReaderService pageReaderService,
    ISettingsService settingsService,
    AppRepository repository) : ObservableObject
{
    private AvitoAccount? _account;
    private CancellationTokenSource? _authorizationMonitoringCts;
    private bool _authorizationPersisted;

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
        StopMonitoring();
        _account = account;
        _authorizationPersisted = false;
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
        StartMonitoring();
    }

    [RelayCommand]
    public async Task CheckAuthorizationAsync()
        => await CheckAuthorizationCoreAsync(CancellationToken.None, persistOnSuccessOnly: false);

    public void StopMonitoring()
    {
        _authorizationMonitoringCts?.Cancel();
        _authorizationMonitoringCts?.Dispose();
        _authorizationMonitoringCts = null;
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
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
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
        AuthorizationStatus = result.StatusMessage;

        _account.LastAuthCheckAt = DateTime.UtcNow;
        _account.BrowserProfilePath = Session.ProfilePath;
        _account.LastErrorMessage = string.Empty;

        if (result.IsAuthorized)
        {
            _account.Status = AvitoAccountStatus.Authorized;
            await PersistAccountAsync(settings, cancellationToken);
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
            await PersistAccountAsync(settings, cancellationToken);
        }
    }

    private async Task PersistAccountAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        var existing = settings.Avito.Accounts.FirstOrDefault(x => x.Id == _account.Id);
        if (existing is null)
        {
            settings.Avito.Accounts.Add(_account);
        }
        else if (!ReferenceEquals(existing, _account))
        {
            existing.DisplayName = _account.DisplayName;
            existing.AvitoResponsesUrl = _account.AvitoResponsesUrl;
            existing.BrowserProfilePath = _account.BrowserProfilePath;
            existing.IsEnabled = _account.IsEnabled;
            existing.Status = _account.Status;
            existing.LastAuthCheckAt = _account.LastAuthCheckAt;
            existing.LastMonitoringAt = _account.LastMonitoringAt;
            existing.LastErrorMessage = _account.LastErrorMessage;
        }

        await repository.SaveAccountAsync(_account, cancellationToken);
        await settingsService.SaveAsync(settings, cancellationToken);
    }
}
